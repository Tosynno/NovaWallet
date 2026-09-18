using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;
using Xunit;

namespace NovaWallet.IntegrationTests;

public sealed class ConcurrencyTests
{
    private static readonly FeePolicy Fees = new(50_000, 0.075m);

    [Fact]
    public async Task Concurrent_internal_transfers_from_one_wallet_never_double_spend()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var source = Wallet.CreateCustomer("customer-1", "5000000001", DateTimeOffset.UtcNow);
        source.Credit(Money.Create(1_000), DateTimeOffset.UtcNow);
        var destinations = Enumerable.Range(0, 20).Select(i => Wallet.CreateCustomer("dest", $"50{(i + 2):D8}", DateTimeOffset.UtcNow)).ToArray();
        db.Db.Wallets.Add(source);
        db.Db.Wallets.AddRange(destinations);
        await db.Db.SaveChangesAsync();

        var tasks = destinations.Select((d, i) => Task.Run(async () =>
        {
            await using var ctx = TestDb.NewContext(db.TestConnection);
            var service = TestDb.CreateTransferService(ctx, fees: Fees);
            try { return await service.TransferInternalAsync(new InternalTransferCommand("customer-1", source.Id, d.AccountNumber, 100, $"key-{i}", Guid.NewGuid().ToString("N")), CancellationToken.None); }
            catch (DomainException) { return null; }
        })).ToArray();
        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(x => x is not null);
        Assert.Equal(10, successCount);

        await using var verify = TestDb.NewContext(db.TestConnection);
        var finalSource = await verify.Wallets.SingleAsync(x => x.CustomerId == "customer-1" && x.AccountNumber == "5000000001");
        Assert.Equal(0, finalSource.BalanceKobo);
        Assert.True(finalSource.BalanceKobo >= 0);
        Assert.Equal(10, await verify.Transfers.CountAsync());
    }

    [Fact]
    public async Task Concurrent_credits_to_same_wallet_do_not_lose_updates()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var wallet = Wallet.CreateCustomer("credit-cust", "5000000002", DateTimeOffset.UtcNow);
        db.Db.Wallets.Add(wallet);
        await db.Db.SaveChangesAsync();

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await using var ctx = TestDb.NewContext(db.TestConnection);
            var service = TestDb.CreateWalletService(ctx);
            await service.CreditAsync(wallet.Id, 1_000, "tester", Guid.NewGuid().ToString("N"), CancellationToken.None);
        })).ToArray();
        await Task.WhenAll(tasks);

        await using var verify = TestDb.NewContext(db.TestConnection);
        var final = await verify.Wallets.SingleAsync(x => x.Id == wallet.Id);
        Assert.Equal(20_000, final.BalanceKobo);
    }

    [Fact]
    public async Task Concurrent_internal_transfers_into_same_destination_do_not_lose_credits()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var destination = Wallet.CreateCustomer("dest-cust", "5000000003", DateTimeOffset.UtcNow);
        var sources = Enumerable.Range(0, 20).Select(i =>
        {
            var w = Wallet.CreateCustomer($"src-{i}", $"91{(i + 1):D8}", DateTimeOffset.UtcNow);
            w.Credit(Money.Create(1_000), DateTimeOffset.UtcNow);
            return w;
        }).ToArray();
        db.Db.Wallets.Add(destination);
        db.Db.Wallets.AddRange(sources);
        await db.Db.SaveChangesAsync();

        var tasks = sources.Select((s, i) => Task.Run(async () =>
        {
            await using var ctx = TestDb.NewContext(db.TestConnection);
            var service = TestDb.CreateTransferService(ctx, fees: Fees);
            try { return await service.TransferInternalAsync(new InternalTransferCommand(s.CustomerId, s.Id, "5000000003", 100, $"key-{i}", Guid.NewGuid().ToString("N")), CancellationToken.None); }
            catch (DomainException) { return null; }
        })).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(20, results.Count(x => x is not null));

        await using var verify = TestDb.NewContext(db.TestConnection);
        var finalDest = await verify.Wallets.SingleAsync(x => x.AccountNumber == "5000000003");
        Assert.Equal(2_000, finalDest.BalanceKobo);
    }

    [Fact]
    public async Task Concurrent_outbound_transfers_respect_per_wallet_daily_limit()
    {
        if (!TestDb.IsConfigured) return;
        await using var db = await TestDb.CreateAsync();
        var user = User.Create("limit-cust", "limit@test.com", "Limit", "Customer", UserRole.Customer, DateTimeOffset.UtcNow);
        user.VerifyKyc(DateTimeOffset.UtcNow);
        db.Db.Users.Add(user);
        var w1 = Wallet.CreateCustomer("limit-cust", "5000000004", DateTimeOffset.UtcNow); w1.Credit(Money.Create(60_000_000), DateTimeOffset.UtcNow);
        db.Db.Wallets.Add(w1);
        await db.Db.SaveChangesAsync();

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await using var ctx = TestDb.NewContext(db.TestConnection);
            var service = TestDb.CreateTransferService(ctx, fees: Fees);
            try { return await service.TransferOutboundAsync(new OutboundTransferCommand("limit-cust", w1.Id, "9999999999", "058", "Jane", 5_000_000, $"key-{i}", Guid.NewGuid().ToString("N")), CancellationToken.None); }
            catch (DomainException) { return null; }
        })).ToArray();
        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(x => x is not null);
        Assert.Equal(10, successCount);

        await using var verify = TestDb.NewContext(db.TestConnection);
        var totalOutbound = await verify.Transfers.Where(x => x.CustomerId == "limit-cust" && x.Type == TransferType.Outbound).SumAsync(x => (long?)x.AmountKobo) ?? 0;
        Assert.Equal(50_000_000, totalOutbound);
        Assert.True(totalOutbound <= DailyOutboundLimitPolicy.VerifiedLimitKobo);
    }
}
