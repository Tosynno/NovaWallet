using System.Security.Cryptography;
using System.Text;
using NovaWallet.Domain;

namespace NovaWallet.Application;

public sealed record TransferCommand(string CustomerId, string IdempotencyKey, string CorrelationId);

public sealed record InternalTransferRequest(Guid SourceWalletId, string DestinationAccountNumber, long AmountKobo);
public sealed record InternalTransferCommand(string CustomerId, Guid SourceWalletId, string DestinationAccountNumber, long AmountKobo, string IdempotencyKey, string CorrelationId);

public sealed record OutboundTransferRequest(Guid SourceWalletId, string DestinationAccountNumber, string DestinationBankCode, string DestinationAccountName, long AmountKobo);
public sealed record OutboundTransferCommand(
    string CustomerId, Guid SourceWalletId,
    string DestinationAccountNumber, string DestinationBankCode, string DestinationAccountName,
    long AmountKobo, string IdempotencyKey, string CorrelationId);

public sealed record InboundTransferRequest(string DestinationAccountNumber, string OriginatorBankCode, string OriginatorAccountNumber, string OriginatorAccountName, long AmountKobo);
public sealed record InboundTransferCommand(
    string DestinationAccountNumber, string OriginatorBankCode, string OriginatorAccountNumber, string OriginatorAccountName,
    long AmountKobo, string IdempotencyKey, string CorrelationId);

public sealed record TransferResult(Guid TransferId, TransferType Type, TransferStatus Status, long AmountKobo, long FeeKobo, long VatKobo, long TotalDebitKobo, string Reference, string? ExternalReference);
public sealed record StatementItem(Guid? TransferId, Guid WalletId, LedgerDirection Direction, long AmountKobo, string Leg, DateTimeOffset CreatedAt);
public sealed record WalletCreatedResult(Guid Id, string AccountNumber, string CustomerId, string Currency, string AccountName, long BalanceKobo);
public sealed record BalanceResult(string AccountNumber, string Currency, long BalanceKobo);
public sealed record NameEnquiryResult(string AccountNumber, string AccountName, string BankCode, string BankName);

public interface IWalletService
{
    Task<WalletCreatedResult> CreateAsync(string customerId, string currency, string? accountName, CancellationToken ct);
    Task<BalanceResult?> GetAsync(Guid walletId, string customerId, CancellationToken ct);
    Task<BalanceResult> CreditAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct);
    Task<IReadOnlyList<StatementItem>> StatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct);
    Task<NameEnquiryResult?> NameEnquiryAsync(string accountNumber, CancellationToken ct);
    Task<IReadOnlyList<WalletSummaryResult>> ListByCustomerAsync(string customerId, CancellationToken ct);
}
public sealed record WalletSummaryResult(Guid Id, string AccountNumber, string Currency, string AccountName, long BalanceKobo);
public interface ITransferService
{
    Task<TransferResult> TransferInternalAsync(InternalTransferCommand command, CancellationToken ct);
    Task<TransferResult> TransferOutboundAsync(OutboundTransferCommand command, CancellationToken ct);
    Task<TransferResult> TransferInboundAsync(InboundTransferCommand command, CancellationToken ct);
    Task<TransferResult> QueryTransferStatusAsync(Guid transferId, string customerId, CancellationToken ct);
    Task<TransferDetailResult?> GetTransferDetailAsync(Guid transferId, string customerId, CancellationToken ct);
    Task<RepostResult> RepostAsync(Guid transferId, CancellationToken ct);
}

