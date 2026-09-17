using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Repositories;

namespace NovaWallet.IntegrationTests;

public sealed class TestDb : IAsyncDisposable
{
    public AppDbContext Db { get; }
    private readonly string _connection;
    private readonly string _dbName;

    private TestDb(AppDbContext db, string connection, string dbName) { Db = db; _connection = connection; _dbName = dbName; }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOVAWALLET_TEST_CONNECTION"));

    public static async Task<TestDb> CreateAsync()
    {
        var connection = Environment.GetEnvironmentVariable("NOVAWALLET_TEST_CONNECTION")!;
        var dbName = $"novawallet_test_{Guid.NewGuid():N}";
        var masterConn = ReplaceDatabase(connection, "master");
        await using (var master = new SqlConnection(masterConn))
        {
            await master.OpenAsync();
            await using var cmd = master.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{dbName}]";
            await cmd.ExecuteNonQueryAsync();
        }
        var testConn = ReplaceDatabase(connection, dbName);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(testConn).Options;
        var db = new AppDbContext(options);
        await DbInitializer.InitializeAsync(db, CancellationToken.None);
        return new TestDb(db, testConn, dbName);
    }

    public static AppDbContext NewContext(string connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        return new AppDbContext(options);
    }

    public string TestConnection => _connection;

    private static string ReplaceDatabase(string conn, string db)
    {
        var b = new SqlConnectionStringBuilder(conn) { InitialCatalog = db };
        return b.ConnectionString;
    }

    public WalletService CreateWalletService(IClock? clock = null) =>
        new(new UnitOfWork(Db), new WalletRepository(Db), new ExternalAccountRepository(Db),
            new LedgerEntryRepository(Db), new AuditLogRepository(Db), clock ?? new SystemClock());

    public TransferService CreateTransferService(IClock? clock = null, FeePolicy? fees = null) =>
        new(new UnitOfWork(Db), new WalletRepository(Db), new TransferRepository(Db),
            new LedgerEntryRepository(Db), new AuditLogRepository(Db), new OutboxRepository(Db),
            new SettlementJobRepository(Db), new ExternalAccountRepository(Db),
            new DailyOutboundCounterRepository(Db), clock ?? new SystemClock(),
            fees ?? new FeePolicy(50_000, 0.075m), new MockNipPaymentRail());

    public ReconciliationService CreateReconciliationService(IClock? clock = null) =>
        new(new UnitOfWork(Db), new ReconciliationRepository(Db), new TransferRepository(Db),
            new WalletRepository(Db), new ExternalAccountRepository(Db),
            new LedgerEntryRepository(Db), new AuditLogRepository(Db), clock ?? new SystemClock());

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        var masterConn = ReplaceDatabase(_connection, "master");
        await using var master = new SqlConnection(masterConn);
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]";
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    public static WalletService CreateWalletService(AppDbContext db, IClock? clock = null) =>
        new(new UnitOfWork(db), new WalletRepository(db), new ExternalAccountRepository(db),
            new LedgerEntryRepository(db), new AuditLogRepository(db), clock ?? new SystemClock());

    public static TransferService CreateTransferService(AppDbContext db, IClock? clock = null, FeePolicy? fees = null) =>
        new(new UnitOfWork(db), new WalletRepository(db), new TransferRepository(db),
            new LedgerEntryRepository(db), new AuditLogRepository(db), new OutboxRepository(db),
            new SettlementJobRepository(db), new ExternalAccountRepository(db),
            new DailyOutboundCounterRepository(db), clock ?? new SystemClock(),
            fees ?? new FeePolicy(50_000, 0.075m), new MockNipPaymentRail());

    public static ReconciliationService CreateReconciliationService(AppDbContext db, IClock? clock = null) =>
        new(new UnitOfWork(db), new ReconciliationRepository(db), new TransferRepository(db),
            new WalletRepository(db), new ExternalAccountRepository(db),
            new LedgerEntryRepository(db), new AuditLogRepository(db), clock ?? new SystemClock());
}
