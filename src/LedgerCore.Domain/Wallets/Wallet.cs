using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Monetary;

namespace LedgerCore.Domain.Wallets;

public enum WalletStatus
{
    /// <summary>Normal state: can be debited and credited.</summary>
    Active,

    /// <summary>
    /// Blocked for all postings. Set by reconciliation when the cached balance disagrees with the
    /// journal, because spending against a balance that may be wrong could create money.
    /// </summary>
    Frozen,
}

/// <summary>
/// A customer-facing wallet together with its backing ledger account, as observed while the account
/// row is locked. The wallet itself holds no balance; the balance lives on the ledger account.
/// </summary>
public sealed record WalletSnapshot(WalletId Id, CustomerId OwnerId, WalletStatus Status, LedgerAccount Account)
{
    public Currency Currency => Account.Currency;

    public long AvailableBalanceMinor => Account.Balance;
}
