namespace LedgerCore.Domain.Ledger;

/// <summary>The five classic account classes of double-entry bookkeeping.</summary>
public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

/// <summary>Which side of a posting increases an account.</summary>
public enum PostingDirection
{
    Debit,
    Credit,
}

public static class AccountTypeExtensions
{
    /// <summary>
    /// Debit-normal accounts (assets, expenses) grow with debits; credit-normal accounts
    /// (liabilities, equity, revenue) grow with credits. A customer wallet is a liability:
    /// crediting it increases what the system owes the customer.
    /// </summary>
    public static PostingDirection NormalBalance(this AccountType type) => type switch
    {
        AccountType.Asset or AccountType.Expense => PostingDirection.Debit,
        AccountType.Liability or AccountType.Equity or AccountType.Revenue => PostingDirection.Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown account type."),
    };

    public static PostingDirection Opposite(this PostingDirection direction) => direction switch
    {
        PostingDirection.Debit => PostingDirection.Credit,
        PostingDirection.Credit => PostingDirection.Debit,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown posting direction."),
    };
}
