using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Monetary;
using Xunit;
using static LedgerCore.UnitTests.TestData;

namespace LedgerCore.UnitTests.Domain;

public sealed class JournalEntryTests
{
    private static Result<JournalEntry> Create(EntryType type, params Posting[] postings) =>
        JournalEntry.Create(JournalEntryId.New(), type, Currency.Nok, "test", postings, Now);

    [Fact]
    public void A_balanced_two_posting_entry_is_created()
    {
        var a = LedgerAccountId.New();
        var b = LedgerAccountId.New();

        var entry = Create(EntryType.Transfer, new Posting(a, PostingDirection.Debit, 500), new Posting(b, PostingDirection.Credit, 500));

        Assert.True(entry.IsSuccess);
        Assert.Equal(500, entry.Value.TotalDebits);
        Assert.Equal(500, entry.Value.TotalCredits);
    }

    [Fact]
    public void An_unbalanced_entry_is_rejected()
    {
        var entry = Create(EntryType.Transfer,
            new Posting(LedgerAccountId.New(), PostingDirection.Debit, 500),
            new Posting(LedgerAccountId.New(), PostingDirection.Credit, 499));

        Assert.True(entry.IsFailure);
        Assert.Equal("UNBALANCED_JOURNAL_ENTRY", entry.Error.Code);
    }

