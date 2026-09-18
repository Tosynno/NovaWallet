using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<SettlementJob> SettlementJobs => Set<SettlementJob>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<DailyOutboundCounter> DailyOutboundCounters => Set<DailyOutboundCounter>();
    public DbSet<ExternalAccount> ExternalAccounts => Set<ExternalAccount>();
    public DbSet<ReconciliationReport> ReconciliationReports => Set<ReconciliationReport>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<User> Users => Set<User>();
    public DbSet<KycDocument> KycDocuments => Set<KycDocument>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Wallet>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AccountNumber).HasMaxLength(10).IsRequired();
            e.Property(x => x.CustomerId).HasMaxLength(100).IsRequired();
            e.Property(x => x.SystemKey).HasMaxLength(50);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.AccountName).HasMaxLength(200).IsRequired(false);
            e.Property(x => x.BalanceKobo).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.AccountNumber).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.SystemKey).IsUnique().HasFilter("[SystemKey] IS NOT NULL");
            e.ToTable(t => t.HasCheckConstraint("CK_Wallet_Balance_NonNegative", "[BalanceKobo] >= 0"));
        });

        b.Entity<Transfer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerId).HasMaxLength(100).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(50).IsRequired();
            e.Property(x => x.DestinationAccountNumber).HasMaxLength(20);
            e.Property(x => x.DestinationBankCode).HasMaxLength(10);
            e.Property(x => x.DestinationAccountName).HasMaxLength(200);
            e.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.SourceWalletId);
            e.ToTable(t => t.HasCheckConstraint("CK_Transfer_Amount_Positive", "[AmountKobo] > 0"));
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Leg).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.WalletId);
            e.HasIndex(x => x.TransferId);
            e.ToTable("LedgerEntries", t => t.HasCheckConstraint("CK_Ledger_Amount_Positive", "[AmountKobo] > 0"));
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(100).IsRequired();
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.Entity).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.ToTable("AuditLogs");
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EventKey).IsUnique();
        });

        b.Entity<SettlementJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TransferId).IsUnique();
            e.HasIndex(x => x.Status);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CustomerId, x.Key }).IsUnique();
        });

        b.Entity<DailyOutboundCounter>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WalletId, x.WatDate }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_DailyCounter_NonNegative", "[TotalKobo] >= 0"));
        });

        b.Entity<ExternalAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AccountKey).HasMaxLength(50).IsRequired();
            e.Property(x => x.BankName).HasMaxLength(100).IsRequired();
            e.Property(x => x.BankCode).HasMaxLength(10).IsRequired();
            e.Property(x => x.AccountNumber).HasMaxLength(20).IsRequired();
            e.Property(x => x.AccountName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.AccountKey).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_ExternalAccount_NonNegative", "[BalanceKobo] >= 0"));
        });

        b.Entity<ReconciliationReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RunBy).HasMaxLength(100).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(500);
            e.HasIndex(x => x.WatDate).IsUnique();
        });

        b.Entity<Channel>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ChannelKey).HasMaxLength(50).IsRequired();
            e.Property(x => x.ChannelName).HasMaxLength(200).IsRequired();
            e.Property(x => x.AppKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.AppSecretHash).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.ChannelKey).IsUnique();
            e.HasIndex(x => x.AppKey).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.PhoneNumber).HasMaxLength(20);
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.CustomerId).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<KycDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DocumentNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.ReviewNotes).HasMaxLength(500);
            e.HasIndex(x => x.UserId);
        });
    }
}
