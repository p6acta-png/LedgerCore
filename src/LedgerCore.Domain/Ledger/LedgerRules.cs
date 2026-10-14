using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;
using LedgerCore.Domain.Wallets;

namespace LedgerCore.Domain.Ledger;

/// <summary>
/// Pure decision functions for the three ledger operations. They receive state that the caller has
/// already locked, decide, and return either a balanced <see cref="JournalEntry"/> or a
/// <see cref="LedgerError"/>. They perform no I/O, which keeps every money rule unit-testable.
/// </summary>
public static class LedgerRules
{
    /// <summary>
    /// Customer-to-customer transfer: debit the sender's wallet liability (we owe them less) and credit
    /// the receiver's wallet liability (we owe them more).
    /// </summary>
    public static Result<JournalEntry> PlanTransfer(
        JournalEntryId entryId,
        CustomerId requestedBy,
        WalletSnapshot from,
        WalletSnapshot to,
        Money amount,
        string description,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(amount);

        if (from.Id == to.Id)
        {
            return LedgerErrors.SelfTransfer;
        }

        // Authorization is checked before the transaction starts; this re-check against locked state is
        // defence in depth so the rule holds even if a future caller forgets the earlier check.
        if (from.OwnerId != requestedBy)
        {
            return LedgerErrors.SourceWalletNotOwned;
        }

        if (from.Status != WalletStatus.Active)
        {
            return LedgerErrors.WalletNotActive(from.Id);
        }

        if (to.Status != WalletStatus.Active)
        {
            return LedgerErrors.WalletNotActive(to.Id);
        }

        if (from.Currency != amount.Currency)
        {
            return LedgerErrors.CurrencyMismatch(from.Currency.Code, amount.Currency.Code);
        }

        if (to.Currency != amount.Currency)
        {
            return LedgerErrors.CurrencyMismatch(to.Currency.Code, amount.Currency.Code);
        }

        if (from.AvailableBalanceMinor < amount.MinorUnits)
        {
            return LedgerErrors.InsufficientFunds(from.AvailableBalanceMinor, amount.MinorUnits);
        }

        return JournalEntry.Create(
            entryId,
            EntryType.Transfer,
            amount.Currency,
            description,
            [
                new Posting(from.Account.Id, PostingDirection.Debit, amount.MinorUnits),
                new Posting(to.Account.Id, PostingDirection.Credit, amount.MinorUnits),
            ],
            now);
    }

    /// <summary>
    /// Simulated deposit: debit the funding expense account and credit the wallet liability.
    /// Both sides are recorded, so the journal stays balanced and reconciliation can explain every krone.
    /// </summary>
    public static Result<JournalEntry> PlanFunding(
        JournalEntryId entryId,
        WalletSnapshot wallet,
        LedgerAccount fundingAccount,
        Money amount,
        string description,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(fundingAccount);
        ArgumentNullException.ThrowIfNull(amount);

        if (wallet.Status != WalletStatus.Active)
        {
            return LedgerErrors.WalletNotActive(wallet.Id);
        }

        if (wallet.Currency != amount.Currency)
        {
            return LedgerErrors.CurrencyMismatch(wallet.Currency.Code, amount.Currency.Code);
        }

        if (fundingAccount.Currency != amount.Currency)
        {
            return LedgerErrors.CurrencyMismatch(fundingAccount.Currency.Code, amount.Currency.Code);
        }

        return JournalEntry.Create(
            entryId,
            EntryType.Funding,
            amount.Currency,
            description,
            [
                new Posting(fundingAccount.Id, PostingDirection.Debit, amount.MinorUnits),
                new Posting(wallet.Account.Id, PostingDirection.Credit, amount.MinorUnits),
            ],
            now);
    }

    /// <summary>
    /// Reversal of a completed transfer: a new compensating entry that mirrors the original. The
    /// original receiver must still hold the funds, because wallets can never go negative.
    /// </summary>
    public static Result<JournalEntry> PlanTransferReversal(
        JournalEntry original,
        JournalEntryId reversalId,
        WalletSnapshot originalSender,
        WalletSnapshot originalReceiver,
        long transferAmountMinor,
        string description,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(originalSender);
        ArgumentNullException.ThrowIfNull(originalReceiver);

        if (original.Type != EntryType.Transfer)
        {
            return LedgerErrors.CannotReverseReversal;
        }

        if (originalSender.Status != WalletStatus.Active)
        {
            return LedgerErrors.WalletNotActive(originalSender.Id);
        }

        if (originalReceiver.Status != WalletStatus.Active)
        {
            return LedgerErrors.WalletNotActive(originalReceiver.Id);
        }

        if (originalReceiver.AvailableBalanceMinor < transferAmountMinor)
        {
            return LedgerErrors.ReversalInsufficientFunds(
                originalReceiver.Id, originalReceiver.AvailableBalanceMinor, transferAmountMinor);
        }

        return original.CreateReversal(reversalId, description, now);
    }
}
