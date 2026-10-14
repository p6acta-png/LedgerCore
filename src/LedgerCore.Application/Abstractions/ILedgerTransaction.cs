using LedgerCore.Application.Idempotency;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Wallets;

namespace LedgerCore.Application.Abstractions;

/// <summary>
/// Runs a unit of work inside exactly one database transaction.
///
/// Contract:
///   * READ COMMITTED isolation; consistency of balances comes from explicit row locks
///     (<see cref="ILedgerTransaction.LockAccountsAsync"/>), not from the isolation level.
///   * If <paramref name="work"/> returns normally the transaction is committed; if it throws, it is
///     rolled back, so no partial journal entry can ever be committed.
///   * Transient failures (deadlock, serialization failure, dropped connection) roll back and re-run the
///     whole delegate in a fresh transaction, a bounded number of times. The delegate must therefore not
///     carry state between attempts. Because every ledger command claims its idempotency key as the
///     first statement, re-running after an ambiguous commit replays the stored result instead of
///     posting twice.
/// </summary>
public interface ILedgerTransactionRunner
{
    Task<T> ExecuteAsync<T>(
        string operationName,
        Func<ILedgerTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

/// <summary>
/// The operations available inside one ledger database transaction. Every method executes on the same
/// connection and transaction. Implemented with parameterized SQL in the infrastructure layer.
/// </summary>
public interface ILedgerTransaction
{
    /// <summary>
    /// Inserts the idempotency record for this key, or, if one already exists, waits for the
    /// transaction that owns it to finish and returns its stored outcome. Backed by a primary-key
    /// constraint, so concurrent duplicates across any number of API instances serialize here.
    /// </summary>
    Task<IdempotencyClaim> ClaimIdempotencyKeyAsync(
        IdempotencyScope scope, string requestHash, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Stores the final outcome on the record claimed earlier in this transaction.</summary>
    Task CompleteIdempotencyKeyAsync(
        IdempotencyScope scope, StoredOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Locks (SELECT ... FOR UPDATE) the ledger accounts behind the given wallets plus any system
    /// accounts, in one statement ordered by account id. A single global lock order means two
    /// transactions can never wait for each other in a cycle, so these locks cannot deadlock.
    /// Wallet status is read after the locks are held. Unknown wallets are simply absent from the result.
    /// </summary>
    Task<LockedLedgerState> LockAccountsAsync(
        IReadOnlyCollection<WalletId> walletIds,
        IReadOnlyCollection<LedgerAccountId> systemAccountIds,
        CancellationToken cancellationToken);

    /// <summary>Appends the journal entry and all of its postings.</summary>
    Task InsertJournalEntryAsync(JournalEntry entry, string createdBy, string? correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the cached-balance deltas computed by the domain and verifies the database arrived at the
    /// same balances. Must only be called for accounts locked in this transaction.
    /// </summary>
    Task ApplyBalanceChangesAsync(IReadOnlyList<BalanceChange> changes, DateTimeOffset now, CancellationToken cancellationToken);

    Task InsertTransferAsync(TransferRecord transfer, CancellationToken cancellationToken);

    /// <summary>Loads a transfer and its original journal entry (immutable data, no lock needed).</summary>
    Task<StoredTransfer?> FindTransferAsync(TransferId transferId, CancellationToken cancellationToken);

    /// <summary>Call only after the wallets involved are locked, so the answer cannot go stale.</summary>
    Task<bool> IsTransferReversedAsync(TransferId transferId, CancellationToken cancellationToken);

    Task InsertTransferReversalAsync(TransferReversalRecord reversal, CancellationToken cancellationToken);

    /// <summary>Freezes wallets; locks their ledger accounts first so in-flight transfers finish or see the freeze.</summary>
    Task<IReadOnlyList<WalletId>> FreezeWalletsAsync(IReadOnlyCollection<WalletId> walletIds, CancellationToken cancellationToken);

    /// <summary>Writes an event to the transactional outbox; it commits or rolls back with the ledger change.</summary>
    Task EnqueueOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task AppendAuditEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Named point in the transaction where tests and the Tech Lead challenges can inject a failure.
    /// A no-op unless failure injection is explicitly enabled, which is refused outside Development/Testing.
    /// </summary>
    Task CheckpointAsync(FailurePoint point, CancellationToken cancellationToken);
}

/// <summary>Accounts and wallets read while their account rows are locked by the current transaction.</summary>
public sealed class LockedLedgerState(
    IReadOnlyDictionary<LedgerAccountId, LedgerAccount> accounts,
    IReadOnlyDictionary<WalletId, WalletSnapshot> wallets)
{
    public IReadOnlyDictionary<LedgerAccountId, LedgerAccount> Accounts { get; } = accounts;

    public IReadOnlyDictionary<WalletId, WalletSnapshot> Wallets { get; } = wallets;

    public WalletSnapshot? FindWallet(WalletId id) => Wallets.GetValueOrDefault(id);
}

public sealed record TransferRecord(
    TransferId Id,
    JournalEntryId JournalEntryId,
    WalletId FromWalletId,
    WalletId ToWalletId,
    long AmountMinor,
    string Currency,
    string Description,
    CustomerId RequestedBy,
    string IdempotencyKey,
    DateTimeOffset CreatedAt);

public sealed record StoredTransfer(TransferRecord Transfer, JournalEntry JournalEntry);

public sealed record TransferReversalRecord(
    Guid Id,
    TransferId TransferId,
    JournalEntryId JournalEntryId,
    string Reason,
    string RequestedBy,
    DateTimeOffset CreatedAt);

public sealed record OutboxMessage(Guid Id, string Type, string PayloadJson, string? CorrelationId, DateTimeOffset CreatedAt);

public enum AuditOutcome
{
    Succeeded,
    Rejected,
    Denied,
}

public sealed record AuditEvent(
    DateTimeOffset OccurredAt,
    string ActorId,
    string Action,
    AuditOutcome Outcome,
    string? ResourceType,
    string? ResourceId,
    string? CorrelationId,
    IReadOnlyDictionary<string, string?> Details);
