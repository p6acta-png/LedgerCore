using System.Globalization;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Security;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using Microsoft.Extensions.Logging;

namespace LedgerCore.Application.Reversals;

public sealed record ReverseTransferCommand(Guid TransferId, string? Reason);

public sealed record ReversalResponse(
    Guid TransferId,
    Guid ReversalId,
    Guid ReversalJournalEntryId,
    Guid OriginalJournalEntryId,
    long AmountMinor,
    string Currency,
    string Reason,
    DateTimeOffset CreatedAt);

/// <summary>
/// Reverses a completed transfer by appending a compensating journal entry. Historical postings are
/// never updated or deleted (the database rejects UPDATE/DELETE on them). Each transfer can be reversed
/// at most once, enforced by a UNIQUE constraint on transfer_reversals.transfer_id.
/// </summary>
public sealed class ReversalService(
    IdempotentCommandExecutor executor,
    ILedgerReadStore readStore,
    TimeProvider clock,
    ILogger<ReversalService> logger)
{
    public const string Operation = "transfers.reverse";

    public Task<CommandResult<ReversalResponse>> ReverseAsync(
        ReverseTransferCommand command, CallerContext caller, IdempotencyKey idempotencyKey, CancellationToken cancellationToken) =>
        CommandInstrumentation.MeasureAsync(Operation, () => ReverseCoreAsync(command, caller, idempotencyKey, cancellationToken));

    private async Task<CommandResult<ReversalResponse>> ReverseCoreAsync(
        ReverseTransferCommand command, CallerContext caller, IdempotencyKey idempotencyKey, CancellationToken cancellationToken)
    {
        if (!caller.IsOperator)
        {
            return CommandResult<ReversalResponse>.Rejected(ApplicationErrors.OperatorRoleRequired);
        }

        var reason = JournalEntry.ValidateDescription(command.Reason);
        if (reason.IsFailure)
        {
            return CommandResult<ReversalResponse>.Rejected(reason.Error);
        }

        if (reason.Value.Length == 0)
        {
            return CommandResult<ReversalResponse>.Rejected(ApplicationErrors.Validation("A reversal reason is required."));
        }

        var transferId = new TransferId(command.TransferId);
        if (await readStore.GetTransferAsync(transferId, cancellationToken) is null)
        {
            return CommandResult<ReversalResponse>.Rejected(ApplicationErrors.TransferNotFound(command.TransferId));
        }

        var fingerprint = RequestFingerprint.Compute(new ReversalFingerprint(command.TransferId, reason.Value));
        var scope = new IdempotencyScope(caller.Subject, Operation, idempotencyKey);

        return await executor.ExecuteAsync(
            scope,
            fingerprint,
            response => response.ReversalId,
            (tx, ct) => ExecuteLockedAsync(tx, transferId, reason.Value, caller, ct),
            cancellationToken);
    }

    private async Task<Result<ReversalResponse>> ExecuteLockedAsync(
        ILedgerTransaction tx, TransferId transferId, string reason, CallerContext caller, CancellationToken ct)
    {
        var stored = await tx.FindTransferAsync(transferId, ct);
        if (stored is null)
        {
            return ApplicationErrors.TransferNotFound(transferId.Value);
        }

        var transfer = stored.Transfer;
        var locked = await tx.LockAccountsAsync([transfer.FromWalletId, transfer.ToWalletId], [], ct);
        await tx.CheckpointAsync(FailurePoint.AfterAccountsLocked, ct);

        // Checked after the locks: a concurrent reversal of the same transfer locks the same accounts,
        // so it has either fully committed (and we see it here) or not started.
        var now = clock.GetUtcNow();
        if (await tx.IsTransferReversedAsync(transferId, ct))
        {
            return await RejectAsync(LedgerErrors.TransferAlreadyReversed(transferId));
        }

        var sender = locked.FindWallet(transfer.FromWalletId)
            ?? throw new InvalidOperationException($"Wallet {transfer.FromWalletId} referenced by transfer {transferId} is missing.");
        var receiver = locked.FindWallet(transfer.ToWalletId)
            ?? throw new InvalidOperationException($"Wallet {transfer.ToWalletId} referenced by transfer {transferId} is missing.");

        var planned = LedgerRules.PlanTransferReversal(
            stored.JournalEntry, JournalEntryId.New(), sender, receiver, transfer.AmountMinor, reason, now);
        if (planned.IsFailure)
        {
            return await RejectAsync(planned.Error);
        }

        var changes = planned.Value.CalculateBalanceChanges(locked.Accounts);
        if (changes.IsFailure)
        {
            return await RejectAsync(changes.Error);
        }

        var reversalEntry = planned.Value;
        var reversalId = Guid.CreateVersion7();
        await tx.InsertJournalEntryAsync(reversalEntry, caller.Subject, caller.CorrelationId, ct);
        await tx.ApplyBalanceChangesAsync(changes.Value, now, ct);
        await tx.InsertTransferReversalAsync(
            new TransferReversalRecord(reversalId, transferId, reversalEntry.Id, reason, caller.Subject, now), ct);
        await tx.EnqueueOutboxMessageAsync(
            OutboxMessageTypes.Create(
                OutboxMessageTypes.TransferReversed,
                new TransferReversedPayload(transferId.Value, reversalEntry.Id.Value, stored.JournalEntry.Id.Value,
                    transfer.AmountMinor, transfer.Currency, reason, now),
                caller.CorrelationId,
                now),
            ct);
        await tx.AppendAuditEventAsync(
            Audit.Event(caller, AuditActions.TransferReverse, AuditOutcome.Succeeded, "transfer", transferId.ToString(), now,
                ("reversalJournalEntryId", reversalEntry.Id.ToString()),
                ("amountMinor", transfer.AmountMinor.ToString(CultureInfo.InvariantCulture)),
                ("reason", reason)),
            ct);

        logger.LogInformation("Transfer {TransferId} reversed by {Subject} with entry {JournalEntryId}", transferId, caller.Subject, reversalEntry.Id);
        return new ReversalResponse(
            transferId.Value, reversalId, reversalEntry.Id.Value, stored.JournalEntry.Id.Value,
            transfer.AmountMinor, transfer.Currency, reason, now);

        async Task<Result<ReversalResponse>> RejectAsync(LedgerError rejection)
        {
            await tx.AppendAuditEventAsync(
                Audit.Event(caller, AuditActions.TransferReverse, AuditOutcome.Rejected, "transfer", transferId.ToString(), now,
                    ("code", rejection.Code), ("reason", reason)),
                ct);
            logger.LogInformation("Reversal of transfer {TransferId} rejected with {Code}", transferId, rejection.Code);
            return rejection;
        }
    }

    private sealed record ReversalFingerprint(Guid TransferId, string Reason);
}
