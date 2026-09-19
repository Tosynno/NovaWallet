using System.Text.Json;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public sealed class ReconciliationService(
    IUnitOfWork uow,
    IReconciliationRepository reports,
    ITransferRepository transfers,
    IWalletRepository wallets,
    IExternalAccountRepository externalAccounts,
    IAuditLogRepository auditLogs,
    IClock clock) : IReconciliationService
{
    public async Task<ReconciliationReportResult> RunReconciliationAsync(DateOnly? watDate, string runBy, CancellationToken ct)
    {
        var day = watDate ?? WatBusinessDay.Today(clock.UtcNow);
        var now = clock.UtcNow;

        var existing = await reports.GetByWatDateAsync(day, ct);
        if (existing is not null) return ToResult(existing);

        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var report = ReconciliationReport.Create(day, runBy, now);
            await reports.AddAsync(report, ct);
            await uow.SaveChangesAsync(ct);

            var settledOutbound = await transfers.GetSettledOutboundForReconciliationAsync(day, day.AddDays(1), ct);
            var settledInbound = await transfers.GetSettledInboundForReconciliationAsync(day, day.AddDays(1), ct);

            long totalAmount = 0, totalFee = 0, totalVat = 0;

            foreach (var transfer in settledOutbound)
            {
                totalAmount += transfer.AmountKobo;
                totalFee += transfer.FeeKobo;
                totalVat += transfer.VatKobo;

                var extSettlement = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.SettlementHolding, ct)
                    ?? throw new DomainException("external_account.missing", "Settlement holding account is not seeded.");
                extSettlement.Credit(Money.Create(transfer.AmountKobo), now);

                var extIncome = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.IncomeHolding, ct)
                    ?? throw new DomainException("external_account.missing", "Income holding account is not seeded.");
                extIncome.Credit(Money.Create(transfer.FeeKobo), now);

                var extVat = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.VatHolding, ct)
                    ?? throw new DomainException("external_account.missing", "VAT holding account is not seeded.");
                extVat.Credit(Money.Create(transfer.VatKobo), now);

                var extLedger = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.LedgerHolding, ct)
                    ?? throw new DomainException("external_account.missing", "Ledger holding account is not seeded.");
                extLedger.Debit(Money.Create(transfer.TotalDebitKobo), now);

                transfer.MarkReconciled(now);

                await auditLogs.AddAsync(AuditLog.Create("SYSTEM", "transfer.reconciled", "Transfer", transfer.Id.ToString(),
                    JsonSerializer.Serialize(new { transfer.Status, Reconciled = false }),
                    JsonSerializer.Serialize(new { transfer.Status, Reconciled = true }),
                    transfer.CorrelationId, now), ct);
            }

            foreach (var transfer in settledInbound)
            {
                var extSettlement = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.SettlementHolding, ct)
                    ?? throw new DomainException("external_account.missing", "Settlement holding account is not seeded.");
                extSettlement.Debit(Money.Create(transfer.AmountKobo), now);

                var extLedger = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.LedgerHolding, ct)
                    ?? throw new DomainException("external_account.missing", "Ledger holding account is not seeded.");
                extLedger.Credit(Money.Create(transfer.AmountKobo), now);

                transfer.MarkReconciled(now);

                await auditLogs.AddAsync(AuditLog.Create("SYSTEM", "transfer.reconciled", "Transfer", transfer.Id.ToString(),
                    JsonSerializer.Serialize(new { transfer.Status, Reconciled = false }),
                    JsonSerializer.Serialize(new { transfer.Status, Reconciled = true }),
                    transfer.CorrelationId, now), ct);
            }

            await uow.SaveChangesAsync(ct);

            report.RecordOutboundTotals(totalAmount, totalFee, totalVat, settledOutbound.Count);

            var customerSum = await wallets.GetCustomerSumBalanceAsync(ct);
            var settlementSys = await wallets.GetSystemAccountBalanceAsync(SystemAccountKeys.Settlement, ct);
            var incomeSys = await wallets.GetSystemAccountBalanceAsync(SystemAccountKeys.Income, ct);
            var vatSys = await wallets.GetSystemAccountBalanceAsync(SystemAccountKeys.Vat, ct);

            var extLedger = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.LedgerHolding, ct)
                ?? throw new DomainException("external_account.missing", "Ledger holding account is not seeded.");
            var extSettlement = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.SettlementHolding, ct)
                ?? throw new DomainException("external_account.missing", "Settlement holding account is not seeded.");

            var ledgerDiff = customerSum - extLedger.BalanceKobo;
            if (ledgerDiff > 0) extLedger.Credit(Money.Create(ledgerDiff), now);
            else if (ledgerDiff < 0) extLedger.Debit(Money.Create(-ledgerDiff), now);

            var settlementDiff = settlementSys - extSettlement.BalanceKobo;
            if (settlementDiff > 0) extSettlement.Credit(Money.Create(settlementDiff), now);
            else if (settlementDiff < 0) extSettlement.Debit(Money.Create(-settlementDiff), now);

            await uow.SaveChangesAsync(ct);

            var extLedgerBal = extLedger.BalanceKobo;
            var extSettlementBal = extSettlement.BalanceKobo;
            var extIncomeBal = await externalAccounts.GetBalanceByKeyAsync(ExternalAccountKeys.IncomeHolding, ct);
            var extVatBal = await externalAccounts.GetBalanceByKeyAsync(ExternalAccountKeys.VatHolding, ct);

            report.RecordAccount(ExternalAccountKeys.LedgerHolding, customerSum, extLedgerBal);
            report.RecordAccount(ExternalAccountKeys.SettlementHolding, settlementSys, extSettlementBal);
            report.RecordAccount(ExternalAccountKeys.IncomeHolding, incomeSys, extIncomeBal);
            report.RecordAccount(ExternalAccountKeys.VatHolding, vatSys, extVatBal);
            report.Complete(now);

            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToResult(report);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<ReconciliationReportResult?> GetReconciliationAsync(DateOnly watDate, CancellationToken ct)
    {
        var report = await reports.GetByWatDateAsync(watDate, ct);
        return report is null ? null : ToResult(report);
    }

    public async Task<IReadOnlyList<ReconciliationReportSummary>> ListReconciliationsAsync(int page, int pageSize, CancellationToken ct)
    {
        var list = await reports.GetPagedAsync(page, pageSize, ct);
        return [.. list.Select(x => new ReconciliationReportSummary(x.Id, x.WatDate, x.Status, x.TotalOutboundAmountKobo, x.TotalOutboundCount, x.CreatedAt))];
    }

    private static ReconciliationReportResult ToResult(ReconciliationReport r) =>
        new(r.Id, r.WatDate, r.Status,
            r.TotalOutboundAmountKobo, r.TotalOutboundFeeKobo, r.TotalOutboundVatKobo, r.TotalOutboundCount,
            r.LedgerHoldingExpectedKobo, r.LedgerHoldingActualKobo, r.LedgerDiscrepancyKobo,
            r.SettlementHoldingExpectedKobo, r.SettlementHoldingActualKobo, r.SettlementDiscrepancyKobo,
            r.IncomeHoldingExpectedKobo, r.IncomeHoldingActualKobo, r.IncomeDiscrepancyKobo,
            r.VatHoldingExpectedKobo, r.VatHoldingActualKobo, r.VatDiscrepancyKobo,
            r.Notes, r.RunBy, r.CreatedAt, r.CompletedAt);
}
