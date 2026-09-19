using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;
using Xunit;

namespace NovaWallet.IntegrationTests;

public sealed class PersistenceTests
{
    private static readonly FeePolicy Fees = new(50_000, 0.075m);

    [Fact]
    public async Task Same_idempotency_key_does_not_create_two_internal_transfers()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var source = Wallet.CreateCustomer("idem-customer", "5000000001", DateTimeOffset.UtcNow);
        source.Credit(Money.Create(100_000), DateTimeOffset.UtcNow);
        var dest = Wallet.CreateCustomer("other", "5000000002", DateTimeOffset.UtcNow);
        db.Db.Wallets.AddRange(source, dest);
        await db.Db.SaveChangesAsync();

        var service = TestDb.CreateTransferService(db.Db, fees: Fees);
        var command = new InternalTransferCommand("idem-customer", source.Id, "5000000002", 10_000, "same-key", "corr");
        var first = await service.TransferInternalAsync(command, CancellationToken.None);
        var second = await service.TransferInternalAsync(command, CancellationToken.None);
        Assert.Equal(first.TransferId, second.TransferId);
        Assert.Equal(1, await db.Db.Transfers.CountAsync(x => x.IdempotencyKey == "same-key"));
        Assert.Equal(90_000, await db.Db.Wallets.Where(x => x.Id == source.Id).Select(x => x.BalanceKobo).SingleAsync());

        var mismatch = command with { AmountKobo = 10_001 };
        var ex = await Assert.ThrowsAsync<DomainException>(() => service.TransferInternalAsync(mismatch, CancellationToken.None));
        Assert.Equal("idempotency.payload_mismatch", ex.Code);
    }

    [Fact]
    public async Task Outbound_transfer_credits_settlement_income_and_vat()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var source = Wallet.CreateCustomer("out-cust", "5000000003", DateTimeOffset.UtcNow);
        source.Credit(Money.Create(1_000_000), DateTimeOffset.UtcNow);
        db.Db.Wallets.Add(source);
        await db.Db.SaveChangesAsync();

        var settlement = await db.Db.Wallets.Where(x => x.SystemKey == SystemAccountKeys.Settlement).SingleAsync();
        var income = await db.Db.Wallets.Where(x => x.SystemKey == SystemAccountKeys.Income).SingleAsync();
        var vat = await db.Db.Wallets.Where(x => x.SystemKey == SystemAccountKeys.Vat).SingleAsync();

        var service = TestDb.CreateTransferService(db.Db, fees: Fees);
        var result = await service.TransferOutboundAsync(
            new OutboundTransferCommand("out-cust", source.Id, "9999999999", "058", "Jane Doe", 100_000, "key-1", "corr"),
            CancellationToken.None);

        Assert.Equal(100_000, result.AmountKobo);
        Assert.Equal(50_000, result.FeeKobo);
        Assert.Equal(3_750, result.VatKobo);
        Assert.Equal(153_750, result.TotalDebitKobo);
        Assert.Equal(846_250, await db.Db.Wallets.Where(x => x.Id == source.Id).Select(x => x.BalanceKobo).SingleAsync());
        Assert.Equal(600_000_100_000, await db.Db.Wallets.Where(x => x.Id == settlement.Id).Select(x => x.BalanceKobo).SingleAsync());
        Assert.Equal(50_000, await db.Db.Wallets.Where(x => x.Id == income.Id).Select(x => x.BalanceKobo).SingleAsync());
        Assert.Equal(3_750, await db.Db.Wallets.Where(x => x.Id == vat.Id).Select(x => x.BalanceKobo).SingleAsync());
        var job = await db.Db.SettlementJobs.SingleAsync(x => x.TransferId == result.TransferId);
        Assert.Equal(SettlementStatus.Pending, job.Status);
    }

    [Fact]
    public async Task Internal_transfer_rejects_unknown_destination_account_number()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var source = Wallet.CreateCustomer("c1", "5000000004", DateTimeOffset.UtcNow);
        source.Credit(Money.Create(10_000), DateTimeOffset.UtcNow);
        db.Db.Wallets.Add(source);
        await db.Db.SaveChangesAsync();

        var service = TestDb.CreateTransferService(db.Db, fees: Fees);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.TransferInternalAsync(new InternalTransferCommand("c1", source.Id, "0000000000", 1_000, "k", "corr"), CancellationToken.None));
        Assert.Equal("wallet.destination_account_not_found", ex.Code);
    }

    [Fact]
    public async Task Audit_log_is_append_only()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        db.Db.AuditLogs.Add(AuditLog.Create("test", "test", "Wallet", Guid.NewGuid().ToString(), null, "{}", "corr", DateTimeOffset.UtcNow));
        await db.Db.SaveChangesAsync();
        var id = await db.Db.AuditLogs.Select(x => x.Id).MaxAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AuditLogs WHERE Id = {id}"));
    }
}
