using LedgerCore.Application.Abstractions;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Wallets;

namespace LedgerCore.Infrastructure.Persistence;

// Persistence shapes ("rows") mapped by EF Core. They mirror the tables one-to-one and carry no
// business rules; the rules live in LedgerCore.Domain. Keeping them separate lets the domain use
// strongly typed ids and immutable types without bending them around ORM requirements.

public sealed class CustomerRow
{
    public Guid Id { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LedgerAccountRow
{
    public Guid Id { get; set; }

    public required string Code { get; set; }

    public required string Name { get; set; }

    public AccountType AccountType { get; set; }

    public PostingDirection NormalBalance { get; set; }

    public required string Currency { get; set; }

    public bool AllowNegativeBalance { get; set; }

    /// <summary>Cached projection of SUM(postings) in normal-balance terms. Never the source of truth.</summary>
    public long BalanceMinor { get; set; }

    public long Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WalletRow
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public Guid LedgerAccountId { get; set; }

    public required string Currency { get; set; }

    public required string Name { get; set; }

    public WalletStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public CustomerRow? Customer { get; set; }

    public LedgerAccountRow? LedgerAccount { get; set; }
}

public sealed class JournalEntryRow
{
    public Guid Id { get; set; }

    public EntryType EntryType { get; set; }

    public required string Currency { get; set; }

    public required string Description { get; set; }

    public Guid? ReversesJournalEntryId { get; set; }

    public required string CreatedBy { get; set; }

    public string? CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<PostingRow> Postings { get; set; } = [];
}

public sealed class PostingRow
{
    public long Id { get; set; }

    public Guid JournalEntryId { get; set; }

    public Guid LedgerAccountId { get; set; }

    public PostingDirection Direction { get; set; }

    public long AmountMinor { get; set; }

    public required string Currency { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public LedgerAccountRow? LedgerAccount { get; set; }
}

public sealed class TransferRow
{
    public Guid Id { get; set; }

    public Guid JournalEntryId { get; set; }

    public Guid FromWalletId { get; set; }

    public Guid ToWalletId { get; set; }

    public long AmountMinor { get; set; }

    public required string Currency { get; set; }

    public required string Description { get; set; }

    public Guid RequestedBy { get; set; }

    public required string IdempotencyKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TransferReversalRow
{
    public Guid Id { get; set; }

    public Guid TransferId { get; set; }

    public Guid JournalEntryId { get; set; }

    public required string Reason { get; set; }

    public required string RequestedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IdempotencyRecordRow
{
    public required string PrincipalId { get; set; }

    public required string Operation { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string RequestHash { get; set; }

    /// <summary>Null only inside the claiming transaction; a deferred trigger forbids committing it null.</summary>
    public bool? Succeeded { get; set; }

    public string? ResponseBody { get; set; }

    public Guid? ResourceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AuditEventRow
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string ActorId { get; set; }

    public required string Action { get; set; }

    public AuditOutcome Outcome { get; set; }

    public string? ResourceType { get; set; }

    public string? ResourceId { get; set; }

    public string? CorrelationId { get; set; }

    public required string Details { get; set; }
}

public sealed class OutboxMessageRow
{
    public Guid Id { get; set; }

    public required string MessageType { get; set; }

    public required string Payload { get; set; }

    public string? CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset AvailableAt { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LockedBy { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? DeadLetteredAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// Inbox of the simulated external notification service. Keyed by outbox message id, so redelivered
/// messages are deduplicated (idempotent consumer) while delivery_count records the duplicates.
/// </summary>
public sealed class SimulatedNotificationRow
{
    public Guid MessageId { get; set; }

    public required string MessageType { get; set; }

    public required string Payload { get; set; }

    public int DeliveryCount { get; set; }

    public DateTimeOffset FirstDeliveredAt { get; set; }

    public DateTimeOffset LastDeliveredAt { get; set; }
}
