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
public sealed record WalletCreatedResult(Guid Id, string AccountNumber, string CustomerId, string Currency, long BalanceKobo);
public sealed record BalanceResult(string AccountNumber, string Currency, long BalanceKobo);
public sealed record NameEnquiryResult(string AccountNumber, string AccountName, string BankCode, string BankName);

public interface IWalletService
{
    Task<WalletCreatedResult> CreateAsync(string customerId, CancellationToken ct);
    Task<BalanceResult?> GetAsync(Guid walletId, string customerId, CancellationToken ct);
    Task CreditAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct);
    Task<IReadOnlyList<StatementItem>> StatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct);
    Task<NameEnquiryResult?> NameEnquiryAsync(string accountNumber, CancellationToken ct);
}
public interface ITransferService
{
    Task<TransferResult> TransferInternalAsync(InternalTransferCommand command, CancellationToken ct);
    Task<TransferResult> TransferOutboundAsync(OutboundTransferCommand command, CancellationToken ct);
    Task<TransferResult> TransferInboundAsync(InboundTransferCommand command, CancellationToken ct);
    Task<TransferResult> QueryTransferStatusAsync(Guid transferId, string customerId, CancellationToken ct);
}

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
    public const long LimitKobo = 50_000_000;
    public static bool IsAllowed(long alreadySentKobo, long requestedKobo) =>
        alreadySentKobo >= 0 && requestedKobo > 0 && alreadySentKobo <= LimitKobo && requestedKobo <= LimitKobo - alreadySentKobo;
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
    private const string NovaPrefix = "90";
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
