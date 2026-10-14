using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;

namespace LedgerCore.Domain.Ledger;

public enum EntryType
{
    Transfer,
    Funding,
    Reversal,
}

/// <summary>One line of a journal entry: a debit or credit of a positive amount to one account.</summary>
public sealed record Posting(LedgerAccountId AccountId, PostingDirection Direction, long AmountMinor);

/// <summary>Net effect of a journal entry on one account, plus the resulting cached balance.</summary>
public sealed record BalanceChange(LedgerAccountId AccountId, long DeltaMinor, long NewBalanceMinor);

/// <summary>
/// An immutable, balanced set of postings. The only way to construct one is <see cref="Create"/>,
/// which guarantees: at least two postings, every amount strictly positive, a single currency,
/// and SUM(debits) == SUM(credits). The database re-checks the balance rule at commit time
/// (see the ledger integrity migration), so a bug here cannot persist an unbalanced entry.
/// </summary>
public sealed class JournalEntry
{
    public const int MaxDescriptionLength = 200;

    private JournalEntry(
        JournalEntryId id,
        EntryType type,
        Currency currency,
        string description,
        IReadOnlyList<Posting> postings,
        DateTimeOffset createdAt,
        JournalEntryId? reversesEntryId)
    {
        Id = id;
        Type = type;
        Currency = currency;
        Description = description;
        Postings = postings;
        CreatedAt = createdAt;
        ReversesEntryId = reversesEntryId;
    }

    public JournalEntryId Id { get; }

    public EntryType Type { get; }

    public Currency Currency { get; }

    public string Description { get; }

    public IReadOnlyList<Posting> Postings { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Set only for <see cref="EntryType.Reversal"/> entries.</summary>
    public JournalEntryId? ReversesEntryId { get; }

    public long TotalDebits => Postings.Where(p => p.Direction == PostingDirection.Debit).Sum(p => p.AmountMinor);

    public long TotalCredits => Postings.Where(p => p.Direction == PostingDirection.Credit).Sum(p => p.AmountMinor);

    public static Result<JournalEntry> Create(
        JournalEntryId id,
        EntryType type,
        Currency currency,
        string description,
        IReadOnlyList<Posting> postings,
        DateTimeOffset createdAt,
        JournalEntryId? reversesEntryId = null)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(postings);
        if (createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Journal timestamps must be UTC.", nameof(createdAt));
        }

        var descriptionCheck = ValidateDescription(description);
        if (descriptionCheck.IsFailure)
        {
            return descriptionCheck.Error;
        }

        if (postings.Count < 2)
        {
            return LedgerErrors.UnbalancedEntry("an entry needs at least two postings.");
        }

        if ((type == EntryType.Reversal) != reversesEntryId.HasValue)
        {
            return LedgerErrors.UnbalancedEntry("only reversal entries may reference a reversed entry, and they must.");
        }

        long debits = 0;
        long credits = 0;
        foreach (var posting in postings)
        {
            if (posting.AmountMinor <= 0)
            {
                return LedgerErrors.UnbalancedEntry("posting amounts must be strictly positive.");
            }

            try
            {
                if (posting.Direction == PostingDirection.Debit)
                {
                    debits = checked(debits + posting.AmountMinor);
                }
                else
                {
                    credits = checked(credits + posting.AmountMinor);
                }
            }
            catch (OverflowException)
            {
                return LedgerErrors.UnbalancedEntry("posting totals overflow a 64-bit integer.");
            }
        }

        if (debits != credits)
        {
            return LedgerErrors.UnbalancedEntry($"debits ({debits}) do not equal credits ({credits}).");
        }

        return new JournalEntry(id, type, currency, descriptionCheck.Value, postings.ToArray(), createdAt, reversesEntryId);
    }

    /// <summary>
    /// Rehydrates an entry read from the database. It is re-validated, so a corrupt row surfaces as an
    /// exception instead of silently flowing into a reversal.
    /// </summary>
    public static JournalEntry FromStorage(
        JournalEntryId id,
        EntryType type,
        Currency currency,
        string description,
        IReadOnlyList<Posting> postings,
        DateTimeOffset createdAt,
        JournalEntryId? reversesEntryId)
    {
        var result = Create(id, type, currency, description, postings, createdAt.ToUniversalTime(), reversesEntryId);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException($"Stored journal entry {id} violates ledger invariants: {result.Error}");
    }

    /// <summary>
    /// Builds the compensating entry: identical postings with debit and credit swapped. The original
    /// entry is never modified or deleted; history stays append-only.
    /// </summary>
    public Result<JournalEntry> CreateReversal(JournalEntryId reversalId, string description, DateTimeOffset createdAt)
    {
        if (Type == EntryType.Reversal)
        {
            return LedgerErrors.CannotReverseReversal;
        }

        var swapped = Postings
            .Select(p => p with { Direction = p.Direction.Opposite() })
            .ToArray();
        return Create(reversalId, EntryType.Reversal, Currency, description, swapped, createdAt, Id);
    }

    /// <summary>
    /// Computes the net change per account and the resulting balances, validating overflow and
    /// non-negativity for every touched account. All touched accounts must be supplied (normally the
    /// snapshots read under row locks).
    /// </summary>
    public Result<IReadOnlyList<BalanceChange>> CalculateBalanceChanges(IReadOnlyDictionary<LedgerAccountId, LedgerAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var changes = new List<BalanceChange>();
        foreach (var group in Postings.GroupBy(p => p.AccountId))
        {
            if (!accounts.TryGetValue(group.Key, out var account))
            {
                throw new InvalidOperationException($"Account {group.Key} was not loaded before applying entry {Id}.");
            }

            if (account.Currency != Currency)
            {
                return LedgerErrors.CurrencyMismatch(account.Currency.Code, Currency.Code);
            }

            long delta = 0;
            try
            {
                foreach (var posting in group)
                {
                    delta = checked(delta + account.SignedEffect(posting.Direction, posting.AmountMinor));
                }
            }
            catch (OverflowException)
            {
                return LedgerErrors.BalanceOverflow(account.Code);
            }

            var newBalance = account.BalanceAfter(delta);
            if (newBalance.IsFailure)
            {
                return newBalance.Error;
            }

            changes.Add(new BalanceChange(account.Id, delta, newBalance.Value));
        }

        return changes;
    }

    public static Result<string> ValidateDescription(string? description)
    {
        var trimmed = (description ?? string.Empty).Trim();
        if (trimmed.Length > MaxDescriptionLength)
        {
            return LedgerErrors.InvalidDescription($"at most {MaxDescriptionLength} characters are allowed.");
        }

        if (trimmed.Any(char.IsControl))
        {
            return LedgerErrors.InvalidDescription("control characters are not allowed.");
        }

        return trimmed;
    }
}
