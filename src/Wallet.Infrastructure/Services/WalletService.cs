using System.Security.Cryptography;
using System.Text.Json;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public sealed class WalletService(
    IUnitOfWork uow,
    IWalletRepository wallets,
    IExternalAccountRepository externalAccounts,
    ILedgerEntryRepository ledgerEntries,
    IAuditLogRepository auditLogs,
    IClock clock) : IWalletService
{
    public async Task<WalletCreatedResult> CreateAsync(string customerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var accountNumber = AccountNumberGenerator.Generate();
            var wallet = Wallet.CreateCustomer(customerId, accountNumber, clock.UtcNow);
            try
            {
                await wallets.AddAsync(wallet, ct);
                return new(wallet.Id, wallet.AccountNumber, wallet.CustomerId, wallet.Currency, wallet.BalanceKobo);
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

    public async Task CreditAsync(Guid walletId, long amountKobo, string actor, string correlationId, CancellationToken ct)
    {
        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var wallet = await wallets.GetByIdForUpdateAsync(walletId, ct)
                ?? throw new DomainException("wallet.not_found", "Wallet not found.");
            if (wallet.AccountType != AccountType.Customer) throw new DomainException("wallet.not_customer", "Only customer wallets can be credited directly.");

            var before = wallet.BalanceKobo;
            wallet.Credit(Money.Create(amountKobo), clock.UtcNow);
            await ledgerEntries.AddAsync(LedgerEntry.Create(null, wallet.Id, LedgerDirection.Credit, Money.Create(amountKobo), "Deposit", clock.UtcNow), ct);

            var ledgerHolding = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.LedgerHolding, ct)
                ?? throw new DomainException("external_account.missing", "Ledger holding account is not seeded.");
            ledgerHolding.Credit(Money.Create(amountKobo), clock.UtcNow);

            await auditLogs.AddAsync(AuditLog.Create(actor, "wallet.credit", "Wallet", wallet.Id.ToString(),
                JsonSerializer.Serialize(new { BalanceKobo = before }),
                JsonSerializer.Serialize(new { wallet.BalanceKobo, LedgerHolding = ledgerHolding.BalanceKobo }),
                correlationId, clock.UtcNow), ct);
            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<IReadOnlyList<StatementItem>> StatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct) =>
        await ledgerEntries.GetStatementAsync(walletId, page, pageSize, ct);

    public async Task<NameEnquiryResult?> NameEnquiryAsync(string accountNumber, CancellationToken ct)
    {
        var wallet = await wallets.GetByAccountNumberAsync(accountNumber, ct);
        if (wallet is null || wallet.AccountType != AccountType.Customer) return null;
        return new NameEnquiryResult(wallet.AccountNumber, wallet.CustomerId, "011", "First Bank of Nigeria");
    }
}
