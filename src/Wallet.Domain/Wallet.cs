namespace NovaWallet.Domain;

public sealed class Wallet
{
    private Wallet() { }
    public Guid Id { get; private set; }
    public string AccountNumber { get; private set; } = null!;
    public string CustomerId { get; private set; } = null!;
    public AccountType AccountType { get; private set; }
    public string? SystemKey { get; private set; }
    public string Currency { get; private set; } = "NGN";
    public long BalanceKobo { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Wallet CreateCustomer(string customerId, string accountNumber, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        AccountNumber = accountNumber,
        CustomerId = customerId,
        AccountType = AccountType.Customer,
        BalanceKobo = 0,
        Version = 1,
        CreatedAt = now,
        UpdatedAt = now
    };

    public static Wallet CreateSystem(AccountType type, string systemKey, string accountNumber, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        AccountNumber = accountNumber,
        CustomerId = "SYSTEM",
        AccountType = type,
        SystemKey = systemKey,
        BalanceKobo = 0,
        Version = 1,
        CreatedAt = now,
        UpdatedAt = now
    };

    public void Credit(Money amount, DateTimeOffset now)
    {
        if (!amount.IsPositive) throw new DomainException("amount.invalid", "Credit amount must be greater than zero.");
        BalanceKobo = checked(BalanceKobo + amount.Kobo);
        Version++;
        UpdatedAt = now;
    }

    public void Debit(Money amount, DateTimeOffset now)
    {
        if (!amount.IsPositive) throw new DomainException("amount.invalid", "Debit amount must be greater than zero.");
        if (BalanceKobo < amount.Kobo) throw new DomainException("wallet.insufficient_funds", "Insufficient wallet balance.");
        BalanceKobo -= amount.Kobo;
        Version++;
        UpdatedAt = now;
    }
}