public sealed record TransferDetailResult(Guid Id, TransferType Type, TransferStatus Status, long AmountKobo, long FeeKobo, long VatKobo, long TotalDebitKobo, string Reference, string? ExternalReference, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record RepostResult(Guid TransferId, TransferStatus Status, string Message);

public interface IPaymentRail
{
    Task<PaymentResult> SendAsync(PaymentInstruction instruction, CancellationToken ct);
    Task<PaymentStatusResult> GetStatusAsync(string externalReference, CancellationToken ct);
}
public sealed record PaymentInstruction(Guid TransferId, string Reference, string DestinationAccountNumber, string DestinationBankCode, string DestinationAccountName, long AmountKobo);
public enum PaymentRailStatus { Sent, Failed, Unknown }
public sealed record PaymentResult(PaymentRailStatus Status, string? ExternalReference, string? Error);
public sealed record PaymentStatusResult(PaymentRailStatus Status, string? ExternalReference, string? Error);

public interface IReconciliationService
{
    Task<ReconciliationReportResult> RunReconciliationAsync(DateOnly? watDate, string runBy, CancellationToken ct);
    Task<ReconciliationReportResult?> GetReconciliationAsync(DateOnly watDate, CancellationToken ct);
    Task<IReadOnlyList<ReconciliationReportSummary>> ListReconciliationsAsync(int page, int pageSize, CancellationToken ct);
}
public sealed record ReconciliationReportResult(
    Guid Id, DateOnly WatDate, ReconciliationStatus Status,
    long TotalOutboundAmountKobo, long TotalOutboundFeeKobo, long TotalOutboundVatKobo, int TotalOutboundCount,
    long LedgerHoldingExpectedKobo, long LedgerHoldingActualKobo, long LedgerDiscrepancyKobo,
    long SettlementHoldingExpectedKobo, long SettlementHoldingActualKobo, long SettlementDiscrepancyKobo,
    long IncomeHoldingExpectedKobo, long IncomeHoldingActualKobo, long IncomeDiscrepancyKobo,
    long VatHoldingExpectedKobo, long VatHoldingActualKobo, long VatDiscrepancyKobo,
    string? Notes, string RunBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record ReconciliationReportSummary(Guid Id, DateOnly WatDate, ReconciliationStatus Status, long TotalOutboundAmountKobo, int TotalOutboundCount, DateTimeOffset CreatedAt);
public sealed record ExternalAccountResult(string AccountKey, ExternalAccountType Type, string BankName, string BankCode, string AccountNumber, string AccountName, long BalanceKobo);

public interface IExternalAccountService
{
    Task<IReadOnlyList<ExternalAccountResult>> GetAllAsync(CancellationToken ct);
}

public interface IAuthService
{
    Task<ChannelTokenResult?> IssueChannelTokenAsync(string appKey, string appSecret, CancellationToken ct);
    Task<LoginResult?> LoginAsync(string email, string password, CancellationToken ct);
    Task<RegisterResult> RegisterAsync(string email, string password, string firstName, string lastName, string? phoneNumber, CancellationToken ct);
    Task<RefreshTokenResult?> RefreshTokenAsync(string customerId, CancellationToken ct);
}

public sealed record ChannelTokenResult(string Token, int ExpiresInSeconds);
public sealed record LoginResult(string Token, int ExpiresInSeconds, string CustomerId);
public sealed record RegisterResult(bool Success, string? Error, string? Token, int ExpiresInSeconds, string? CustomerId, Guid? WalletId, string? AccountNumber);
public sealed record RefreshTokenResult(string Token, int ExpiresInSeconds);

public interface IChannelService
{
    Task<ChannelCreatedResult> CreateAsync(string channelKey, string channelName, CancellationToken ct);
    Task<IReadOnlyList<ChannelListItem>> ListAsync(CancellationToken ct);
    Task<ChannelKeysRotatedResult?> RotateKeysAsync(string channelKey, CancellationToken ct);
    Task<ChannelStatusResult?> UpdateStatusAsync(string channelKey, ChannelStatus status, CancellationToken ct);
}

public sealed record ChannelCreatedResult(string ChannelKey, string ChannelName, ChannelStatus Status, string AppKey, string AppSecret);
public sealed record ChannelListItem(long Id, string ChannelKey, string ChannelName, string AppKey, ChannelStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ChannelKeysRotatedResult(string ChannelKey, string AppKey, string AppSecret);
public sealed record ChannelStatusResult(string ChannelKey, ChannelStatus Status);

public interface IUserService
{
    Task<UserResult?> GetByIdAsync(long userId, CancellationToken ct);
    Task<UserResult?> GetByCustomerAsync(string customerId, CancellationToken ct);
    Task<UserCreatedResult> CreateAsync(string customerId, string email, string? firstName, string? lastName, string? phoneNumber, CancellationToken ct);
}

public sealed record UserResult(long Id, string CustomerId, string Email, string? PhoneNumber, string FirstName, string LastName, KycStatus KycStatus, DateTimeOffset CreatedAt);
public sealed record UserCreatedResult(long Id, string CustomerId, string Email, KycStatus KycStatus);

public interface IKycService
{
    Task<KycSubmittedResult?> SubmitAsync(long userId, KycDocumentType documentType, string documentNumber, CancellationToken ct);
    Task<IReadOnlyList<KycDocumentResult>> ListAsync(long userId, CancellationToken ct);
    Task<KycReviewResult?> ApproveAsync(long userId, long docId, CancellationToken ct);
    Task<KycReviewResult?> RejectAsync(long userId, long docId, string? notes, CancellationToken ct);
    Task<KycStatusResult?> GetByCustomerAsync(string customerId, CancellationToken ct);
}
public sealed record KycStatusResult(long UserId, string CustomerId, KycStatus KycStatus, string FirstName, string LastName, string Email);

public sealed record KycSubmittedResult(long Id, KycDocumentType DocumentType, string DocumentNumber, KycStatus Status);
public sealed record KycDocumentResult(long Id, KycDocumentType DocumentType, string DocumentNumber, KycStatus Status, DateTimeOffset SubmittedAt, DateTimeOffset? ReviewedAt, string? ReviewNotes);
public sealed record KycReviewResult(long DocId, KycStatus DocStatus, KycStatus UserKycStatus, string? ReviewNotes);

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public static class WatBusinessDay
{
    private static readonly TimeZoneInfo WAT = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "W. Central Africa Standard Time" : "Africa/Lagos");

    public static DateOnly Today(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, WAT).DateTime);

    public static DateTimeOffset StartOfWatDayUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, WAT.GetUtcOffset(local)).ToUniversalTime();
    }
}

