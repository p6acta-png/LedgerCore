using LedgerCore.Domain.Common;

namespace LedgerCore.Application.Abstractions;

/// <summary>
/// Read-only queries. Reads take no locks and run outside ledger transactions; they may be a few
/// milliseconds behind a concurrent write, which is acceptable for display purposes. Decisions about
/// money are never made from these results, only from state locked inside a ledger transaction.
/// </summary>
public interface ILedgerReadStore
{
    Task<WalletView?> GetWalletAsync(WalletId walletId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WalletView>> ListWalletsAsync(CustomerId ownerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WalletTransactionView>> GetWalletTransactionsAsync(
        WalletId walletId, int limit, long? beforePostingId, CancellationToken cancellationToken);

    Task<TransferView?> GetTransferAsync(TransferId transferId, CancellationToken cancellationToken);

    Task<JournalEntryView?> GetJournalEntryAsync(JournalEntryId entryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalEntryView>> ListJournalEntriesAsync(int limit, Guid? beforeEntryId, CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes every account balance from the immutable postings and compares it with the cached
    /// projection, inside a single REPEATABLE READ snapshot so both sides describe the same moment.
    /// </summary>
    Task<ReconciliationSnapshot> ComputeReconciliationAsync(CancellationToken cancellationToken);
}

public sealed record WalletView(
    Guid Id,
    Guid CustomerId,
    string Name,
    string Currency,
    string Status,
    long BalanceMinor,
    Guid LedgerAccountId,
    string LedgerAccountCode,
    DateTimeOffset CreatedAt);

public sealed record WalletTransactionView(
    long PostingId,
    Guid JournalEntryId,
    string EntryType,
    string Direction,
    long AmountMinor,
    long SignedAmountMinor,
    string Currency,
    string Description,
    Guid? TransferId,
    DateTimeOffset CreatedAt);

public sealed record TransferView(
    Guid Id,
    Guid FromWalletId,
    Guid ToWalletId,
    Guid FromCustomerId,
    Guid ToCustomerId,
    long AmountMinor,
    string Currency,
    string Description,
    Guid JournalEntryId,
    Guid RequestedBy,
    DateTimeOffset CreatedAt,
    TransferReversalView? Reversal);

public sealed record TransferReversalView(Guid Id, Guid JournalEntryId, string Reason, string RequestedBy, DateTimeOffset CreatedAt);

public sealed record JournalEntryView(
    Guid Id,
    string EntryType,
    string Currency,
    string Description,
    Guid? ReversesJournalEntryId,
    string CreatedBy,
    string? CorrelationId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PostingView> Postings);

public sealed record PostingView(long Id, Guid LedgerAccountId, string LedgerAccountCode, string Direction, long AmountMinor);

/// <summary>Raw output of the reconciliation query.</summary>
public sealed record ReconciliationSnapshot(
    int AccountsChecked,
    long JournalEntriesChecked,
    long TotalDebitsMinor,
    long TotalCreditsMinor,
    IReadOnlyList<AccountDiscrepancy> Discrepancies,
    IReadOnlyList<Guid> UnbalancedJournalEntryIds,
    IReadOnlyList<Guid> NegativeWalletIds);

public sealed record AccountDiscrepancy(
    Guid LedgerAccountId,
    string AccountCode,
    Guid? WalletId,
    long CachedBalanceMinor,
    long JournalBalanceMinor);
