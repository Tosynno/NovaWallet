using NovaWallet.Domain;

namespace NovaWallet.Application.Repositories;

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}

public interface IUnitOfWork
{
    Task<ITransaction> BeginTransactionAsync(CancellationToken ct);
    Task<int> SaveChangesAsync(CancellationToken ct);
    Task<int> ExecuteSqlInterpolatedAsync(FormattableString sql, CancellationToken ct);
}

public interface IWalletRepository
{
    Task<Wallet?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Wallet?> GetByIdForUpdateAsync(Guid id, CancellationToken ct);
    Task<Wallet?> GetByAccountNumberAsync(string accountNumber, CancellationToken ct);
    Task<Wallet?> GetBySystemKeyForUpdateAsync(string systemKey, CancellationToken ct);
    Task<List<Wallet>> GetCustomersAsync(CancellationToken ct);
    Task<long> GetCustomerSumBalanceAsync(CancellationToken ct);
    Task<long> GetSystemAccountBalanceAsync(string systemKey, CancellationToken ct);
    Task<Wallet> AddAsync(Wallet wallet, CancellationToken ct);
}

public interface ITransferRepository
{
    Task<Transfer?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Transfer?> GetByIdAndCustomerAsync(Guid id, string customerId, CancellationToken ct);
    Task<List<Transfer>> GetSettledOutboundForReconciliationAsync(DateOnly dayStart, DateOnly dayEnd, CancellationToken ct);
    Task<List<Transfer>> GetUnknownOutboundAsync(int take, CancellationToken ct);
    Task<long> GetDailyOutboundTotalAsync(string customerId, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken ct);
    Task<int> CountByIdempotencyKeyAsync(string key, CancellationToken ct);
    Task<IdempotencyRecord?> GetIdempotencyRecordAsync(string customerId, string key, CancellationToken ct);
    Task AddAsync(Transfer transfer, CancellationToken ct);
    Task AddIdempotencyRecordAsync(IdempotencyRecord record, CancellationToken ct);
}

public interface ILedgerEntryRepository
{
    Task<List<StatementItem>> GetStatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct);
    Task AddAsync(LedgerEntry entry, CancellationToken ct);
}

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog entry, CancellationToken ct);
    Task<long> GetMaxIdAsync(CancellationToken ct);
}

public interface IOutboxRepository
{
    Task<List<OutboxMessage>> GetUnpublishedAsync(int take, CancellationToken ct);
    Task AddAsync(OutboxMessage message, CancellationToken ct);
}

public interface ISettlementJobRepository
{
    Task<SettlementJob?> GetByTransferIdAsync(Guid transferId, CancellationToken ct);
    Task<List<SettlementJob>> GetClaimableAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task AddAsync(SettlementJob job, CancellationToken ct);
}

public interface IExternalAccountRepository
{
    Task<ExternalAccount?> GetByKeyAsync(string key, CancellationToken ct);
    Task<ExternalAccount?> GetByKeyForUpdateAsync(string key, CancellationToken ct);
    Task<List<ExternalAccount>> GetAllAsync(CancellationToken ct);
    Task<long> GetBalanceByKeyAsync(string key, CancellationToken ct);
    Task AddAsync(ExternalAccount account, CancellationToken ct);
    Task<bool> AnyByKeyAsync(string key, CancellationToken ct);
}

public interface IReconciliationRepository
{
    Task<ReconciliationReport?> GetByWatDateAsync(DateOnly watDate, CancellationToken ct);
    Task<List<ReconciliationReport>> GetPagedAsync(int page, int pageSize, CancellationToken ct);
    Task<bool> ExistsForWatDateAsync(DateOnly watDate, CancellationToken ct);
    Task AddAsync(ReconciliationReport report, CancellationToken ct);
    Task<bool> HasSettledOutboundAsync(DateOnly dayStart, DateOnly dayEnd, CancellationToken ct);
}

public interface IDailyOutboundCounterRepository
{
    Task<int> MergeCounterAsync(Guid walletId, DateOnly watDate, long amount, long limit, DateTimeOffset now, CancellationToken ct);
}
