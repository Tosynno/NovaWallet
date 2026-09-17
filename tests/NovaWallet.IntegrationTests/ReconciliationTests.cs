using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;
using Xunit;

namespace NovaWallet.IntegrationTests;

public sealed class ReconciliationTests
{
    private static readonly FeePolicy Fees = new(50_000, 0.075m);

    [Fact]
    public async Task Reconciliation_after_outbound_settlement_balances_all_holding_accounts()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();

        var clock = new SystemClock();

        var walletService = TestDb.CreateWalletService(db.Db, clock);
        var transferService = TestDb.CreateTransferService(db.Db, clock, Fees);
        var reconService = TestDb.CreateReconciliationService(db.Db, clock);

        var customer = await walletService.CreateAsync("recon-cust", CancellationToken.None);
        var walletId = customer.Id;

        await walletService.CreditAsync(walletId, 1_000_000, "tester", "corr-1", CancellationToken.None);

        var result = await transferService.TransferOutboundAsync(
            new OutboundTransferCommand("recon-cust", walletId, "9999999999", "058", "Jane Doe", 100_000, "recon-key-1", "corr-2"),
            CancellationToken.None);

        var transferId = result.TransferId;
        var job = await db.Db.SettlementJobs.SingleAsync(x => x.TransferId == transferId);
        job.Succeed("SIM-PROVIDER-1", clock.UtcNow);
        var transfer = await db.Db.Transfers.SingleAsync(x => x.Id == transferId);
        transfer.MarkSettled(clock.UtcNow);
        await db.Db.SaveChangesAsync();

        var watDate = WatBusinessDay.Today(clock.UtcNow);
        var report = await reconService.RunReconciliationAsync(watDate, "admin-1", CancellationToken.None);

        Assert.Equal(ReconciliationStatus.Balanced, report.Status);
        Assert.Equal(100_000, report.TotalOutboundAmountKobo);
        Assert.Equal(50_000, report.TotalOutboundFeeKobo);
        Assert.Equal(3_750, report.TotalOutboundVatKobo);
        Assert.Equal(1, report.TotalOutboundCount);

        Assert.Equal(0, report.LedgerDiscrepancyKobo);
        Assert.Equal(0, report.SettlementDiscrepancyKobo);
        Assert.Equal(0, report.IncomeDiscrepancyKobo);
        Assert.Equal(0, report.VatDiscrepancyKobo);

        await using var verify = TestDb.NewContext(db.TestConnection);
        var reconciledTransfer = await verify.Transfers.SingleAsync(x => x.Id == transferId);
        Assert.NotNull(reconciledTransfer.ReconciledAt);

        var extLedger = await verify.ExternalAccounts.Where(x => x.AccountKey == ExternalAccountKeys.LedgerHolding).SingleAsync();
        var customerSum = await verify.Wallets.Where(x => x.AccountType == AccountType.Customer).SumAsync(x => (long?)x.BalanceKobo) ?? 0;
        Assert.Equal(customerSum, extLedger.BalanceKobo);

        var extSettlement = await verify.ExternalAccounts.Where(x => x.AccountKey == ExternalAccountKeys.SettlementHolding).SingleAsync();
        var settlementSys = await verify.Wallets.Where(x => x.SystemKey == SystemAccountKeys.Settlement).Select(x => x.BalanceKobo).SingleAsync();
        Assert.Equal(settlementSys, extSettlement.BalanceKobo);

        var extIncome = await verify.ExternalAccounts.Where(x => x.AccountKey == ExternalAccountKeys.IncomeHolding).SingleAsync();
        var incomeSys = await verify.Wallets.Where(x => x.SystemKey == SystemAccountKeys.Income).Select(x => x.BalanceKobo).SingleAsync();
        Assert.Equal(incomeSys, extIncome.BalanceKobo);

