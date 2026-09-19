using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public sealed class WalletService(
    IUnitOfWork uow,
    IWalletRepository wallets,
    ILedgerEntryRepository ledgerEntries,
    IAuditLogRepository auditLogs,
    IClock clock,
    AppDbContext db) : IWalletService
{
    public async Task<WalletCreatedResult> CreateAsync(string customerId, string currency, string? accountName, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var accountNumber = AccountNumberGenerator.Generate();
            var wallet = Wallet.CreateCustomer(customerId, accountNumber, clock.UtcNow, currency, accountName);
            try
            {
                await wallets.AddAsync(wallet, ct);
                return new(wallet.Id, wallet.AccountNumber, wallet.CustomerId, wallet.Currency, wallet.AccountName, wallet.BalanceKobo);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException) when (attempt < 4) { }
        }
        throw new DomainException("wallet.account_number_unavailable", "Could not allocate a unique account number.");
    }

    public async Task<BalanceResult?> GetAsync(Guid walletId, string customerId, CancellationToken ct)
    {
        var wallet = await wallets.GetByIdAsync(walletId, ct);
        if (wallet is null || wallet.CustomerId != customerId) return null;
        return new(wallet.AccountNumber, wallet.Currency, wallet.BalanceKobo);
    }

    public async Task<IReadOnlyList<WalletSummaryResult>> ListByCustomerAsync(string customerId, CancellationToken ct)
    {
        return await db.Wallets.AsNoTracking()
            .Where(x => x.CustomerId == customerId && x.AccountType == AccountType.Customer)
            .Select(x => new WalletSummaryResult(x.Id, x.AccountNumber, x.Currency, x.AccountName, x.BalanceKobo))
            .ToListAsync(ct);
    }

    public async Task<BalanceResult> CreditAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct)
    {
        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var wallet = await wallets.GetByIdForUpdateAsync(walletId, ct)
                ?? throw new DomainException("wallet.not_found", "Wallet not found.");
            if (wallet.AccountType != AccountType.Customer) throw new DomainException("wallet.not_customer", "Only customer wallets can be credited directly.");

            var settlement = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Settlement, ct)
                ?? throw new DomainException("system_account.missing", "Settlement account is not seeded.");

            var before = wallet.BalanceKobo;
            wallet.Credit(Money.Create(amountKobo), clock.UtcNow);
            settlement.Debit(Money.Create(amountKobo), clock.UtcNow);

            await ledgerEntries.AddAsync(LedgerEntry.Create(null, wallet.Id, LedgerDirection.Credit, Money.Create(amountKobo), "Deposit", clock.UtcNow), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(null, settlement.Id, LedgerDirection.Debit, Money.Create(amountKobo), "SettlementOut", clock.UtcNow), ct);

            await auditLogs.AddAsync(AuditLog.Create(actor, "wallet.credit", "Wallet", wallet.Id.ToString(),
                JsonSerializer.Serialize(new { BalanceKobo = before, Settlement = settlement.BalanceKobo + amountKobo }),
                JsonSerializer.Serialize(new { wallet.BalanceKobo, Settlement = settlement.BalanceKobo }),
                correlationId, clock.UtcNow), ct);
            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new BalanceResult(wallet.AccountNumber, wallet.Currency, wallet.BalanceKobo);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<BalanceResult> CreditSettlementAsync(long amountKobo, string actor, string correlationId, CancellationToken ct)
    {
        if (amountKobo <= 0) throw new DomainException("amount.invalid", "Amount must be greater than zero.");

        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var settlement = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Settlement, ct)
                ?? throw new DomainException("system_account.missing", "Settlement account is not seeded.");
            var extSettlementHolding = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.SettlementHolding, ct)
                ?? throw new DomainException("external_account.missing", "Settlement holding account is not seeded.");

            settlement.Credit(Money.Create(amountKobo), clock.UtcNow);
            extSettlementHolding.Credit(Money.Create(amountKobo), clock.UtcNow);

            await ledgerEntries.AddAsync(LedgerEntry.Create(null, settlement.Id, LedgerDirection.Credit, Money.Create(amountKobo), "SettlementFunding", clock.UtcNow), ct);

            await auditLogs.AddAsync(AuditLog.Create(actor, "settlement.credit", "Wallet", settlement.Id.ToString(),
                JsonSerializer.Serialize(new { SettlementBalance = settlement.BalanceKobo - amountKobo }),
                JsonSerializer.Serialize(new { SettlementBalance = settlement.BalanceKobo, ExtSettlementBalance = extSettlementHolding.BalanceKobo }),
                correlationId, clock.UtcNow), ct);
            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new BalanceResult(settlement.AccountNumber, settlement.Currency, settlement.BalanceKobo);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<IReadOnlyList<StatementItem>> StatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct) =>
        await ledgerEntries.GetStatementAsync(walletId, page, pageSize, ct);

    public async Task<NameEnquiryResult?> NameEnquiryAsync(string accountNumber, CancellationToken ct)
    {
        var wallet = await wallets.GetByAccountNumberAsync(accountNumber, ct);
        if (wallet is null || wallet.AccountType != AccountType.Customer) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == wallet.CustomerId, ct);
        var accountName = user is not null ? $"{user.FirstName} {user.LastName}" : wallet.CustomerId;
        return new NameEnquiryResult(wallet.AccountNumber, accountName, "011", "First Bank of Nigeria");
    }
}
