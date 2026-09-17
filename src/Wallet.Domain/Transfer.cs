namespace NovaWallet.Domain;

public sealed class Transfer
{
    private Transfer() { }
    public Guid Id { get; private set; }
    public string CustomerId { get; private set; } = null!;
    public TransferType Type { get; private set; }
    public Guid SourceWalletId { get; private set; }
    public Guid? DestinationWalletId { get; private set; }
    public string? DestinationAccountNumber { get; private set; }
    public string? DestinationBankCode { get; private set; }
    public string? DestinationAccountName { get; private set; }
    public long AmountKobo { get; private set; }
    public long FeeKobo { get; private set; }
    public long VatKobo { get; private set; }
    public long TotalDebitKobo { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public string Reference { get; private set; } = null!;
    public string? ExternalReference { get; private set; }
    public TransferStatus Status { get; private set; }
    public string CorrelationId { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ReconciledAt { get; private set; }

    public static Transfer CreateInternal(
        string customerId, Guid source, Guid destination, string destinationAccountNumber,
        Money amount, string key, string hash, string reference, string correlationId, DateTimeOffset now)
    {
        if (source == destination) throw new DomainException("transfer.same_wallet", "Source and destination wallets must differ.");
        if (!amount.IsPositive) throw new DomainException("amount.invalid", "Transfer amount must be greater than zero.");
        return new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Type = TransferType.Internal,
            SourceWalletId = source,
            DestinationWalletId = destination,
            DestinationAccountNumber = destinationAccountNumber,
            AmountKobo = amount.Kobo,
            FeeKobo = 0,
            VatKobo = 0,
            TotalDebitKobo = amount.Kobo,
            IdempotencyKey = key,
            RequestHash = hash,
            Reference = reference,
            Status = TransferStatus.Settled,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Transfer CreateOutbound(
        string customerId, Guid source,
        string destinationAccountNumber, string destinationBankCode, string destinationAccountName,
        Money amount, Money fee, Money vat, string key, string hash, string reference, string correlationId, DateTimeOffset now)
    {
        if (!amount.IsPositive) throw new DomainException("amount.invalid", "Transfer amount must be greater than zero.");
        return new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Type = TransferType.Outbound,
            SourceWalletId = source,
            DestinationWalletId = null,
            DestinationAccountNumber = destinationAccountNumber,
            DestinationBankCode = destinationBankCode,
            DestinationAccountName = destinationAccountName,
            AmountKobo = amount.Kobo,
            FeeKobo = fee.Kobo,
            VatKobo = vat.Kobo,
            TotalDebitKobo = checked(amount.Kobo + fee.Kobo + vat.Kobo),
            IdempotencyKey = key,
            RequestHash = hash,
            Reference = reference,
            Status = TransferStatus.Submitted,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Transfer CreateInbound(
        string customerId, Guid destinationWallet, string destinationAccountNumber,
        string originatorBankCode, string originatorAccountNumber, string originatorAccountName,
        Money amount, string key, string hash, string reference, string correlationId, DateTimeOffset now)
    {
        if (!amount.IsPositive) throw new DomainException("amount.invalid", "Transfer amount must be greater than zero.");
        return new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Type = TransferType.Inbound,
            SourceWalletId = Guid.Empty,
            DestinationWalletId = destinationWallet,
            DestinationAccountNumber = destinationAccountNumber,
            DestinationBankCode = originatorBankCode,
            DestinationAccountName = originatorAccountName,
            AmountKobo = amount.Kobo,
            FeeKobo = 0,
            VatKobo = 0,
            TotalDebitKobo = amount.Kobo,
            IdempotencyKey = key,
            RequestHash = hash,
            Reference = reference,
            Status = TransferStatus.Settled,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void MarkSettled(DateTimeOffset now) { Status = TransferStatus.Settled; UpdatedAt = now; }
    public void MarkFailed(DateTimeOffset now) { Status = TransferStatus.Failed; UpdatedAt = now; }
    public void MarkUnknown(DateTimeOffset now) { Status = TransferStatus.Unknown; UpdatedAt = now; }
    public void ResetForRepost(DateTimeOffset now) { Status = TransferStatus.Submitted; ExternalReference = null; UpdatedAt = now; }
    public void SetExternalReference(string externalReference, DateTimeOffset now) { ExternalReference = externalReference; UpdatedAt = now; }
    public void MarkReconciled(DateTimeOffset now) { ReconciledAt = now; UpdatedAt = now; }
}
