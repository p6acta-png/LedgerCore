using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Monetary;
using LedgerCore.Domain.Wallets;
using Xunit;
using static LedgerCore.UnitTests.TestData;

namespace LedgerCore.UnitTests.Domain;

/// <summary>
/// Property-style test: thousands of random operations (including deliberate overdraft attempts and
/// reversals) are pushed through the real domain rules, applying accepted entries to an in-memory ledger.
/// After every step the accounting identities must hold. Seeds are fixed, so a failure is reproducible.
/// </summary>
public sealed class RandomizedLedgerInvariantTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20261014)]
    public void Random_operations_never_break_the_ledger(int seed)
    {
        var random = new Random(seed);
        var owners = Enumerable.Range(0, 6).Select(_ => new CustomerId(Guid.NewGuid())).ToArray();
        var wallets = owners.Select(o => Wallet(0, o)).ToArray();
        var accounts = wallets.Select(w => w.Account).Append(FundingAccount()).ToDictionary(a => a.Id);
        var transfers = new List<JournalEntry>();
        var reversed = new HashSet<JournalEntryId>();
        long accepted = 0;
        long rejected = 0;

        for (var step = 0; step < 3_000; step++)
        {
            Result<JournalEntry> planned;
            var roll = random.Next(100);
            if (roll < 15)
            {
                var wallet = Current(wallets[random.Next(wallets.Length)], accounts);
                planned = LedgerRules.PlanFunding(JournalEntryId.New(), wallet, accounts[ChartOfAccounts.FundingExpenseNokId], Nok(random.Next(1, 50_000)), "fund", Now);
            }
            else if (roll < 25 && transfers.Count > 0)
            {
                var original = transfers[random.Next(transfers.Count)];
                if (!reversed.Add(original.Id))
                {
                    continue;
                }

                var debited = original.Postings.Single(p => p.Direction == PostingDirection.Debit).AccountId;
                var credited = original.Postings.Single(p => p.Direction == PostingDirection.Credit).AccountId;
                var sender = Current(wallets.Single(w => w.Account.Id == debited), accounts);
                var receiver = Current(wallets.Single(w => w.Account.Id == credited), accounts);
                planned = LedgerRules.PlanTransferReversal(original, JournalEntryId.New(), sender, receiver, original.TotalDebits, "rev", Now);
                if (planned.IsFailure)
                {
                    reversed.Remove(original.Id);
                }
            }
            else
            {
                var from = Current(wallets[random.Next(wallets.Length)], accounts);
                var to = Current(wallets[random.Next(wallets.Length)], accounts);

                // Often ask for more than the balance to exercise the rejection path.
                var amount = Math.Max(1, (long)(from.AvailableBalanceMinor * random.NextDouble() * 1.5) + random.Next(0, 100));
                planned = LedgerRules.PlanTransfer(JournalEntryId.New(), from.OwnerId, from, to, Nok(Math.Min(amount, Money.MaxOperationMinorUnits)), "t", Now);
            }

            if (planned.IsFailure)
            {
                rejected++;
                AssertInvariants(accounts);
                continue;
            }

            var changes = planned.Value.CalculateBalanceChanges(accounts);
            if (changes.IsFailure)
            {
                rejected++;
                AssertInvariants(accounts);
                continue;
            }

            foreach (var change in changes.Value)
            {
                accounts[change.AccountId] = accounts[change.AccountId] with { Balance = change.NewBalanceMinor };
            }

            if (planned.Value.Type == EntryType.Transfer)
            {
                transfers.Add(planned.Value);
            }

            accepted++;
            AssertInvariants(accounts);
        }

        Assert.True(accepted > 500, $"too few accepted operations ({accepted}) to be meaningful");
        Assert.True(rejected > 100, $"too few rejected operations ({rejected}) to exercise the guards");
    }

    private static WalletSnapshot Current(WalletSnapshot wallet, Dictionary<LedgerAccountId, LedgerAccount> accounts) =>
        wallet with { Account = accounts[wallet.Account.Id] };

    private static void AssertInvariants(Dictionary<LedgerAccountId, LedgerAccount> accounts)
    {
        // Debit-normal balances (funding expense) equal credit-normal balances (all wallets).
        var debitNormal = accounts.Values.Where(a => a.NormalBalance == PostingDirection.Debit).Sum(a => a.Balance);
        var creditNormal = accounts.Values.Where(a => a.NormalBalance == PostingDirection.Credit).Sum(a => a.Balance);
        Assert.Equal(debitNormal, creditNormal);
        Assert.All(accounts.Values.Where(a => !a.AllowNegativeBalance), a => Assert.True(a.Balance >= 0));
    }
}
