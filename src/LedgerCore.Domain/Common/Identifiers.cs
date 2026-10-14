namespace LedgerCore.Domain.Common;

// Strongly typed identifiers. Each wraps a Guid but is a distinct type, so the compiler rejects
// accidentally passing a CustomerId where a WalletId is expected. New ids are UUIDv7 (time-ordered),
// which keeps primary-key indexes append-friendly in PostgreSQL.

public readonly record struct CustomerId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct WalletId(Guid Value)
{
    public static WalletId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct LedgerAccountId(Guid Value)
{
    public static LedgerAccountId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct JournalEntryId(Guid Value)
{
    public static JournalEntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct TransferId(Guid Value)
{
    public static TransferId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
