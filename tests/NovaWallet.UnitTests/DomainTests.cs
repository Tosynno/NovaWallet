using NovaWallet.Application;
using NovaWallet.Domain;
using Xunit;

namespace NovaWallet.UnitTests;

public sealed class MoneyTests
{
    [Fact]
    public void Create_rejects_negative_kobo() => Assert.Throws<ArgumentOutOfRangeException>(() => Money.Create(-1));

    [Fact]
    public void Subtraction_rejects_negative_result() =>
        Assert.Throws<InvalidOperationException>(() => Money.Create(10) - Money.Create(11));

    [Fact]
    public void Checked_addition_overflows_throw() => Assert.Throws<OverflowException>(() => Money.Create(long.MaxValue) + Money.Create(1));
}

public sealed class WalletTests
{
    private static Wallet NewWallet() => Wallet.CreateCustomer("customer-1", "9000000001", DateTimeOffset.UtcNow);

    [Fact]
    public void Credit_increases_balance_in_kobo()
    {
        var wallet = NewWallet();
        wallet.Credit(Money.Create(50_000), DateTimeOffset.UtcNow);
        Assert.Equal(50_000, wallet.BalanceKobo);
    }

    [Fact]
    public void Debit_cannot_exceed_balance()
    {
        var wallet = NewWallet();
        wallet.Credit(Money.Create(15_000), DateTimeOffset.UtcNow);
        var ex = Assert.Throws<DomainException>(() => wallet.Debit(Money.Create(15_001), DateTimeOffset.UtcNow));
        Assert.Equal("wallet.insufficient_funds", ex.Code);
        Assert.Equal(15_000, wallet.BalanceKobo);
    }

    [Fact]
    public void Credit_rejects_non_positive_amount()
    {
        var wallet = NewWallet();
        Assert.Throws<DomainException>(() => wallet.Credit(Money.Zero, DateTimeOffset.UtcNow));
    }
}

public sealed class TransferTests
{
    [Fact]
    public void Internal_rejects_same_wallet()
    {
        var id = Guid.NewGuid();
        var ex = Assert.Throws<DomainException>(() => Transfer.CreateInternal("c", id, id, "9000000001", Money.Create(100), "key", "hash", "REF", "corr", DateTimeOffset.UtcNow));
        Assert.Equal("transfer.same_wallet", ex.Code);
    }

    [Fact]
    public void Outbound_total_debit_is_amount_plus_fee_plus_vat()
    {
        var t = Transfer.CreateOutbound("c", Guid.NewGuid(), "9999999999", "058", "Jane", Money.Create(10_000), Money.Create(500), Money.Create(37), "key", "hash", "REF", "corr", DateTimeOffset.UtcNow);
        Assert.Equal(10_537, t.TotalDebitKobo);
        Assert.Equal(TransferType.Outbound, t.Type);
    }
}

public sealed class FingerprintTests
{
    [Fact]
    public void Internal_fingerprint_changes_when_payload_changes()
    {
        var source = Guid.NewGuid();
        var a = new InternalTransferCommand("c", source, "9000000001", 10_000, "key", "corr");
        var b = a with { AmountKobo = 10_001 };
        Assert.NotEqual(IdempotencyFingerprint.Compute(a), IdempotencyFingerprint.Compute(b));
        Assert.Equal(IdempotencyFingerprint.Compute(a), IdempotencyFingerprint.Compute(a));
    }

    [Fact]
    public void Outbound_fingerprint_changes_when_bank_code_changes()
    {
        var source = Guid.NewGuid();
        var a = new OutboundTransferCommand("c", source, "9999999999", "058", "Jane", 10_000, "key", "corr");
        var b = a with { DestinationBankCode = "011" };
        Assert.NotEqual(IdempotencyFingerprint.Compute(a), IdempotencyFingerprint.Compute(b));
    }
}

public sealed class WatBusinessDayTests
{
    [Fact]
    public void WAT_business_day_handles_midnight_boundary()
    {
        var beforeMidnight = new DateTimeOffset(2026, 9, 15, 22, 59, 59, TimeSpan.Zero);
        var afterMidnight = new DateTimeOffset(2026, 9, 15, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 9, 15), WatBusinessDay.Today(beforeMidnight));
        Assert.Equal(new DateOnly(2026, 9, 16), WatBusinessDay.Today(afterMidnight));
    }
}

public sealed class LimitPolicyTests
{
    [Fact]
    public void Daily_limit_rejects_amount_that_crosses_500k()
    {
        Assert.True(DailyOutboundLimitPolicy.IsAllowed(49_900_000, 100_000));
        Assert.False(DailyOutboundLimitPolicy.IsAllowed(49_900_000, 100_001));
        Assert.False(DailyOutboundLimitPolicy.IsAllowed(50_000_000, 1));
    }
}

public sealed class FeePolicyTests
{
    [Fact]
    public void Outbound_fee_is_flat_50_naira_with_7_5_percent_vat()
    {
        var policy = new FeePolicy(50_000, 0.075m);
        var (fee, vat) = policy.OutboundCharges();
        Assert.Equal(50_000, fee);
        Assert.Equal(3_750, vat);
    }
}

public sealed class AccountNumberTests
{
    [Fact]
    public void Generated_account_number_is_10_digits_and_starts_with_90()
    {
        for (var i = 0; i < 100; i++)
        {
            var n = AccountNumberGenerator.Generate();
            Assert.Equal(10, n.Length);
            Assert.StartsWith("90", n);
            Assert.All(n, c => Assert.True(char.IsDigit(c)));
        }
    }
}
