using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Repositories;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("LedgerDb")));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IPaymentRail, MockNipPaymentRail>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<ITransferRepository, TransferRepository>();
builder.Services.AddScoped<ILedgerEntryRepository, LedgerEntryRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
builder.Services.AddScoped<ISettlementJobRepository, SettlementJobRepository>();
builder.Services.AddScoped<IExternalAccountRepository, ExternalAccountRepository>();
builder.Services.AddScoped<IReconciliationRepository, ReconciliationRepository>();
builder.Services.AddScoped<IDailyOutboundCounterRepository, DailyOutboundCounterRepository>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>();
builder.Services.AddHostedService<SettlementBackgroundService>();
builder.Services.AddHostedService<ReconciliationBackgroundService>();
var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), CancellationToken.None);
}
await host.RunAsync();

public sealed class SettlementBackgroundService(IServiceScopeFactory scopeFactory, ILogger<SettlementBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(60);
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Settlement cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rail = scope.ServiceProvider.GetRequiredService<IPaymentRail>();
        var now = DateTimeOffset.UtcNow;

        var unpublished = await db.OutboxMessages.Where(x => x.PublishedAt == null).OrderBy(x => x.Id).Take(50).ToListAsync(ct);
        foreach (var message in unpublished) message.MarkPublished(now);
        if (unpublished.Count > 0) await db.SaveChangesAsync(ct);

        var claimable = await db.SettlementJobs
            .Where(x => x.Status == SettlementStatus.Pending
                || (x.Status == SettlementStatus.Failed && x.NextAttemptAt <= now)
                || (x.Status == SettlementStatus.Processing && x.LeaseUntil != null && x.LeaseUntil < now))
            .OrderBy(x => x.Id).Take(20).ToListAsync(ct);

        foreach (var job in claimable)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                job.Claim(now, Lease);
                await db.SaveChangesAsync(ct);

                var transfer = await db.Transfers.SingleAsync(x => x.Id == job.TransferId, ct);
                if (transfer.Status is TransferStatus.Failed or TransferStatus.Settled)
                {
                    job.Succeed("NOOP-ALREADY-RESOLVED", now);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    continue;
                }

                var instruction = new PaymentInstruction(transfer.Id, transfer.Reference,
                    transfer.DestinationAccountNumber!, transfer.DestinationBankCode!,
                    transfer.DestinationAccountName ?? "", transfer.AmountKobo);

                var railResult = await rail.SendAsync(instruction, ct);

                if (railResult.Status == PaymentRailStatus.Sent)
                {
                    transfer.SetExternalReference(railResult.ExternalReference!, now);
                    transfer.MarkSettled(now);
                    job.Succeed(railResult.ExternalReference!, now);
                }
                else if (railResult.Status == PaymentRailStatus.Failed)
                {
                    var deadLetter = job.AttemptCount >= MaxAttempts;
                    job.Fail(railResult.Error ?? "NIP dispatch failed", now, Backoff, deadLetter);
                    await ReverseOutboundAsync(db, transfer, now, ct);
                    transfer.MarkFailed(now);
                }
                else
                {
                    if (railResult.ExternalReference is not null)
                        transfer.SetExternalReference(railResult.ExternalReference, now);
                    transfer.MarkUnknown(now);
                    job.Fail("NIP response indeterminate; status query required", now, Backoff, deadLetter: false);
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogWarning(ex, "Settlement job {TransferId} failed this cycle; lease will release", job.TransferId);
            }
        }

        var unknowns = await db.Transfers
            .Where(x => x.Type == TransferType.Outbound && x.Status == TransferStatus.Unknown && x.ExternalReference != null)
            .OrderBy(x => x.UpdatedAt).Take(20).ToListAsync(ct);

        foreach (var transfer in unknowns)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var nipStatus = await rail.GetStatusAsync(transfer.ExternalReference!, ct);

                if (nipStatus.Status == PaymentRailStatus.Sent)
                {
                    transfer.MarkSettled(now);
                    var job = await db.SettlementJobs.SingleOrDefaultAsync(x => x.TransferId == transfer.Id, ct);
                    if (job is not null) job.Succeed(transfer.ExternalReference!, now);
                }
                else if (nipStatus.Status == PaymentRailStatus.Failed)
                {
                    transfer.MarkFailed(now);
                    await ReverseOutboundAsync(db, transfer, now, ct);
                    var job = await db.SettlementJobs.SingleOrDefaultAsync(x => x.TransferId == transfer.Id, ct);
                    if (job is not null) job.Fail("NIP status query: failed", now, Backoff, job.AttemptCount >= MaxAttempts);
                }

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogWarning(ex, "Status query for transfer {TransferId} failed this cycle", transfer.Id);
            }
        }
    }

    private static async Task ReverseOutboundAsync(AppDbContext db, Transfer transfer, DateTimeOffset now, CancellationToken ct)
    {
        var source = await db.Wallets.SingleAsync(x => x.Id == transfer.SourceWalletId, ct);
        var settlement = await db.Wallets.SingleAsync(x => x.SystemKey == SystemAccountKeys.Settlement, ct);
        var income = await db.Wallets.SingleAsync(x => x.SystemKey == SystemAccountKeys.Income, ct);
        var vat = await db.Wallets.SingleAsync(x => x.SystemKey == SystemAccountKeys.Vat, ct);

        source.Credit(Money.Create(transfer.TotalDebitKobo), now);
        settlement.Debit(Money.Create(transfer.AmountKobo), now);
        income.Debit(Money.Create(transfer.FeeKobo), now);
        vat.Debit(Money.Create(transfer.VatKobo), now);

        db.LedgerEntries.Add(LedgerEntry.Create(transfer.Id, source.Id, LedgerDirection.Credit, Money.Create(transfer.TotalDebitKobo), "ReversalSource", now));
        db.LedgerEntries.Add(LedgerEntry.Create(transfer.Id, settlement.Id, LedgerDirection.Debit, Money.Create(transfer.AmountKobo), "ReversalSettlement", now));
        db.LedgerEntries.Add(LedgerEntry.Create(transfer.Id, income.Id, LedgerDirection.Debit, Money.Create(transfer.FeeKobo), "ReversalIncome", now));
        db.LedgerEntries.Add(LedgerEntry.Create(transfer.Id, vat.Id, LedgerDirection.Debit, Money.Create(transfer.VatKobo), "ReversalVat", now));
        db.AuditLogs.Add(AuditLog.Create("SYSTEM", "transfer.outbound.reversed", "Transfer", transfer.Id.ToString(),
            JsonSerializer.Serialize(new { transfer.Status }),
            JsonSerializer.Serialize(new { Status = nameof(TransferStatus.Failed) }),
            transfer.CorrelationId, now));
    }
}

