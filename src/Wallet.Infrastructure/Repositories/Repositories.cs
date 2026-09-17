using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure.Repositories;

public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        return new EfTransaction(tx);
    }
    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
    public Task<int> ExecuteSqlInterpolatedAsync(FormattableString sql, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(sql, ct);
}

public sealed class EfTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) : ITransaction
{
    public Task CommitAsync(CancellationToken ct) => tx.CommitAsync(ct);
    public Task RollbackAsync(CancellationToken ct) => tx.RollbackAsync(ct);
    public ValueTask DisposeAsync() => tx.DisposeAsync();
}

public sealed class WalletRepository(AppDbContext db) : IWalletRepository
{
    public Task<Wallet?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    public Task<Wallet?> GetByIdForUpdateAsync(Guid id, CancellationToken ct) =>
        db.Wallets.FromSqlInterpolated($"SELECT * FROM Wallets WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = {id}").SingleOrDefaultAsync(ct);

    public Task<Wallet?> GetByAccountNumberAsync(string accountNumber, CancellationToken ct) =>
        db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.AccountNumber == accountNumber, ct);

    public Task<Wallet?> GetBySystemKeyForUpdateAsync(string systemKey, CancellationToken ct) =>
        db.Wallets.FromSqlInterpolated($"SELECT * FROM Wallets WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE SystemKey = {systemKey}").SingleOrDefaultAsync(ct);

    public Task<List<Wallet>> GetCustomersAsync(CancellationToken ct) =>
        db.Wallets.Where(x => x.AccountType == AccountType.Customer).ToListAsync(ct);

    public async Task<long> GetCustomerSumBalanceAsync(CancellationToken ct) =>
        await db.Wallets.Where(x => x.AccountType == AccountType.Customer).SumAsync(x => (long?)x.BalanceKobo, ct) ?? 0;

    public async Task<long> GetSystemAccountBalanceAsync(string systemKey, CancellationToken ct) =>
        await db.Wallets.Where(x => x.SystemKey == systemKey).Select(x => x.BalanceKobo).SingleAsync(ct);

    public async Task<Wallet> AddAsync(Wallet wallet, CancellationToken ct)
    {
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(ct);
        return wallet;
    }
}

public sealed class TransferRepository(AppDbContext db) : ITransferRepository
{
    public Task<Transfer?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Transfers.SingleOrDefaultAsync(x => x.Id == id, ct);

    public Task<Transfer?> GetByIdAndCustomerAsync(Guid id, string customerId, CancellationToken ct) =>
        db.Transfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, ct);

    public Task<List<Transfer>> GetSettledOutboundForReconciliationAsync(DateOnly dayStart, DateOnly dayEnd, CancellationToken ct)
    {
        var startUtc = WatBusinessDay.StartOfWatDayUtc(dayStart);
        var endUtc = WatBusinessDay.StartOfWatDayUtc(dayEnd);
        return db.Transfers
            .Where(x => x.Type == TransferType.Outbound && x.Status == TransferStatus.Settled && x.ReconciledAt == null
                && x.CreatedAt >= startUtc && x.CreatedAt < endUtc)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
    }

    public Task<List<Transfer>> GetUnknownOutboundAsync(int take, CancellationToken ct) =>
        db.Transfers.Where(x => x.Type == TransferType.Outbound && x.Status == TransferStatus.Unknown && x.ExternalReference != null)
            .OrderBy(x => x.UpdatedAt).Take(take).ToListAsync(ct);

    public async Task<long> GetDailyOutboundTotalAsync(string customerId, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken ct) =>
        await db.Transfers.Where(x => x.CustomerId == customerId && x.Status != TransferStatus.Failed
            && x.CreatedAt >= dayStart && x.CreatedAt < dayEnd).SumAsync(x => (long?)x.AmountKobo, ct) ?? 0;

    public Task<int> CountByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        db.Transfers.CountAsync(x => x.IdempotencyKey == key, ct);

    public Task<IdempotencyRecord?> GetIdempotencyRecordAsync(string customerId, string key, CancellationToken ct) =>
        db.IdempotencyRecords.SingleOrDefaultAsync(x => x.CustomerId == customerId && x.Key == key, ct);

    public Task AddAsync(Transfer transfer, CancellationToken ct) { db.Transfers.Add(transfer); return Task.CompletedTask; }
    public Task AddIdempotencyRecordAsync(IdempotencyRecord record, CancellationToken ct) { db.IdempotencyRecords.Add(record); return Task.CompletedTask; }
}

public sealed class LedgerEntryRepository(AppDbContext db) : ILedgerEntryRepository
{
    public async Task<List<StatementItem>> GetStatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return await db.LedgerEntries.AsNoTracking()
            .Where(x => x.WalletId == walletId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new StatementItem(x.TransferId, x.WalletId, x.Direction, x.AmountKobo, x.Leg, x.CreatedAt))
            .ToListAsync(ct);
    }
    public Task AddAsync(LedgerEntry entry, CancellationToken ct) { db.LedgerEntries.Add(entry); return Task.CompletedTask; }
}

public sealed class AuditLogRepository(AppDbContext db) : IAuditLogRepository
{
    public Task AddAsync(AuditLog entry, CancellationToken ct) { db.AuditLogs.Add(entry); return Task.CompletedTask; }
    public async Task<long> GetMaxIdAsync(CancellationToken ct) => await db.AuditLogs.Select(x => x.Id).MaxAsync(ct);
}

