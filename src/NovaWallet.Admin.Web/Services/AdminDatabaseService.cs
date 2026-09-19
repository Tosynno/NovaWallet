using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.Admin.Web.Services;

public sealed class AdminDatabaseService(AppDbContext db)
{
    public async Task<List<ExternalAccountDto>> GetExternalAccountsAsync() =>
        await db.ExternalAccounts.AsNoTracking()
            .Select(x => new ExternalAccountDto(x.AccountKey, x.BankName, x.BankCode, x.AccountNumber, x.AccountName, x.BalanceKobo))
            .ToListAsync();

    public async Task<List<TransferDto>> GetRecentTransfersAsync(int take = 50) =>
        await db.Transfers.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(take)
            .Select(x => new TransferDto(x.Id, x.Type.ToString(), x.Status.ToString(), x.AmountKobo, x.FeeKobo, x.VatKobo, x.TotalDebitKobo, x.Reference, x.ExternalReference, x.DestinationAccountNumber, x.DestinationBankCode, x.CustomerId, x.CreatedAt, x.UpdatedAt))
            .ToListAsync();

    public async Task<TransferDto?> GetTransferByIdAsync(Guid id) =>
        await db.Transfers.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new TransferDto(x.Id, x.Type.ToString(), x.Status.ToString(), x.AmountKobo, x.FeeKobo, x.VatKobo, x.TotalDebitKobo, x.Reference, x.ExternalReference, x.DestinationAccountNumber, x.DestinationBankCode, x.CustomerId, x.CreatedAt, x.UpdatedAt))
            .SingleOrDefaultAsync();

    public async Task<List<SettlementJobDto>> GetSettlementJobsAsync(int take = 50) =>
        await db.SettlementJobs.AsNoTracking().OrderByDescending(x => x.Id).Take(take)
            .Join(db.Transfers, j => j.TransferId, t => t.Id, (j, t) => new SettlementJobDto(j.Id, j.TransferId, j.Status.ToString(), j.ProviderReference, j.AttemptCount, j.NextAttemptAt, j.LeaseUntil, j.LastError, t.AmountKobo, t.CustomerId))
            .ToListAsync();

    public async Task<List<ChannelDto>> GetChannelsAsync() =>
        await db.Channels.AsNoTracking()
            .Select(x => new ChannelDto(x.ChannelKey, x.ChannelName, x.AppKey, x.Status.ToString(), x.CreatedAt))
            .ToListAsync();

    public async Task<List<UserDto>> GetUsersAsync(int take = 50) =>
        await db.Users.AsNoTracking().OrderByDescending(x => x.Id).Take(take)
            .Select(x => new UserDto(x.Id, x.CustomerId, x.Email, x.PhoneNumber, x.FirstName, x.LastName, x.KycStatus.ToString(), x.CreatedAt))
            .ToListAsync();

    public async Task<DashboardStatsDto> GetDashboardStatsAsync()
    {
        var totalTransfers = await db.Transfers.CountAsync();
        var settledCount = await db.Transfers.CountAsync(x => x.Status == TransferStatus.Settled);
        var failedCount = await db.Transfers.CountAsync(x => x.Status == TransferStatus.Failed);
        var unknownCount = await db.Transfers.CountAsync(x => x.Status == TransferStatus.Unknown);
        var totalOutboundAmount = await db.Transfers.Where(x => x.Type == TransferType.Outbound && x.Status == TransferStatus.Settled).SumAsync(x => (long?)x.AmountKobo) ?? 0;
        var customerCount = await db.Wallets.CountAsync(x => x.AccountType == AccountType.Customer);
        var channelCount = await db.Channels.CountAsync();
        var pendingJobs = await db.SettlementJobs.CountAsync(x => x.Status == SettlementStatus.Pending || x.Status == SettlementStatus.Processing);
        return new DashboardStatsDto(totalTransfers, settledCount, failedCount, unknownCount, totalOutboundAmount, customerCount, channelCount, pendingJobs);
    }

    public async Task<bool> RepostFailedTransferAsync(Guid transferId)
    {
        var transfer = await db.Transfers.SingleOrDefaultAsync(x => x.Id == transferId);
        if (transfer is null || transfer.Status != TransferStatus.Failed || transfer.Type != TransferType.Outbound) return false;
        var now = DateTimeOffset.UtcNow;
        transfer.ResetForRepost(now);
        var job = await db.SettlementJobs.SingleOrDefaultAsync(x => x.TransferId == transferId);
        job?.Repost(now);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<CreditResultDto?> CreditAccountAsync(string accountNumber, long amountKobo, string actor)
    {
        if (string.IsNullOrWhiteSpace(accountNumber) || amountKobo <= 0) return null;

        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.AccountNumber == accountNumber && x.AccountType == AccountType.Customer);
        if (wallet is null) return null;

        var settlement = await db.Wallets.SingleAsync(x => x.SystemKey == SystemAccountKeys.Settlement);
        var ledgerHolding = await db.ExternalAccounts.SingleAsync(x => x.AccountKey == ExternalAccountKeys.LedgerHolding);
        var extSettlementHolding = await db.ExternalAccounts.SingleAsync(x => x.AccountKey == ExternalAccountKeys.SettlementHolding);

        var now = DateTimeOffset.UtcNow;
        var before = wallet.BalanceKobo;
        wallet.Credit(Money.Create(amountKobo), now);
        settlement.Debit(Money.Create(amountKobo), now);
        ledgerHolding.Credit(Money.Create(amountKobo), now);
        extSettlementHolding.Debit(Money.Create(amountKobo), now);

        db.LedgerEntries.Add(LedgerEntry.Create(null, wallet.Id, LedgerDirection.Credit, Money.Create(amountKobo), "AdminCredit", now));
        db.LedgerEntries.Add(LedgerEntry.Create(null, settlement.Id, LedgerDirection.Debit, Money.Create(amountKobo), "SettlementOut", now));
        db.AuditLogs.Add(AuditLog.Create(actor, "wallet.admin_credit", "Wallet", wallet.Id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(new { BalanceKobo = before, Settlement = settlement.BalanceKobo + amountKobo }),
            System.Text.Json.JsonSerializer.Serialize(new { wallet.BalanceKobo, Settlement = settlement.BalanceKobo, LedgerHolding = ledgerHolding.BalanceKobo }),
            Guid.NewGuid().ToString("N"), now));

        await db.SaveChangesAsync();
        return new CreditResultDto(wallet.AccountNumber, wallet.Currency, wallet.BalanceKobo, settlement.BalanceKobo, ledgerHolding.BalanceKobo);
    }

    public async Task<ChannelDto> CreateChannelAsync(string channelKey, string channelName)
    {
        var appKey = "AK" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
        var appSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var secretHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(appSecret)));
        var channel = Channel.Create(channelKey, channelName, appKey, secretHash, DateTimeOffset.UtcNow);
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return new ChannelDto(channel.ChannelKey, channel.ChannelName, channel.AppKey, channel.Status.ToString(), channel.CreatedAt) { AppSecret = appSecret };
    }

    public async Task<ChannelDto?> RotateChannelKeysAsync(string channelKey)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.ChannelKey == channelKey);
        if (channel is null) return null;
        var appKey = "AK" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
        var appSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var secretHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(appSecret)));
        channel.RotateKeys(appKey, secretHash, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return new ChannelDto(channel.ChannelKey, channel.ChannelName, channel.AppKey, channel.Status.ToString(), channel.CreatedAt) { AppSecret = appSecret };
    }

    public async Task<bool> UpdateChannelStatusAsync(string channelKey, string status)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.ChannelKey == channelKey);
        if (channel is null) return false;
        var now = DateTimeOffset.UtcNow;
        if (status == "Active") channel.Activate(now);
        else if (status == "Suspended") channel.Suspend(now);
        else if (status == "Revoked") channel.Revoke(now);
        else return false;
        await db.SaveChangesAsync();
        return true;
    }

    public record ExternalAccountDto(string AccountKey, string BankName, string BankCode, string AccountNumber, string AccountName, long BalanceKobo);
    public record TransferDto(Guid Id, string Type, string Status, long AmountKobo, long FeeKobo, long VatKobo, long TotalDebitKobo, string Reference, string? ExternalReference, string? DestinationAccountNumber, string? DestinationBankCode, string CustomerId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public record SettlementJobDto(long Id, Guid TransferId, string Status, string? ProviderReference, int AttemptCount, DateTimeOffset NextAttemptAt, DateTimeOffset? LeaseUntil, string? LastError, long AmountKobo, string CustomerId);
    public record ChannelDto(string ChannelKey, string ChannelName, string AppKey, string Status, DateTimeOffset CreatedAt) { public string? AppSecret { get; init; } }
    public record UserDto(long Id, string CustomerId, string Email, string? PhoneNumber, string FirstName, string LastName, string KycStatus, DateTimeOffset CreatedAt);
    public record DashboardStatsDto(int TotalTransfers, int SettledCount, int FailedCount, int UnknownCount, long TotalOutboundAmount, int CustomerCount, int ChannelCount, int PendingJobs);
    public record CreditResultDto(string AccountNumber, string Currency, long BalanceKobo, long SettlementBalanceKobo, long LedgerHoldingBalanceKobo);
}
