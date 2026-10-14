using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;

namespace LedgerCore.Domain.Ledger;

/// <summary>
/// Immutable snapshot of a ledger account, typically read while its database row is locked.
/// <see cref="Balance"/> is the cached projection expressed in the account's normal-balance direction
/// (for a wallet liability: credits minus debits = what the customer can spend).
/// </summary>
public sealed record LedgerAccount(
    LedgerAccountId Id,
    string Code,
    AccountType Type,
    Currency Currency,
    bool AllowNegativeBalance,
    long Balance)
{
    public PostingDirection NormalBalance => Type.NormalBalance();

    /// <summary>+amount when the posting is on the account's normal side, -amount otherwise.</summary>
    public long SignedEffect(PostingDirection direction, long amountMinor) =>
        direction == NormalBalance ? amountMinor : -amountMinor;

    /// <summary>
    /// Computes the balance after applying a net change, enforcing overflow and non-negativity rules.
    /// Does not mutate this snapshot.
    /// </summary>
    public Result<long> BalanceAfter(long netChangeMinor)
    {
        long newBalance;
        try
        {
            newBalance = checked(Balance + netChangeMinor);
        }
        catch (OverflowException)
        {
            return LedgerErrors.BalanceOverflow(Code);
        }

        if (!AllowNegativeBalance && newBalance < 0)
        {
            return LedgerErrors.NegativeBalanceNotAllowed(Code);
        }

        return newBalance;
    }
}
