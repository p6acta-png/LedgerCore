using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Security;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Monetary;
using Microsoft.Extensions.Logging;

namespace LedgerCore.Application.Transfers;

/// <summary>Client input for POST /api/transfers. Amounts are integer minor units (øre).</summary>
public sealed record CreateTransferCommand(
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountMinor,
    string Currency,
    string? Description = null);

public sealed record TransferResponse(
    Guid TransferId,
    string Status,
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountMinor,
    string Currency,
    string Description,
    Guid JournalEntryId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Moves simulated money between two customer wallets. This class is the readable, step-by-step
/// version of the transfer algorithm; the SQL behind each step lives in
/// Infrastructure/Ledger/NpgsqlLedgerTransaction.cs.
/// </summary>
public sealed class TransferService(
    IdempotentCommandExecutor executor,
    ILedgerReadStore readStore,
    IAuditLog auditLog,
    TimeProvider clock,
    ILogger<TransferService> logger)
{
    public const string Operation = "transfers.create";

    public Task<CommandResult<TransferResponse>> CreateAsync(
        CreateTransferCommand command, CallerContext caller, IdempotencyKey idempotencyKey, CancellationToken cancellationToken) =>
        CommandInstrumentation.MeasureAsync(Operation, () => CreateCoreAsync(command, caller, idempotencyKey, cancellationToken));

    internal static Result<ValidatedTransfer> Validate(CreateTransferCommand command)
    {
        if (command.FromWalletId == Guid.Empty || command.ToWalletId == Guid.Empty)
        {
            return ApplicationErrors.Validation("fromWalletId and toWalletId are required.");
        }

        if (command.FromWalletId == command.ToWalletId)
        {
            return LedgerErrors.SelfTransfer;
        }

        var currency = Currency.Parse(command.Currency);
        if (currency.IsFailure)
        {
            return currency.Error;
        }

        var amount = Money.CreatePositive(command.AmountMinor, currency.Value);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        var description = JournalEntry.ValidateDescription(command.Description);
        if (description.IsFailure)
        {
            return description.Error;
        }

        return new ValidatedTransfer(new WalletId(command.FromWalletId), new WalletId(command.ToWalletId), amount.Value, description.Value);
    }

    private async Task<CommandResult<TransferResponse>> CreateCoreAsync(
        CreateTransferCommand command, CallerContext caller, IdempotencyKey idempotencyKey, CancellationToken cancellationToken)
    {
        // Step 1 (authentication) happened in the API: CallerContext only exists for a validated token.
        if (!caller.IsCustomer || caller.CustomerId is not { } customerId)
        {
            return CommandResult<TransferResponse>.Rejected(ApplicationErrors.CustomerRoleRequired);
        }

        // Step 3: validate input before touching the database.
        var validation = Validate(command);
        if (validation.IsFailure)
        {
            return CommandResult<TransferResponse>.Rejected(validation.Error);
        }

        var transfer = validation.Value;

        // Step 2: authorization. Wallet ownership never changes, so checking it before the transaction is
        // race-free; LedgerRules.PlanTransfer re-checks it against the locked rows anyway.
        var source = await readStore.GetWalletAsync(transfer.From, cancellationToken);
        if (source is null)
        {
            return CommandResult<TransferResponse>.Rejected(LedgerErrors.WalletNotFound(transfer.From));
        }

        if (!WalletAccessPolicy.CanDebit(caller, new CustomerId(source.CustomerId)))
        {
            await auditLog.AppendAsync(
                Audit.Event(caller, AuditActions.TransferCreate, AuditOutcome.Denied, "wallet", transfer.From.ToString(), clock.GetUtcNow(),
                    ("reason", "source wallet is not owned by the caller"),
                    ("toWalletId", transfer.To.ToString()),
                    ("amountMinor", transfer.Amount.MinorUnits.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                cancellationToken);
            logger.LogWarning(
                "Denied transfer: subject {Subject} attempted to debit wallet {WalletId} owned by another customer",
                caller.Subject, transfer.From);
            return CommandResult<TransferResponse>.Rejected(LedgerErrors.SourceWalletNotOwned);
        }

        if (await readStore.GetWalletAsync(transfer.To, cancellationToken) is null)
        {
            return CommandResult<TransferResponse>.Rejected(LedgerErrors.WalletNotFound(transfer.To));
        }

        // Steps 4-13 run inside one database transaction (see IdempotentCommandExecutor).
        var fingerprint = RequestFingerprint.Compute(new TransferFingerprint(
            transfer.From.Value, transfer.To.Value, transfer.Amount.MinorUnits, transfer.Amount.Currency.Code, transfer.Description));
        var scope = new IdempotencyScope(caller.Subject, Operation, idempotencyKey);

        return await executor.ExecuteAsync(
            scope,
            fingerprint,
            response => response.TransferId,
            (tx, ct) => ExecuteLockedAsync(tx, transfer, customerId, caller, idempotencyKey, ct),
            cancellationToken);
    }

    private async Task<Result<TransferResponse>> ExecuteLockedAsync(
        ILedgerTransaction tx,
        ValidatedTransfer transfer,
        CustomerId customerId,
        CallerContext caller,
        IdempotencyKey idempotencyKey,
        CancellationToken ct)
    {
        // Step 6: lock both wallet accounts (one statement, deterministic order). From here until COMMIT
        // no other transaction can change these balances.
        var locked = await tx.LockAccountsAsync([transfer.From, transfer.To], [], ct);
        await tx.CheckpointAsync(FailurePoint.AfterAccountsLocked, ct);

        var from = locked.FindWallet(transfer.From);
        var to = locked.FindWallet(transfer.To);
        if (from is null || to is null)
        {
            return LedgerErrors.WalletNotFound(from is null ? transfer.From : transfer.To);
        }

        var now = clock.GetUtcNow();

        // Step 7 + 8: verify funds and build a balanced journal entry (pure domain logic).
        var planned = LedgerRules.PlanTransfer(JournalEntryId.New(), customerId, from, to, transfer.Amount, transfer.Description, now);
        if (planned.IsFailure)
        {
            return await RejectAsync(planned.Error);
        }

        var changes = planned.Value.CalculateBalanceChanges(locked.Accounts);
        if (changes.IsFailure)
        {
            return await RejectAsync(changes.Error);
        }

        var journalEntry = planned.Value;
        var transferId = TransferId.New();

        // Step 9: append the entry and both postings.
        await tx.InsertJournalEntryAsync(journalEntry, caller.Subject, caller.CorrelationId, ct);

        // Step 10: update the cached balances in the same transaction.
        await tx.ApplyBalanceChangesAsync(changes.Value, now, ct);

        await tx.InsertTransferAsync(
            new TransferRecord(transferId, journalEntry.Id, transfer.From, transfer.To, transfer.Amount.MinorUnits,
                transfer.Amount.Currency.Code, transfer.Description, customerId, idempotencyKey.Value, now),
            ct);

        // The notification is written to the outbox in the same transaction: it exists if and only if
        // the transfer committed. Delivery happens later, at least once, in the worker.
        await tx.EnqueueOutboxMessageAsync(
            OutboxMessageTypes.Create(
                OutboxMessageTypes.TransferCompleted,
                new TransferCompletedPayload(transferId.Value, journalEntry.Id.Value, transfer.From.Value, transfer.To.Value,
                    transfer.Amount.MinorUnits, transfer.Amount.Currency.Code, now),
                caller.CorrelationId,
                now),
            ct);

        await tx.AppendAuditEventAsync(
            Audit.Event(caller, AuditActions.TransferCreate, AuditOutcome.Succeeded, "transfer", transferId.ToString(), now,
                ("fromWalletId", transfer.From.ToString()), ("toWalletId", transfer.To.ToString()),
                ("amountMinor", transfer.Amount.MinorUnits.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("journalEntryId", journalEntry.Id.ToString())),
            ct);

        logger.LogInformation(
            "Transfer {TransferId} posted: {AmountMinor} {Currency} from wallet {FromWalletId} to {ToWalletId}",
            transferId, transfer.Amount.MinorUnits, transfer.Amount.Currency.Code, transfer.From, transfer.To);

        // Steps 11-13 (store outcome on the idempotency key, commit, respond) happen in the executor/runner.
        return new TransferResponse(
            transferId.Value, "Completed", transfer.From.Value, transfer.To.Value, transfer.Amount.MinorUnits,
            transfer.Amount.Currency.Code, transfer.Description, journalEntry.Id.Value, now);

        // A business rejection is still a completed, idempotent outcome: it is audited and stored with the key.
        async Task<Result<TransferResponse>> RejectAsync(LedgerError rejection)
        {
            await tx.AppendAuditEventAsync(
                Audit.Event(caller, AuditActions.TransferCreate, AuditOutcome.Rejected, "wallet", transfer.From.ToString(), now,
                    ("code", rejection.Code), ("toWalletId", transfer.To.ToString()),
                    ("amountMinor", transfer.Amount.MinorUnits.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                ct);
            logger.LogInformation("Transfer rejected with {Code} for wallet {WalletId}", rejection.Code, transfer.From);
            return rejection;
        }
    }

    internal sealed record ValidatedTransfer(WalletId From, WalletId To, Money Amount, string Description);

    /// <summary>Canonical form hashed for idempotency; property order is fixed by the record.</summary>
    private sealed record TransferFingerprint(Guid FromWalletId, Guid ToWalletId, long AmountMinor, string Currency, string Description);
}
