using System.Security.Cryptography;
using System.Text.Json;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure;

public sealed class TransferService(
    IUnitOfWork uow,
    IWalletRepository wallets,
    ITransferRepository transfers,
    ILedgerEntryRepository ledgerEntries,
    IAuditLogRepository auditLogs,
    IOutboxRepository outbox,
    ISettlementJobRepository settlementJobs,
    IExternalAccountRepository externalAccounts,
    IDailyOutboundCounterRepository dailyCounters,
    IClock clock,
    FeePolicy fees,
    IPaymentRail rail) : ITransferService
{
    public async Task<TransferResult> TransferInternalAsync(InternalTransferCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) throw new DomainException("idempotency.required", "Idempotency-Key is required.");
        if (command.AmountKobo <= 0) throw new DomainException("amount.invalid", "Amount must be greater than zero.");
        if (command.AmountKobo > DailyOutboundLimitPolicy.LimitKobo) throw new DomainException("amount.too_large", "Single transfer exceeds the daily limit.");

        var hash = IdempotencyFingerprint.Compute(command);
        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var replay = await TryReplayAsync(command.CustomerId, command.IdempotencyKey, hash, ct);
            if (replay is not null) { await tx.CommitAsync(ct); return replay; }

            var source = await wallets.GetByIdForUpdateAsync(command.SourceWalletId, ct)
                ?? throw new DomainException("wallet.source_not_found", "Source wallet not found.");
            if (source.CustomerId != command.CustomerId) throw new DomainException("wallet.forbidden", "Customer does not own source wallet.");
            if (source.BalanceKobo < command.AmountKobo) throw new DomainException("wallet.insufficient_funds", "Insufficient wallet balance.");

            var destLookup = await wallets.GetByAccountNumberAsync(command.DestinationAccountNumber, ct)
                ?? throw new DomainException("wallet.destination_account_not_found", "Destination account number does not exist.");
            if (destLookup.AccountType != AccountType.Customer) throw new DomainException("wallet.destination_not_customer", "Destination must be a customer wallet.");
            if (destLookup.Id == source.Id) throw new DomainException("transfer.same_wallet", "Source and destination wallets must differ.");

            var destination = await wallets.GetByIdForUpdateAsync(destLookup.Id, ct)
                ?? throw new DomainException("wallet.destination_not_found", "Destination wallet not found.");

            var transfer = Transfer.CreateInternal(
                command.CustomerId, source.Id, destination.Id, destination.AccountNumber,
                Money.Create(command.AmountKobo), command.IdempotencyKey, hash, NewReference(), command.CorrelationId, clock.UtcNow);

            source.Debit(Money.Create(command.AmountKobo), clock.UtcNow);
            destination.Credit(Money.Create(command.AmountKobo), clock.UtcNow);

            await transfers.AddAsync(transfer, ct);
            await transfers.AddIdempotencyRecordAsync(IdempotencyRecord.Create(command.CustomerId, command.IdempotencyKey, hash, transfer.Id), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, source.Id, LedgerDirection.Debit, Money.Create(command.AmountKobo), "SourceLedger", clock.UtcNow), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, destination.Id, LedgerDirection.Credit, Money.Create(command.AmountKobo), "DestinationLedger", clock.UtcNow), ct);
            await auditLogs.AddAsync(AuditLog.Create(command.CustomerId, "transfer.internal", "Transfer", transfer.Id.ToString(),
                JsonSerializer.Serialize(new { Source = source.BalanceKobo + command.AmountKobo, Destination = destination.BalanceKobo - command.AmountKobo }),
                JsonSerializer.Serialize(new { Source = source.BalanceKobo, Destination = destination.BalanceKobo }),
                command.CorrelationId, clock.UtcNow), ct);
            await outbox.AddAsync(OutboxMessage.Create("TransferCompleted", transfer.Id.ToString(),
                JsonSerializer.Serialize(new { transfer.Id, transfer.Type, transfer.AmountKobo }), transfer.Id.ToString(), clock.UtcNow), ct);

            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToResult(transfer);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // `await` is not allowed in a catch filter, so the concurrent-duplicate check has to happen
            // inside the catch block itself: two requests racing on the same Idempotency-Key can both pass
            // the pre-check and then collide on the unique index when they commit. Whichever loses the
            // race lands here — roll back, then replay the winner's stored result instead of surfacing
            // a raw DB error for what is really a successful (already-processed) request.
            await tx.RollbackAsync(ct);
            var replay = await TryReplayAsync(command.CustomerId, command.IdempotencyKey, hash, ct);
            if (replay is not null) return replay;
            throw;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<TransferResult> TransferOutboundAsync(OutboundTransferCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) throw new DomainException("idempotency.required", "Idempotency-Key is required.");
        if (command.AmountKobo <= 0) throw new DomainException("amount.invalid", "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(command.DestinationAccountNumber)) throw new DomainException("transfer.destination_required", "Destination account number is required.");
        if (string.IsNullOrWhiteSpace(command.DestinationBankCode)) throw new DomainException("transfer.bank_code_required", "Destination bank code is required.");

        var (feeKobo, vatKobo) = fees.OutboundCharges();
        var totalDebit = checked(command.AmountKobo + feeKobo + vatKobo);
        var hash = IdempotencyFingerprint.Compute(command);

        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var replay = await TryReplayAsync(command.CustomerId, command.IdempotencyKey, hash, ct);
            if (replay is not null) { await tx.CommitAsync(ct); return replay; }

            var affected = await dailyCounters.MergeCounterAsync(command.SourceWalletId, WatBusinessDay.Today(clock.UtcNow),
                command.AmountKobo, DailyOutboundLimitPolicy.LimitKobo, clock.UtcNow, ct);
            if (affected == 0) throw new DomainException("limit.daily_exceeded", "Daily outbound transfer limit exceeded.");

            var source = await wallets.GetByIdForUpdateAsync(command.SourceWalletId, ct)
                ?? throw new DomainException("wallet.source_not_found", "Source wallet not found.");
            if (source.CustomerId != command.CustomerId) throw new DomainException("wallet.forbidden", "Customer does not own source wallet.");
            if (source.BalanceKobo < totalDebit) throw new DomainException("wallet.insufficient_funds", "Insufficient wallet balance for amount + fee + VAT.");

            var settlement = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Settlement, ct)
                ?? throw new DomainException("system_account.missing", "Settlement account is not seeded.");
            var income = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Income, ct)
                ?? throw new DomainException("system_account.missing", "Income account is not seeded.");
            var vat = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Vat, ct)
                ?? throw new DomainException("system_account.missing", "VAT account is not seeded.");

            var transfer = Transfer.CreateOutbound(
                command.CustomerId, source.Id,
                command.DestinationAccountNumber, command.DestinationBankCode, command.DestinationAccountName,
                Money.Create(command.AmountKobo), Money.Create(feeKobo), Money.Create(vatKobo),
                command.IdempotencyKey, hash, NewReference(), command.CorrelationId, clock.UtcNow);

            source.Debit(Money.Create(totalDebit), clock.UtcNow);
            settlement.Credit(Money.Create(command.AmountKobo), clock.UtcNow);
            income.Credit(Money.Create(feeKobo), clock.UtcNow);
            vat.Credit(Money.Create(vatKobo), clock.UtcNow);

            await transfers.AddAsync(transfer, ct);
            await transfers.AddIdempotencyRecordAsync(IdempotencyRecord.Create(command.CustomerId, command.IdempotencyKey, hash, transfer.Id), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, source.Id, LedgerDirection.Debit, Money.Create(totalDebit), "SourceLedger", clock.UtcNow), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, settlement.Id, LedgerDirection.Credit, Money.Create(command.AmountKobo), "Settlement", clock.UtcNow), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, income.Id, LedgerDirection.Credit, Money.Create(feeKobo), "Income", clock.UtcNow), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, vat.Id, LedgerDirection.Credit, Money.Create(vatKobo), "Vat", clock.UtcNow), ct);
            await auditLogs.AddAsync(AuditLog.Create(command.CustomerId, "transfer.outbound", "Transfer", transfer.Id.ToString(),
                JsonSerializer.Serialize(new { Source = source.BalanceKobo + totalDebit }),
                JsonSerializer.Serialize(new { Source = source.BalanceKobo, Settlement = settlement.BalanceKobo, Income = income.BalanceKobo, Vat = vat.BalanceKobo }),
                command.CorrelationId, clock.UtcNow), ct);
            await outbox.AddAsync(OutboxMessage.Create("TransferOutboundSubmitted", transfer.Id.ToString(),
                JsonSerializer.Serialize(new { transfer.Id, transfer.AmountKobo, transfer.DestinationBankCode, transfer.DestinationAccountNumber }),
                transfer.Id.ToString(), clock.UtcNow), ct);
            await settlementJobs.AddAsync(SettlementJob.Create(transfer.Id, clock.UtcNow), ct);

            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToResult(transfer);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Same reasoning as in TransferInternalAsync: handle the idempotency-key race inside the
            // catch block rather than in an (illegal) async catch filter.
            await tx.RollbackAsync(ct);
            var replay = await TryReplayAsync(command.CustomerId, command.IdempotencyKey, hash, ct);
            if (replay is not null) return replay;
            throw;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<TransferResult> TransferInboundAsync(InboundTransferCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) throw new DomainException("idempotency.required", "Idempotency-Key is required.");
        if (command.AmountKobo <= 0) throw new DomainException("amount.invalid", "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(command.DestinationAccountNumber)) throw new DomainException("transfer.destination_required", "Destination account number is required.");

        var hash = IdempotencyFingerprint.Compute(command);

        // Resolve the destination wallet first so we can use its CustomerId for idempotency lookup.
        var destLookup = await wallets.GetByAccountNumberAsync(command.DestinationAccountNumber, ct)
            ?? throw new DomainException("wallet.destination_account_not_found", "Destination account number does not exist.");
        if (destLookup.AccountType != AccountType.Customer) throw new DomainException("wallet.destination_not_customer", "Destination must be a customer wallet.");

        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            var existing = await transfers.GetIdempotencyRecordAsync(destLookup.CustomerId, command.IdempotencyKey, ct);
            if (existing is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existing.RequestHash), Convert.FromHexString(hash)))
                    throw new DomainException("idempotency.payload_mismatch", "Idempotency-Key was already used with a different payload.");
                var prior = await transfers.GetByIdAsync(existing.TransferId, ct) ?? throw new DomainException("transfer.not_found", "Original transfer not found.");
                await tx.CommitAsync(ct);
                return ToResult(prior);
            }

            var dest = await wallets.GetByIdForUpdateAsync(destLookup.Id, ct)
                ?? throw new DomainException("wallet.destination_not_found", "Destination wallet not found.");
            var settlement = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Settlement, ct)
                ?? throw new DomainException("system_account.missing", "Settlement account is not seeded.");
            var extLedger = await externalAccounts.GetByKeyForUpdateAsync(ExternalAccountKeys.LedgerHolding, ct)
                ?? throw new DomainException("external_account.missing", "Ledger holding account is not seeded.");

            var transfer = Transfer.CreateInbound(
                dest.CustomerId, dest.Id, dest.AccountNumber,
                command.OriginatorBankCode, command.OriginatorAccountNumber, command.OriginatorAccountName,
                Money.Create(command.AmountKobo), command.IdempotencyKey, hash, NewReference(), command.CorrelationId, clock.UtcNow);

            settlement.Debit(Money.Create(command.AmountKobo), clock.UtcNow);
            dest.Credit(Money.Create(command.AmountKobo), clock.UtcNow);
            extLedger.Credit(Money.Create(command.AmountKobo), clock.UtcNow);

            await transfers.AddAsync(transfer, ct);
            await transfers.AddIdempotencyRecordAsync(IdempotencyRecord.Create(dest.CustomerId, command.IdempotencyKey, hash, transfer.Id), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, settlement.Id, LedgerDirection.Debit, Money.Create(command.AmountKobo), "InboundSettlement", clock.UtcNow), ct);
            await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, dest.Id, LedgerDirection.Credit, Money.Create(command.AmountKobo), "InboundCustomer", clock.UtcNow), ct);
            await auditLogs.AddAsync(AuditLog.Create("NIP-INBOUND", "transfer.inbound", "Transfer", transfer.Id.ToString(),
                JsonSerializer.Serialize(new { Settlement = settlement.BalanceKobo + command.AmountKobo, Customer = dest.BalanceKobo - command.AmountKobo }),
                JsonSerializer.Serialize(new { Settlement = settlement.BalanceKobo, Customer = dest.BalanceKobo }),
                command.CorrelationId, clock.UtcNow), ct);
            await outbox.AddAsync(OutboxMessage.Create("TransferInboundReceived", transfer.Id.ToString(),
                JsonSerializer.Serialize(new { transfer.Id, transfer.AmountKobo, transfer.DestinationAccountNumber }),
                transfer.Id.ToString(), clock.UtcNow), ct);

            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToResult(transfer);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<TransferResult> QueryTransferStatusAsync(Guid transferId, string customerId, CancellationToken ct)
    {
        var transfer = await transfers.GetByIdAndCustomerAsync(transferId, customerId, ct)
            ?? throw new DomainException("transfer.not_found", "Transfer not found.");
        if (transfer.Type != TransferType.Outbound) return ToResult(transfer);
        if (transfer.Status is TransferStatus.Settled or TransferStatus.Failed) return ToResult(transfer);
        if (string.IsNullOrEmpty(transfer.ExternalReference)) return ToResult(transfer);

        var nipStatus = await rail.GetStatusAsync(transfer.ExternalReference, ct);
        var now = clock.UtcNow;

        await using var tx = await uow.BeginTransactionAsync(ct);
        try
        {
            if (nipStatus.Status == PaymentRailStatus.Sent)
            {
                transfer.MarkSettled(now);
                var job = await settlementJobs.GetByTransferIdAsync(transfer.Id, ct);
                if (job is not null) job.Succeed(transfer.ExternalReference!, now);
            }
            else if (nipStatus.Status == PaymentRailStatus.Failed)
            {
                transfer.MarkFailed(now);
                await ReverseOutboundAsync(transfer, now, ct);
            }
            await uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
        return ToResult(transfer);
    }

    private async Task<TransferResult?> TryReplayAsync(string customerId, string key, string hash, CancellationToken ct)
    {
        var existing = await transfers.GetIdempotencyRecordAsync(customerId, key, ct);
        if (existing is null) return null;
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existing.RequestHash), Convert.FromHexString(hash)))
            throw new DomainException("idempotency.payload_mismatch", "Idempotency-Key was already used with a different payload.");
        var prior = await transfers.GetByIdAsync(existing.TransferId, ct) ?? throw new DomainException("transfer.not_found", "Original transfer not found.");
        return ToResult(prior);
    }

    private async Task ReverseOutboundAsync(Transfer transfer, DateTimeOffset now, CancellationToken ct)
    {
        var source = await wallets.GetByIdForUpdateAsync(transfer.SourceWalletId, ct)
            ?? throw new DomainException("wallet.source_not_found", "Source wallet not found.");
        var settlement = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Settlement, ct)
            ?? throw new DomainException("system_account.missing", "Settlement account is not seeded.");
        var income = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Income, ct)
            ?? throw new DomainException("system_account.missing", "Income account is not seeded.");
        var vat = await wallets.GetBySystemKeyForUpdateAsync(SystemAccountKeys.Vat, ct)
            ?? throw new DomainException("system_account.missing", "VAT account is not seeded.");

        source.Credit(Money.Create(transfer.TotalDebitKobo), now);
        settlement.Debit(Money.Create(transfer.AmountKobo), now);
        income.Debit(Money.Create(transfer.FeeKobo), now);
        vat.Debit(Money.Create(transfer.VatKobo), now);

        await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, source.Id, LedgerDirection.Credit, Money.Create(transfer.TotalDebitKobo), "ReversalSource", now), ct);
        await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, settlement.Id, LedgerDirection.Debit, Money.Create(transfer.AmountKobo), "ReversalSettlement", now), ct);
        await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, income.Id, LedgerDirection.Debit, Money.Create(transfer.FeeKobo), "ReversalIncome", now), ct);
        await ledgerEntries.AddAsync(LedgerEntry.Create(transfer.Id, vat.Id, LedgerDirection.Debit, Money.Create(transfer.VatKobo), "ReversalVat", now), ct);
        await auditLogs.AddAsync(AuditLog.Create("SYSTEM", "transfer.outbound.reversed", "Transfer", transfer.Id.ToString(),
            JsonSerializer.Serialize(new { transfer.Status }),
            JsonSerializer.Serialize(new { Status = nameof(TransferStatus.Failed) }),
            transfer.CorrelationId, now), ct);
    }

    private static string NewReference() => "NV" + Guid.NewGuid().ToString("N").ToUpperInvariant()[..12];
    private static TransferResult ToResult(Transfer t) =>
        new(t.Id, t.Type, t.Status, t.AmountKobo, t.FeeKobo, t.VatKobo, t.TotalDebitKobo, t.Reference, t.ExternalReference);
}