    [Fact]
    public void A_single_posting_entry_is_rejected_even_if_trivially_balanced()
    {
        var entry = Create(EntryType.Transfer, new Posting(LedgerAccountId.New(), PostingDirection.Debit, 500));

        Assert.Equal("UNBALANCED_JOURNAL_ENTRY", entry.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Non_positive_posting_amounts_are_rejected(long amount)
    {
        var entry = Create(EntryType.Transfer,
            new Posting(LedgerAccountId.New(), PostingDirection.Debit, amount),
            new Posting(LedgerAccountId.New(), PostingDirection.Credit, amount));

        Assert.Equal("UNBALANCED_JOURNAL_ENTRY", entry.Error.Code);
    }

    [Fact]
    public void Overflowing_totals_are_rejected_instead_of_wrapping_around()
    {
        var a = LedgerAccountId.New();
        var entry = Create(EntryType.Transfer,
            new Posting(a, PostingDirection.Debit, long.MaxValue),
            new Posting(a, PostingDirection.Debit, 1),
            new Posting(LedgerAccountId.New(), PostingDirection.Credit, long.MaxValue));

        Assert.Equal("UNBALANCED_JOURNAL_ENTRY", entry.Error.Code);
        Assert.Contains("overflow", entry.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_reversals_may_reference_a_reversed_entry()
    {
        var postings = new[]
        {
            new Posting(LedgerAccountId.New(), PostingDirection.Debit, 1),
            new Posting(LedgerAccountId.New(), PostingDirection.Credit, 1),
        };

        var transferWithLink = JournalEntry.Create(JournalEntryId.New(), EntryType.Transfer, Currency.Nok, "x", postings, Now, JournalEntryId.New());
        var reversalWithoutLink = JournalEntry.Create(JournalEntryId.New(), EntryType.Reversal, Currency.Nok, "x", postings, Now);

        Assert.True(transferWithLink.IsFailure);
        Assert.True(reversalWithoutLink.IsFailure);
    }

    [Fact]
    public void Timestamps_must_be_utc()
    {
        var postings = new[]
        {
            new Posting(LedgerAccountId.New(), PostingDirection.Debit, 1),
            new Posting(LedgerAccountId.New(), PostingDirection.Credit, 1),
        };

        Assert.Throws<ArgumentException>(() =>
            JournalEntry.Create(JournalEntryId.New(), EntryType.Transfer, Currency.Nok, "x", postings, Now.ToOffset(TimeSpan.FromHours(2))));
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    public void Descriptions_with_control_characters_are_rejected(string description) =>
        Assert.Equal("VALIDATION_FAILED", JournalEntry.ValidateDescription(description).Error.Code);

    [Fact]
    public void Descriptions_are_trimmed_and_limited_to_200_characters()
    {
        Assert.Equal("rent", JournalEntry.ValidateDescription("  rent  ").Value);
        Assert.True(JournalEntry.ValidateDescription(new string('x', 200)).IsSuccess);
        Assert.True(JournalEntry.ValidateDescription(new string('x', 201)).IsFailure);
        Assert.Equal(string.Empty, JournalEntry.ValidateDescription(null).Value);
    }

    [Fact]
    public void A_reversal_mirrors_every_posting_and_points_at_the_original()
    {
        var a = LedgerAccountId.New();
        var b = LedgerAccountId.New();
        var original = Create(EntryType.Transfer, new Posting(a, PostingDirection.Debit, 700), new Posting(b, PostingDirection.Credit, 700)).Value;

        var reversal = original.CreateReversal(JournalEntryId.New(), "customer complaint", Now);

        Assert.True(reversal.IsSuccess);
        Assert.Equal(EntryType.Reversal, reversal.Value.Type);
        Assert.Equal(original.Id, reversal.Value.ReversesEntryId);
        Assert.Contains(reversal.Value.Postings, p => p.AccountId == a && p.Direction == PostingDirection.Credit && p.AmountMinor == 700);
        Assert.Contains(reversal.Value.Postings, p => p.AccountId == b && p.Direction == PostingDirection.Debit && p.AmountMinor == 700);

        // The original is untouched: history is append-only.
        Assert.Contains(original.Postings, p => p.AccountId == a && p.Direction == PostingDirection.Debit);
    }

    [Fact]
    public void A_reversal_cannot_itself_be_reversed()
    {
        var original = Create(EntryType.Transfer,
            new Posting(LedgerAccountId.New(), PostingDirection.Debit, 1),
            new Posting(LedgerAccountId.New(), PostingDirection.Credit, 1)).Value;
        var reversal = original.CreateReversal(JournalEntryId.New(), "r", Now).Value;

        Assert.Equal("REVERSAL_NOT_ALLOWED", reversal.CreateReversal(JournalEntryId.New(), "r2", Now).Error.Code);
    }

    [Fact]
    public void Crediting_a_liability_increases_it_and_debiting_decreases_it()
    {
        var sender = WalletAccount(1_000);
        var receiver = WalletAccount(0);
        var entry = Create(EntryType.Transfer,
            new Posting(sender.Id, PostingDirection.Debit, 300),
            new Posting(receiver.Id, PostingDirection.Credit, 300)).Value;

        var changes = entry.CalculateBalanceChanges(Accounts(sender, receiver));

        Assert.True(changes.IsSuccess);
        Assert.Contains(changes.Value, c => c.AccountId == sender.Id && c.DeltaMinor == -300 && c.NewBalanceMinor == 700);
        Assert.Contains(changes.Value, c => c.AccountId == receiver.Id && c.DeltaMinor == 300 && c.NewBalanceMinor == 300);
    }

    [Fact]
    public void Debiting_an_expense_increases_it()
    {
        var funding = FundingAccount(0);
        var wallet = WalletAccount(0);
        var entry = Create(EntryType.Funding,
            new Posting(funding.Id, PostingDirection.Debit, 100),
            new Posting(wallet.Id, PostingDirection.Credit, 100)).Value;

        var changes = entry.CalculateBalanceChanges(Accounts(funding, wallet)).Value;

        Assert.Contains(changes, c => c.AccountId == funding.Id && c.NewBalanceMinor == 100);
        Assert.Contains(changes, c => c.AccountId == wallet.Id && c.NewBalanceMinor == 100);
    }

    [Fact]
    public void A_wallet_liability_can_never_be_driven_negative()
    {
        var sender = WalletAccount(299);
        var receiver = WalletAccount(0);
        var entry = Create(EntryType.Transfer,
            new Posting(sender.Id, PostingDirection.Debit, 300),
            new Posting(receiver.Id, PostingDirection.Credit, 300)).Value;

        Assert.Equal("INSUFFICIENT_FUNDS", entry.CalculateBalanceChanges(Accounts(sender, receiver)).Error.Code);
    }

    [Fact]
    public void Balance_overflow_is_detected()
    {
        var sender = WalletAccount(1_000);
        var receiver = WalletAccount(long.MaxValue - 10);
        var entry = Create(EntryType.Transfer,
            new Posting(sender.Id, PostingDirection.Debit, 100),
            new Posting(receiver.Id, PostingDirection.Credit, 100)).Value;

        Assert.Equal("BALANCE_OVERFLOW", entry.CalculateBalanceChanges(Accounts(sender, receiver)).Error.Code);
    }

    [Fact]
    public void Multiple_postings_to_one_account_are_netted()
    {
        var account = WalletAccount(50);
        var other = WalletAccount(0);
        var entry = Create(EntryType.Transfer,
            new Posting(account.Id, PostingDirection.Debit, 100),
            new Posting(account.Id, PostingDirection.Credit, 80),
            new Posting(other.Id, PostingDirection.Credit, 20)).Value;

        var changes = entry.CalculateBalanceChanges(Accounts(account, other)).Value;

        Assert.Contains(changes, c => c.AccountId == account.Id && c.DeltaMinor == -20 && c.NewBalanceMinor == 30);
    }
}
