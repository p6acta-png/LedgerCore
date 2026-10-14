using System.Text.Json;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Security;

namespace LedgerCore.Application.Common;

/// <summary>Names of audited actions. Stable strings: they are queried by auditors.</summary>
public static class AuditActions
{
    public const string TransferCreate = "transfer.create";
    public const string TransferReverse = "transfer.reverse";
    public const string FundingCreate = "funding.create";
    public const string WalletCreate = "wallet.create";
    public const string WalletRead = "wallet.read";
    public const string WalletFreeze = "wallet.freeze";
    public const string ReconciliationRun = "reconciliation.run";
}

/// <summary>Outbox message types and payloads. Consumers must treat every message as possibly duplicated.</summary>
public static class OutboxMessageTypes
{
    public const string TransferCompleted = "transfer.completed";
    public const string TransferReversed = "transfer.reversed";
    public const string WalletFunded = "wallet.funded";

    public static OutboxMessage Create<TPayload>(string type, TPayload payload, string? correlationId, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), type, JsonSerializer.Serialize(payload, LedgerJson.Options), correlationId, now);
}

public sealed record TransferCompletedPayload(
    Guid TransferId,
    Guid JournalEntryId,
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountMinor,
    string Currency,
    DateTimeOffset OccurredAt);

public sealed record TransferReversedPayload(
    Guid TransferId,
    Guid ReversalJournalEntryId,
    Guid OriginalJournalEntryId,
    long AmountMinor,
    string Currency,
    string Reason,
    DateTimeOffset OccurredAt);

public sealed record WalletFundedPayload(
    Guid WalletId,
    Guid JournalEntryId,
    long AmountMinor,
    string Currency,
    DateTimeOffset OccurredAt);

internal static class Audit
{
    public static AuditEvent Event(
        CallerContext caller,
        string action,
        AuditOutcome outcome,
        string? resourceType,
        string? resourceId,
        DateTimeOffset now,
        params (string Key, string? Value)[] details) =>
        new(now, caller.Subject, action, outcome, resourceType, resourceId, caller.CorrelationId,
            details.ToDictionary(d => d.Key, d => d.Value));
}
