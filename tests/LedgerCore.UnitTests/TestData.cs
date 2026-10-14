using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Monetary;
using LedgerCore.Domain.Wallets;

namespace LedgerCore.UnitTests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public static Money Nok(long minor) => Money.CreatePositive(minor, Currency.Nok).Value;

    public static LedgerAccount WalletAccount(long balance) =>
        new(LedgerAccountId.New(), "2100-WALLET-test", AccountType.Liability, Currency.Nok, AllowNegativeBalance: false, balance);

    public static LedgerAccount FundingAccount(long balance = 0) =>
        new(ChartOfAccounts.FundingExpenseNokId, ChartOfAccounts.FundingExpenseNokCode, AccountType.Expense, Currency.Nok, AllowNegativeBalance: false, balance);

    public static WalletSnapshot Wallet(long balance, CustomerId? owner = null, WalletStatus status = WalletStatus.Active) =>
        new(WalletId.New(), owner ?? new CustomerId(Guid.NewGuid()), status, WalletAccount(balance));

    public static Dictionary<LedgerAccountId, LedgerAccount> Accounts(params LedgerAccount[] accounts) =>
        accounts.ToDictionary(a => a.Id);
}