public sealed class OutboxRepository(AppDbContext db) : IOutboxRepository
{
    public Task<List<OutboxMessage>> GetUnpublishedAsync(int take, CancellationToken ct) =>
        db.OutboxMessages.Where(x => x.PublishedAt == null).OrderBy(x => x.Id).Take(take).ToListAsync(ct);
    public Task AddAsync(OutboxMessage message, CancellationToken ct) { db.OutboxMessages.Add(message); return Task.CompletedTask; }
}

public sealed class SettlementJobRepository(AppDbContext db) : ISettlementJobRepository
{
    public Task<SettlementJob?> GetByTransferIdAsync(Guid transferId, CancellationToken ct) =>
        db.SettlementJobs.SingleOrDefaultAsync(x => x.TransferId == transferId, ct);

    public Task<List<SettlementJob>> GetClaimableAsync(DateTimeOffset now, int take, CancellationToken ct) =>
        db.SettlementJobs.Where(x => x.Status == SettlementStatus.Pending
            || (x.Status == SettlementStatus.Failed && x.NextAttemptAt <= now)
            || (x.Status == SettlementStatus.Processing && x.LeaseUntil != null && x.LeaseUntil < now))
            .OrderBy(x => x.Id).Take(take).ToListAsync(ct);

    public Task AddAsync(SettlementJob job, CancellationToken ct) { db.SettlementJobs.Add(job); return Task.CompletedTask; }
}

public sealed class ExternalAccountRepository(AppDbContext db) : IExternalAccountRepository
{
    public Task<ExternalAccount?> GetByKeyAsync(string key, CancellationToken ct) =>
        db.ExternalAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.AccountKey == key, ct);

    public Task<ExternalAccount?> GetByKeyForUpdateAsync(string key, CancellationToken ct) =>
        db.ExternalAccounts.FromSqlInterpolated($"SELECT * FROM ExternalAccounts WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE AccountKey = {key}").SingleOrDefaultAsync(ct);

    public Task<List<ExternalAccount>> GetAllAsync(CancellationToken ct) => db.ExternalAccounts.AsNoTracking().ToListAsync(ct);
    public async Task<long> GetBalanceByKeyAsync(string key, CancellationToken ct) =>
        await db.ExternalAccounts.Where(x => x.AccountKey == key).Select(x => x.BalanceKobo).SingleAsync(ct);
    public async Task AddAsync(ExternalAccount account, CancellationToken ct) { db.ExternalAccounts.Add(account); await db.SaveChangesAsync(ct); }
    public Task<bool> AnyByKeyAsync(string key, CancellationToken ct) => db.ExternalAccounts.AnyAsync(x => x.AccountKey == key, ct);
}

public sealed class ReconciliationRepository(AppDbContext db) : IReconciliationRepository
{
    public Task<ReconciliationReport?> GetByWatDateAsync(DateOnly watDate, CancellationToken ct) =>
        db.ReconciliationReports.AsNoTracking().SingleOrDefaultAsync(x => x.WatDate == watDate, ct);

    public async Task<List<ReconciliationReport>> GetPagedAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return await db.ReconciliationReports.AsNoTracking()
            .OrderByDescending(x => x.WatDate).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    }

    public Task<bool> ExistsForWatDateAsync(DateOnly watDate, CancellationToken ct) =>
        db.ReconciliationReports.AsNoTracking().AnyAsync(x => x.WatDate == watDate, ct);

    public Task AddAsync(ReconciliationReport report, CancellationToken ct) { db.ReconciliationReports.Add(report); return Task.CompletedTask; }

    public async Task<bool> HasSettledOutboundAsync(DateOnly dayStart, DateOnly dayEnd, CancellationToken ct)
    {
        var startUtc = WatBusinessDay.StartOfWatDayUtc(dayStart);
        var endUtc = WatBusinessDay.StartOfWatDayUtc(dayEnd);
        return await db.Transfers.AnyAsync(x => x.Type == TransferType.Outbound && x.Status == TransferStatus.Settled
            && x.ReconciledAt == null && x.CreatedAt >= startUtc && x.CreatedAt < endUtc, ct);
    }
}

public sealed class DailyOutboundCounterRepository(AppDbContext db) : IDailyOutboundCounterRepository
{
    public Task<int> MergeCounterAsync(Guid walletId, DateOnly watDate, long amount, long limit, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            MERGE DailyOutboundCounters WITH (HOLDLOCK) AS t
            USING (VALUES ({walletId}, {watDate}, {amount}, {limit}, {now})) AS s(WalletId, WatDate, Amount, LimitKobo, Now)
            ON t.WalletId = s.WalletId AND t.WatDate = s.WatDate
            WHEN MATCHED AND t.TotalKobo + s.Amount <= s.LimitKobo THEN
                UPDATE SET t.TotalKobo = t.TotalKobo + s.Amount, t.UpdatedAt = s.Now
            WHEN NOT MATCHED AND s.Amount <= s.LimitKobo THEN
                INSERT (WalletId, WatDate, TotalKobo, UpdatedAt) VALUES (s.WalletId, s.WatDate, s.Amount, s.Now);
            """, ct);
}
