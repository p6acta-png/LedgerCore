using LedgerCore.Domain.Common;

namespace LedgerCore.Application.Security;

public static class Roles
{
    /// <summary>An end customer; may own wallets and move money out of them.</summary>
    public const string Customer = "customer";

    /// <summary>Back-office role: reconciliation, reversals, journal inspection, simulated funding.</summary>
    public const string LedgerOperator = "ledger-operator";
}

/// <summary>
/// Who is calling and for which request. Built by the API from the validated access token; the
/// application layer never reads HTTP headers or claims directly.
/// </summary>
public sealed record CallerContext(
    string Subject,
    CustomerId? CustomerId,
    IReadOnlySet<string> Roles,
    string? CorrelationId)
{
    public bool IsOperator => Roles.Contains(Security.Roles.LedgerOperator);

    public bool IsCustomer => CustomerId is not null && Roles.Contains(Security.Roles.Customer);

    /// <summary>Identity used by background jobs (for example scheduled reconciliation).</summary>
    public static CallerContext System(string? correlationId = null) =>
        new("system:ledgercore-worker", null, new HashSet<string> { Security.Roles.LedgerOperator }, correlationId);
}

/// <summary>
/// Authorization rules for wallets, kept as pure functions so they can be unit-tested exhaustively.
/// </summary>
public static class WalletAccessPolicy
{
    /// <summary>Owners and operators may read a wallet's details, balance and history.</summary>
    public static bool CanView(CallerContext caller, CustomerId owner) =>
        caller.IsOperator || (caller.IsCustomer && caller.CustomerId == owner);

    /// <summary>
    /// Only the owning customer may move money out of a wallet. Operators deliberately cannot
    /// (segregation of duties); they correct mistakes through audited reversals instead.
    /// </summary>
    public static bool CanDebit(CallerContext caller, CustomerId owner) =>
        caller.IsCustomer && caller.CustomerId == owner;
}
