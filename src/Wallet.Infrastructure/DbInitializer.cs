using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public static class DbInitializer
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static async Task InitializeAsync(AppDbContext db, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(db, ct);
        }
        finally
        {
            Lock.Release();
        }
    }

    private static async Task InitializeCoreAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.EnsureCreatedAsync(ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2714 || ex.Message.Contains("already an object"))
        {
            await db.Database.EnsureDeletedAsync(ct);
            await db.Database.EnsureCreatedAsync(ct);
        }

        await SeedSystemAccountsAsync(db, ct);
        await SeedExternalAccountsAsync(db, ct);

        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'trg_AuditLogs_Immutable', N'TR') IS NOT NULL DROP TRIGGER trg_AuditLogs_Immutable;
            EXEC('CREATE TRIGGER trg_AuditLogs_Immutable ON AuditLogs
                  FOR UPDATE, DELETE
                  AS
                  BEGIN
                      RAISERROR(''AuditLog is append-only'', 16, 1);
                      ROLLBACK TRANSACTION;
                  END');
            """, ct);
    }

    private static async Task SeedSystemAccountsAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(db, SystemAccountKeys.Settlement, "0000000001", AccountType.Settlement, now, ct);
        await SeedAsync(db, SystemAccountKeys.Vat, "0000000002", AccountType.Vat, now, ct);
        await SeedAsync(db, SystemAccountKeys.Income, "0000000003", AccountType.Income, now, ct);
    }

    private static async Task SeedAsync(AppDbContext db, string key, string accountNumber, AccountType type, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Wallets.AnyAsync(x => x.SystemKey == key || x.AccountNumber == accountNumber, ct)) return;
        try
        {
            db.Wallets.Add(Wallet.CreateSystem(type, key, accountNumber, now));
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    public static Guid SettlementId(AppDbContext db) => db.Wallets.Local.First(x => x.SystemKey == SystemAccountKeys.Settlement).Id;
    public static Guid IncomeId(AppDbContext db) => db.Wallets.Local.First(x => x.SystemKey == SystemAccountKeys.Income).Id;
    public static Guid VatId(AppDbContext db) => db.Wallets.Local.First(x => x.SystemKey == SystemAccountKeys.Vat).Id;

    private static async Task SeedExternalAccountsAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        const string firstBank = "First Bank of Nigeria";
        const string firstBankCode = "011";
        await SeedExternalAsync(db, ExternalAccountKeys.LedgerHolding, ExternalAccountType.LedgerHolding, firstBank, firstBankCode, "7000000001", "NovaWallet Ledger Holding", now, ct);
        await SeedExternalAsync(db, ExternalAccountKeys.SettlementHolding, ExternalAccountType.SettlementHolding, firstBank, firstBankCode, "7000000002", "NovaWallet Settlement Holding", now, ct);
        await SeedExternalAsync(db, ExternalAccountKeys.IncomeHolding, ExternalAccountType.IncomeHolding, firstBank, firstBankCode, "7000000003", "NovaWallet Income Holding", now, ct);
        await SeedExternalAsync(db, ExternalAccountKeys.VatHolding, ExternalAccountType.VatHolding, firstBank, firstBankCode, "7000000004", "NovaWallet VAT Holding", now, ct);
    }

    private static async Task SeedExternalAsync(AppDbContext db, string key, ExternalAccountType type, string bankName, string bankCode, string accountNumber, string accountName, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.ExternalAccounts.AnyAsync(x => x.AccountKey == key, ct)) return;
        try
        {
            db.ExternalAccounts.Add(ExternalAccount.Create(key, type, bankName, bankCode, accountNumber, accountName, now));
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }
}