        var extVat = await verify.ExternalAccounts.Where(x => x.AccountKey == ExternalAccountKeys.VatHolding).SingleAsync();
        var vatSys = await verify.Wallets.Where(x => x.SystemKey == SystemAccountKeys.Vat).Select(x => x.BalanceKobo).SingleAsync();
        Assert.Equal(vatSys, extVat.BalanceKobo);
    }

    [Fact]
    public async Task Reconciliation_is_idempotent_for_same_wat_day()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var clock = new SystemClock();
        var reconService = TestDb.CreateReconciliationService(db.Db, clock);

        var watDate = WatBusinessDay.Today(clock.UtcNow);
        var first = await reconService.RunReconciliationAsync(watDate, "admin-1", CancellationToken.None);
        var second = await reconService.RunReconciliationAsync(watDate, "admin-2", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("admin-1", second.RunBy);
        Assert.Equal(1, await db.Db.ReconciliationReports.CountAsync(x => x.WatDate == watDate));
    }

    [Fact]
    public async Task Reconciliation_report_can_be_retrieved_by_date()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var clock = new SystemClock();
        var reconService = TestDb.CreateReconciliationService(db.Db, clock);

        var watDate = WatBusinessDay.Today(clock.UtcNow);
        await reconService.RunReconciliationAsync(watDate, "admin-1", CancellationToken.None);

        var retrieved = await reconService.GetReconciliationAsync(watDate, CancellationToken.None);
        Assert.NotNull(retrieved);
        Assert.Equal(watDate, retrieved.WatDate);
        Assert.Equal(ReconciliationStatus.Balanced, retrieved.Status);
    }

    [Fact]
    public async Task Reconciliation_shows_amount_debited_from_settlement_holding()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var clock = new SystemClock();

        var walletService = TestDb.CreateWalletService(db.Db, clock);
        var transferService = TestDb.CreateTransferService(db.Db, clock, Fees);
        var reconService = TestDb.CreateReconciliationService(db.Db, clock);

        var customer = await walletService.CreateAsync("recon-cust-2", CancellationToken.None);
        await walletService.CreditAsync(customer.Id, 2_000_000, "tester", "corr", CancellationToken.None);

        await transferService.TransferOutboundAsync(
            new OutboundTransferCommand("recon-cust-2", customer.Id, "9999999999", "058", "Jane", 300_000, "recon-key-a", "corr"),
            CancellationToken.None);
        await transferService.TransferOutboundAsync(
            new OutboundTransferCommand("recon-cust-2", customer.Id, "9999999998", "058", "John", 200_000, "recon-key-b", "corr"),
            CancellationToken.None);

        var transfers = await db.Db.Transfers.Where(x => x.CustomerId == "recon-cust-2").ToListAsync();
        foreach (var t in transfers)
        {
            t.MarkSettled(clock.UtcNow);
            var job = await db.Db.SettlementJobs.SingleAsync(x => x.TransferId == t.Id);
            job.Succeed($"SIM-{t.Id:N}", clock.UtcNow);
        }
        await db.Db.SaveChangesAsync();

        var watDate = WatBusinessDay.Today(clock.UtcNow);
        var report = await reconService.RunReconciliationAsync(watDate, "admin-1", CancellationToken.None);

        Assert.Equal(ReconciliationStatus.Balanced, report.Status);
        Assert.Equal(500_000, report.TotalOutboundAmountKobo);
        Assert.Equal(2, report.TotalOutboundCount);
        Assert.Equal(100_000, report.TotalOutboundFeeKobo);
        Assert.Equal(7_500, report.TotalOutboundVatKobo);

        await using var verify = TestDb.NewContext(db.TestConnection);
        var extSettlement = await verify.ExternalAccounts.Where(x => x.AccountKey == ExternalAccountKeys.SettlementHolding).SingleAsync();
        Assert.Equal(0, extSettlement.BalanceKobo);

        var settlementSys = await verify.Wallets.Where(x => x.SystemKey == SystemAccountKeys.Settlement).Select(x => x.BalanceKobo).SingleAsync();
        Assert.Equal(0, settlementSys);
    }
}