public static class IdempotencyFingerprint
{
    public static string Compute(InternalTransferCommand c)
    {
        var canonical = $"internal|{c.CustomerId}|{c.SourceWalletId:D}|{c.DestinationAccountNumber}|{c.AmountKobo}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Compute(OutboundTransferCommand c)
    {
        var canonical = $"outbound|{c.CustomerId}|{c.SourceWalletId:D}|{c.DestinationAccountNumber}|{c.DestinationBankCode}|{c.AmountKobo}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Compute(InboundTransferCommand c)
    {
        var canonical = $"inbound|{c.DestinationAccountNumber}|{c.OriginatorBankCode}|{c.OriginatorAccountNumber}|{c.AmountKobo}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public static class DailyOutboundLimitPolicy
{
    public const long VerifiedLimitKobo = 50_000_000;
    public const long UnverifiedLimitKobo = 5_000_000;
    public static long LimitFor(bool kycVerified) => kycVerified ? VerifiedLimitKobo : UnverifiedLimitKobo;
    public static bool IsAllowed(long alreadySentKobo, long requestedKobo, long limitKobo) =>
        alreadySentKobo >= 0 && requestedKobo > 0 && alreadySentKobo <= limitKobo && requestedKobo <= limitKobo - alreadySentKobo;
}

public sealed class FeePolicy
{
    public FeePolicy(long outboundFeeKobo, decimal vatRate)
    {
        OutboundFeeKobo = outboundFeeKobo;
        VatRate = vatRate;
    }

    public long OutboundFeeKobo { get; }
    public decimal VatRate { get; }

    public (long feeKobo, long vatKobo) OutboundCharges()
    {
        var vat = Math.Round(OutboundFeeKobo * VatRate, MidpointRounding.ToEven);
        return (OutboundFeeKobo, (long)vat);
    }
}

public static class AccountNumberGenerator
{
    private const string NovaPrefix = "50";
    private static readonly Random Random = new();

    public static string Generate()
    {
        Span<char> digits = stackalloc char[10];
        digits[0] = NovaPrefix[0];
        digits[1] = NovaPrefix[1];
        lock (Random)
        {
            for (var i = 2; i < 9; i++)
                digits[i] = (char)('0' + Random.Next(0, 10));
        }
        digits[9] = CheckDigit(digits[..9]);
        return new string(digits);
    }

    private static char CheckDigit(ReadOnlySpan<char> firstNine)
    {
        var sum = 0;
        for (var i = 0; i < 9; i++)
            sum += (firstNine[i] - '0') * (i + 2);
        var mod = sum % 11;
        var check = (11 - mod) % 11;
        return (char)('0' + (check == 10 ? 0 : check));
    }
}
