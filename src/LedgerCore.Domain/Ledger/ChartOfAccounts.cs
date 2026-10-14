using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;

namespace LedgerCore.Domain.Ledger;

/// <summary>
/// The explicit chart of accounts for the simulated financial system.
///
///   2100-WALLET-{walletId}   Liability, credit-normal. One per customer wallet: what the system owes
///                            that customer. Can never go negative (DB CHECK constraint + domain rule).
///   5900-SIM-FUNDING-NOK     Expense, debit-normal. Counter-account for simulated deposits. Funding a
///                            wallet debits this account and credits the wallet, so the entry balances;
///                            money is never "created" by editing a balance.
///
/// Accounting identity maintained by every entry:
///   SUM(debit-normal balances) == SUM(credit-normal balances)
/// i.e. total simulated funding expense == total of all wallet balances.
/// </summary>
public static class ChartOfAccounts
{
    public const string WalletAccountCodePrefix = "2100-WALLET-";

    public const string FundingExpenseNokCode = "5900-SIM-FUNDING-NOK";

    /// <summary>Fixed id so migrations, seed data and code agree on the system account.</summary>
    public static readonly LedgerAccountId FundingExpenseNokId = new(Guid.Parse("01920000-0000-7000-8000-000000005900"));

    public static string WalletAccountCode(WalletId walletId) => $"{WalletAccountCodePrefix}{walletId.Value:N}";

    public static LedgerAccountId FundingExpenseAccountFor(Currency currency) =>
        currency == Currency.Nok
            ? FundingExpenseNokId
            : throw new InvalidOperationException($"No funding account is configured for {currency}.");

    /// <summary>Shape of a brand-new wallet liability account.</summary>
    public static LedgerAccount NewWalletAccount(LedgerAccountId id, WalletId walletId, Currency currency) =>
        new(id, WalletAccountCode(walletId), AccountType.Liability, currency, AllowNegativeBalance: false, Balance: 0);
}
