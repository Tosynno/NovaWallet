namespace NovaWallet.Domain;

public sealed class LedgerEntry
{
    private LedgerEntry() { }
    public long Id { get; private set; }
    public Guid? TransferId { get; private set; }
    public Guid WalletId { get; private set; }
    public LedgerDirection Direction { get; private set; }
    public long AmountKobo { get; private set; }
    public string Leg { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static LedgerEntry Create(Guid? transferId, Guid walletId, LedgerDirection direction, Money amount, string leg, DateTimeOffset now) =>
        new() { TransferId = transferId, WalletId = walletId, Direction = direction, AmountKobo = amount.Kobo, Leg = leg, CreatedAt = now };
}

public sealed class AuditLog
{
    private AuditLog() { }
    public long Id { get; private set; }
    public string Actor { get; private set; } = null!;
    public string Action { get; private set; } = null!;
    public string Entity { get; private set; } = null!;
    public string EntityId { get; private set; } = null!;
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
    public string CorrelationId { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLog Create(string actor, string action, string entity, string entityId, string? beforeJson, string? afterJson, string correlationId, DateTimeOffset now) =>
        new() { Actor = actor, Action = action, Entity = entity, EntityId = entityId, BeforeJson = beforeJson, AfterJson = afterJson, CorrelationId = correlationId, CreatedAt = now };
}

public sealed class OutboxMessage
{
    private OutboxMessage() { }
    public long Id { get; private set; }
    public string EventType { get; private set; } = null!;
    public string AggregateId { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public int Attempts { get; private set; }
    public string EventKey { get; private set; } = null!;

    public static OutboxMessage Create(string eventType, string aggregateId, string payload, string eventKey, DateTimeOffset now) =>
        new() { EventType = eventType, AggregateId = aggregateId, Payload = payload, EventKey = eventKey, OccurredAt = now };
    public void MarkPublished(DateTimeOffset now) { PublishedAt = now; Attempts++; }
}

public sealed class SettlementJob
{
    private SettlementJob() { }
    public long Id { get; private set; }
    public Guid TransferId { get; private set; }
    public SettlementStatus Status { get; private set; }
    public string? ProviderReference { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? LastError { get; private set; }

    public static SettlementJob Create(Guid transferId, DateTimeOffset now) =>
        new() { TransferId = transferId, Status = SettlementStatus.Pending, NextAttemptAt = now, CreatedAt = now };

    public void Claim(DateTimeOffset now, TimeSpan lease)
    {
        Status = SettlementStatus.Processing;
        LeaseUntil = now + lease;
        AttemptCount++;
        NextAttemptAt = now + lease;
    }

    public void Succeed(string providerReference, DateTimeOffset now)
    {
        Status = SettlementStatus.Succeeded;
        ProviderReference = providerReference;
        LeaseUntil = null;
        LastError = null;
        NextAttemptAt = now;
    }

    public void Fail(string error, DateTimeOffset now, TimeSpan backoff, bool deadLetter)
    {
        Status = deadLetter ? SettlementStatus.DeadLetter : SettlementStatus.Failed;
        LastError = error;
        NextAttemptAt = now + backoff;
        LeaseUntil = null;
    }

    public void Repost(DateTimeOffset now)
    {
        Status = SettlementStatus.Pending;
        NextAttemptAt = now;
        AttemptCount = 0;
        LastError = null;
        LeaseUntil = null;
    }
}

public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }
    public long Id { get; private set; }
    public string CustomerId { get; private set; } = null!;
    public string Key { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public Guid TransferId { get; private set; }

    public static IdempotencyRecord Create(string customerId, string key, string requestHash, Guid transferId) =>
        new() { CustomerId = customerId, Key = key, RequestHash = requestHash, TransferId = transferId };
}

public sealed class DailyOutboundCounter
{
    private DailyOutboundCounter() { }
    public long Id { get; private set; }
    public Guid WalletId { get; private set; }
    public DateOnly WatDate { get; private set; }
    public long TotalKobo { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static DailyOutboundCounter Create(Guid walletId, DateOnly watDate, long amount, DateTimeOffset now) =>
        new() { WalletId = walletId, WatDate = watDate, TotalKobo = amount, UpdatedAt = now };

    public void Add(long amount, DateTimeOffset now)
    {
        TotalKobo = checked(TotalKobo + amount);
        UpdatedAt = now;
    }
}

public sealed class ExternalAccount
{
    private ExternalAccount() { }
    public long Id { get; private set; }
    public string AccountKey { get; private set; } = null!;
    public ExternalAccountType Type { get; private set; }
    public string BankName { get; private set; } = null!;
    public string BankCode { get; private set; } = null!;
    public string AccountNumber { get; private set; } = null!;
    public string AccountName { get; private set; } = null!;
    public long BalanceKobo { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ExternalAccount Create(string accountKey, ExternalAccountType type, string bankName, string bankCode, string accountNumber, string accountName, DateTimeOffset now) =>
        new()
        {
            AccountKey = accountKey,
            Type = type,
            BankName = bankName,
            BankCode = bankCode,
            AccountNumber = accountNumber,
            AccountName = accountName,
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
        if (BalanceKobo < amount.Kobo) throw new DomainException("external.insufficient_funds", "External account has insufficient funds for reconciliation.");
        BalanceKobo -= amount.Kobo;
        Version++;
        UpdatedAt = now;
    }
}

public sealed class ReconciliationReport
{
    private ReconciliationReport() { }
    public Guid Id { get; private set; }
    public DateOnly WatDate { get; private set; }
    public ReconciliationStatus Status { get; private set; }
    public long TotalOutboundAmountKobo { get; private set; }
    public long TotalOutboundFeeKobo { get; private set; }
    public long TotalOutboundVatKobo { get; private set; }
    public int TotalOutboundCount { get; private set; }
    public long LedgerHoldingExpectedKobo { get; private set; }
    public long LedgerHoldingActualKobo { get; private set; }
    public long SettlementHoldingExpectedKobo { get; private set; }
    public long SettlementHoldingActualKobo { get; private set; }
    public long IncomeHoldingExpectedKobo { get; private set; }
    public long IncomeHoldingActualKobo { get; private set; }
    public long VatHoldingExpectedKobo { get; private set; }
    public long VatHoldingActualKobo { get; private set; }
    public long LedgerDiscrepancyKobo { get; private set; }
    public long SettlementDiscrepancyKobo { get; private set; }
    public long IncomeDiscrepancyKobo { get; private set; }
    public long VatDiscrepancyKobo { get; private set; }
    public string? Notes { get; private set; }
    public string RunBy { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static ReconciliationReport Create(DateOnly watDate, string runBy, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            WatDate = watDate,
            Status = ReconciliationStatus.InProgress,
            RunBy = runBy,
            CreatedAt = now
        };

    public void RecordOutboundTotals(long amount, long fee, long vat, int count)
    {
        TotalOutboundAmountKobo = amount;
        TotalOutboundFeeKobo = fee;
        TotalOutboundVatKobo = vat;
        TotalOutboundCount = count;
    }

    public void RecordAccount(string accountKey, long expected, long actual)
    {
        var discrepancy = actual - expected;
        switch (accountKey)
        {
            case ExternalAccountKeys.LedgerHolding:
                LedgerHoldingExpectedKobo = expected;
                LedgerHoldingActualKobo = actual;
                LedgerDiscrepancyKobo = discrepancy;
                break;
            case ExternalAccountKeys.SettlementHolding:
                SettlementHoldingExpectedKobo = expected;
                SettlementHoldingActualKobo = actual;
                SettlementDiscrepancyKobo = discrepancy;
                break;
            case ExternalAccountKeys.IncomeHolding:
                IncomeHoldingExpectedKobo = expected;
                IncomeHoldingActualKobo = actual;
                IncomeDiscrepancyKobo = discrepancy;
                break;
            case ExternalAccountKeys.VatHolding:
                VatHoldingExpectedKobo = expected;
                VatHoldingActualKobo = actual;
                VatDiscrepancyKobo = discrepancy;
                break;
        }
    }

    public void Complete(DateTimeOffset now)
    {
        var balanced = LedgerDiscrepancyKobo == 0
            && SettlementDiscrepancyKobo == 0
            && IncomeDiscrepancyKobo == 0
            && VatDiscrepancyKobo == 0;
        Status = balanced ? ReconciliationStatus.Balanced : ReconciliationStatus.Imbalanced;
        CompletedAt = now;
    }
}

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class Channel
{
    private Channel() { }
    public long Id { get; private set; }
    public string ChannelKey { get; private set; } = null!;
    public string ChannelName { get; private set; } = null!;
    public string AppKey { get; private set; } = null!;
    public string AppSecretHash { get; private set; } = null!;
    public ChannelStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Channel Create(string channelKey, string channelName, string appKey, string appSecretHash, DateTimeOffset now) =>
        new()
        {
            ChannelKey = channelKey,
            ChannelName = channelName,
            AppKey = appKey,
            AppSecretHash = appSecretHash,
            Status = ChannelStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

    public void RotateKeys(string newAppKey, string newAppSecretHash, DateTimeOffset now)
    {
        AppKey = newAppKey;
        AppSecretHash = newAppSecretHash;
        UpdatedAt = now;
    }

    public void Suspend(DateTimeOffset now) { Status = ChannelStatus.Suspended; UpdatedAt = now; }
    public void Activate(DateTimeOffset now) { Status = ChannelStatus.Active; UpdatedAt = now; }
    public void Revoke(DateTimeOffset now) { Status = ChannelStatus.Revoked; UpdatedAt = now; }
}

public sealed class User
{
    private User() { }
    public long Id { get; private set; }
    public string CustomerId { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? PhoneNumber { get; private set; }
    public string FirstName { get; private set; } = null!;
    public string LastName { get; private set; } = null!;
    public UserRole Role { get; private set; }
    public KycStatus KycStatus { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static User Create(string customerId, string email, string firstName, string lastName, UserRole role, DateTimeOffset now) =>
        new()
        {
            CustomerId = customerId,
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            Role = role,
            KycStatus = KycStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

    public void SetPhoneNumber(string phoneNumber, DateTimeOffset now) { PhoneNumber = phoneNumber; UpdatedAt = now; }
    public void VerifyKyc(DateTimeOffset now) { KycStatus = KycStatus.Verified; UpdatedAt = now; }
    public void RejectKyc(DateTimeOffset now) { KycStatus = KycStatus.Rejected; UpdatedAt = now; }
    public void SubmitKyc(DateTimeOffset now) { KycStatus = KycStatus.Pending; UpdatedAt = now; }
}

public sealed class KycDocument
{
    private KycDocument() { }
    public long Id { get; private set; }
    public long UserId { get; private set; }
    public KycDocumentType DocumentType { get; private set; }
    public string DocumentNumber { get; private set; } = null!;
    public KycStatus Status { get; private set; }
    public string? ReviewNotes { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    public static KycDocument Create(long userId, KycDocumentType type, string documentNumber, DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            DocumentType = type,
            DocumentNumber = documentNumber,
            Status = KycStatus.Pending,
            SubmittedAt = now
        };

    public void Approve(string? notes, DateTimeOffset now) { Status = KycStatus.Verified; ReviewNotes = notes; ReviewedAt = now; }
    public void Reject(string notes, DateTimeOffset now) { Status = KycStatus.Rejected; ReviewNotes = notes; ReviewedAt = now; }
}
