using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Wallets;
using Xunit;
using static LedgerCore.UnitTests.TestData;

namespace LedgerCore.UnitTests.Domain;

public sealed class LedgerRulesTests
{
    [Fact]
    public void A_transfer_debits_the_sender_and_credits_the_receiver()
    {
        var owner = new CustomerId(Guid.NewGuid());
        var from = Wallet(100_000, owner);
        var to = Wallet(0);

        var entry = LedgerRules.PlanTransfer(JournalEntryId.New(), owner, from, to, Nok(80_000), "rent", Now);

        Assert.True(entry.IsSuccess);
        Assert.Equal(EntryType.Transfer, entry.Value.Type);
        Assert.Equal(2, entry.Value.Postings.Count);
        Assert.Contains(entry.Value.Postings, p => p.AccountId == from.Account.Id && p.Direction == PostingDirection.Debit && p.AmountMinor == 80_000);
        Assert.Contains(entry.Value.Postings, p => p.AccountId == to.Account.Id && p.Direction == PostingDirection.Credit && p.AmountMinor == 80_000);
    }

    [Fact]
    public void Spending_the_exact_balance_is_allowed_but_one_more_unit_is_not()
    {
        var owner = new CustomerId(Guid.NewGuid());
        var from = Wallet(80_000, owner);

        Assert.True(LedgerRules.PlanTransfer(JournalEntryId.New(), owner, from, Wallet(0), Nok(80_000), "", Now).IsSuccess);

        var tooMuch = LedgerRules.PlanTransfer(JournalEntryId.New(), owner, from, Wallet(0), Nok(80_001), "", Now);
        Assert.Equal("INSUFFICIENT_FUNDS", tooMuch.Error.Code);
    }

    [Fact]
    public void Self_transfers_are_rejected()
    {
        var owner = new CustomerId(Guid.NewGuid());
        var wallet = Wallet(1_000, owner);

        Assert.Equal("SELF_TRANSFER", LedgerRules.PlanTransfer(JournalEntryId.New(), owner, wallet, wallet, Nok(1), "", Now).Error.Code);
    }

    [Fact]
    public void A_customer_cannot_debit_a_wallet_they_do_not_own()
    {
        var from = Wallet(1_000);

        var entry = LedgerRules.PlanTransfer(JournalEntryId.New(), new CustomerId(Guid.NewGuid()), from, Wallet(0), Nok(1), "", Now);

        Assert.Equal(ErrorKind.Forbidden, entry.Error.Kind);
    }

    [Fact]
    public void Frozen_wallets_can_neither_send_nor_receive()
    {
        var owner = new CustomerId(Guid.NewGuid());

        var frozenSender = LedgerRules.PlanTransfer(
            JournalEntryId.New(), owner, Wallet(1_000, owner, WalletStatus.Frozen), Wallet(0), Nok(1), "", Now);
        var frozenReceiver = LedgerRules.PlanTransfer(
            JournalEntryId.New(), owner, Wallet(1_000, owner), Wallet(0, status: WalletStatus.Frozen), Nok(1), "", Now);

        Assert.Equal("WALLET_NOT_ACTIVE", frozenSender.Error.Code);
        Assert.Equal("WALLET_NOT_ACTIVE", frozenReceiver.Error.Code);
    }

    [Fact]
    public void Funding_is_a_balanced_entry_against_the_funding_expense_account()
    {
        var wallet = Wallet(0);
        var funding = FundingAccount();

        var entry = LedgerRules.PlanFunding(JournalEntryId.New(), wallet, funding, Nok(5_000), "demo", Now);

        Assert.True(entry.IsSuccess);
        Assert.Equal(EntryType.Funding, entry.Value.Type);
        Assert.Contains(entry.Value.Postings, p => p.AccountId == funding.Id && p.Direction == PostingDirection.Debit);
        Assert.Contains(entry.Value.Postings, p => p.AccountId == wallet.Account.Id && p.Direction == PostingDirection.Credit);
        Assert.Equal(entry.Value.TotalDebits, entry.Value.TotalCredits);
    }

    [Fact]
    public void A_reversal_requires_the_receiver_to_still_hold_the_funds()
    {
        var owner = new CustomerId(Guid.NewGuid());
        var sender = Wallet(1_000, owner);
        var receiver = Wallet(0);
        var original = LedgerRules.PlanTransfer(JournalEntryId.New(), owner, sender, receiver, Nok(600), "", Now).Value;

        // The receiver has since spent part of the money: only 599 left.
        var receiverNow = receiver with { Account = receiver.Account with { Balance = 599 } };
        var reversal = LedgerRules.PlanTransferReversal(original, JournalEntryId.New(), sender, receiverNow, 600, "dispute", Now);

        Assert.Equal("REVERSAL_INSUFFICIENT_FUNDS", reversal.Error.Code);
    }

    [Fact]
    public void A_reversal_of_a_transfer_restores_both_balances()
    {
        var owner = new CustomerId(Guid.NewGuid());
        var sender = Wallet(1_000, owner);
        var receiver = Wallet(0);
        var original = LedgerRules.PlanTransfer(JournalEntryId.New(), owner, sender, receiver, Nok(600), "", Now).Value;
        var afterTransfer = Accounts(sender.Account with { Balance = 400 }, receiver.Account with { Balance = 600 });

        var reversal = LedgerRules.PlanTransferReversal(
            original, JournalEntryId.New(), sender with { Account = afterTransfer[sender.Account.Id] },
            receiver with { Account = afterTransfer[receiver.Account.Id] }, 600, "dispute", Now).Value;
        var changes = reversal.CalculateBalanceChanges(afterTransfer).Value;

        Assert.Contains(changes, c => c.AccountId == sender.Account.Id && c.NewBalanceMinor == 1_000);
        Assert.Contains(changes, c => c.AccountId == receiver.Account.Id && c.NewBalanceMinor == 0);
    }
}