public sealed class ReconciliationBackgroundService(IServiceScopeFactory scopeFactory, ILogger<ReconciliationBackgroundService> logger) : BackgroundService
{
    private DateOnly? _lastReconciledDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TryRunEodReconciliationAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Reconciliation cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task TryRunEodReconciliationAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var reconService = scope.ServiceProvider.GetRequiredService<IReconciliationService>();

        var currentWatDay = WatBusinessDay.Today(clock.UtcNow);
        var dayToReconcile = currentWatDay.AddDays(-1);
        if (_lastReconciledDay == dayToReconcile) return;

        var exists = await db.ReconciliationReports.AsNoTracking().AnyAsync(x => x.WatDate == dayToReconcile, ct);
        if (exists) { _lastReconciledDay = dayToReconcile; return; }

        var dayStart = WatBusinessDay.StartOfWatDayUtc(dayToReconcile);
        var dayEnd = WatBusinessDay.StartOfWatDayUtc(currentWatDay);
        var hasSettled = await db.Transfers.AnyAsync(x =>
            x.Type == TransferType.Outbound && x.Status == TransferStatus.Settled && x.ReconciledAt == null
            && x.CreatedAt >= dayStart && x.CreatedAt < dayEnd, ct);

        if (!hasSettled) return;

        logger.LogInformation("Running EOD reconciliation for WAT day {WatDate}", dayToReconcile);
        var result = await reconService.RunReconciliationAsync(dayToReconcile, "SYSTEM-EOD", ct);
        _lastReconciledDay = dayToReconcile;
        logger.LogInformation("Reconciliation for {WatDate}: {Status} (outbound count={Count}, amount={Amount} kobo)",
            result.WatDate, result.Status, result.TotalOutboundCount, result.TotalOutboundAmountKobo);
    }
}
