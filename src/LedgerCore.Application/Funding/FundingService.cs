using System.Globalization;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Security;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Ledger;
using LedgerCore.Domain.Monetary;
using Microsoft.Extensions.Logging;

namespace LedgerCore.Application.Funding;

public sealed record FundWalletCommand(Guid WalletId, long AmountMinor, string Currency, string? Description = null);

public sealed record FundingResponse(
    Guid WalletId,
    Guid JournalEntryId,
    long AmountMinor,
    string Currency,
    string Description,
    DateTimeOffset CreatedAt);

/// <summary>
/// Simulated deposit for demonstrations. Exposed only in Development/Testing and only to operators.
/// It still produces a balanced entry against the funding expense account; balances are never edited directly.
/// </summary>
public sealed class FundingService(
    IdempotentCommandExecutor executor,
    ILedgerReadStore readStore,
    TimeProvider clock,
    ILogger<FundingService> logger)
{
    public const string Operation = "funding.create";

    public Task<CommandResult<FundingResponse>> FundAsync(
        FundWalletCommand command, CallerContext caller, IdempotencyKey idempotencyKey, CancellationToken cancellationToken) =>
        CommandInstrumentation.MeasureAsync(Operation, () => FundCoreAsync(command, caller, idempotencyKey, cancellationToken));

    private async Task<CommandResult<FundingResponse>> FundCoreAsync(
        FundWalletCommand command, CallerContext caller, IdempotencyKey idempotencyKey, CancellationToken cancellationToken)
    {
        if (!caller.IsOperator)
        {
            return CommandResult<FundingResponse>.Rejected(ApplicationErrors.OperatorRoleRequired);
        }

        if (command.WalletId == Guid.Empty)
        {
            return CommandResult<FundingResponse>.Rejected(ApplicationErrors.Validation("walletId is required."));
        }

        var currency = Currency.Parse(command.Currency);
        if (currency.IsFailure)
        {
            return CommandResult<FundingResponse>.Rejected(currency.Error);
        }

        var amount = Money.CreatePositive(command.AmountMinor, currency.Value);
        if (amount.IsFailure)
        {
            return CommandResult<FundingResponse>.Rejected(amount.Error);
        }

        var description = JournalEntry.ValidateDescription(
            string.IsNullOrWhiteSpace(command.Description) ? "Simulated funding" : command.Description);
        if (description.IsFailure)
        {
            return CommandResult<FundingResponse>.Rejected(description.Error);
        }

        var walletId = new WalletId(command.WalletId);
        if (await readStore.GetWalletAsync(walletId, cancellationToken) is null)
        {
            return CommandResult<FundingResponse>.Rejected(LedgerErrors.WalletNotFound(walletId));
        }

        var fingerprint = RequestFingerprint.Compute(new FundingFingerprint(walletId.Value, amount.Value.MinorUnits, currency.Value.Code, description.Value));
        var scope = new IdempotencyScope(caller.Subject, Operation, idempotencyKey);

        return await executor.ExecuteAsync(
            scope,
            fingerprint,
            response => response.JournalEntryId,
            (tx, ct) => ExecuteLockedAsync(tx, walletId, amount.Value, description.Value, caller, ct),
            cancellationToken);
    }

    private async Task<Result<FundingResponse>> ExecuteLockedAsync(
        ILedgerTransaction tx, WalletId walletId, Money amount, string description, CallerContext caller, CancellationToken ct)
    {
        var fundingAccountId = ChartOfAccounts.FundingExpenseAccountFor(amount.Currency);
        var locked = await tx.LockAccountsAsync([walletId], [fundingAccountId], ct);
        await tx.CheckpointAsync(FailurePoint.AfterAccountsLocked, ct);

        var wallet = locked.FindWallet(walletId);
        if (wallet is null)
        {
            return LedgerErrors.WalletNotFound(walletId);
        }

        var now = clock.GetUtcNow();
        var planned = LedgerRules.PlanFunding(JournalEntryId.New(), wallet, locked.Accounts[fundingAccountId], amount, description, now);
        if (planned.IsFailure)
        {
            return planned.Error;
        }

        var changes = planned.Value.CalculateBalanceChanges(locked.Accounts);
        if (changes.IsFailure)
        {
            return changes.Error;
        }

        var entry = planned.Value;
        await tx.InsertJournalEntryAsync(entry, caller.Subject, caller.CorrelationId, ct);
        await tx.ApplyBalanceChangesAsync(changes.Value, now, ct);
        await tx.EnqueueOutboxMessageAsync(
            OutboxMessageTypes.Create(
                OutboxMessageTypes.WalletFunded,
                new WalletFundedPayload(walletId.Value, entry.Id.Value, amount.MinorUnits, amount.Currency.Code, now),
                caller.CorrelationId,
                now),
            ct);
        await tx.AppendAuditEventAsync(
            Audit.Event(caller, AuditActions.FundingCreate, AuditOutcome.Succeeded, "wallet", walletId.ToString(), now,
                ("amountMinor", amount.MinorUnits.ToString(CultureInfo.InvariantCulture)),
                ("journalEntryId", entry.Id.ToString())),
            ct);

        logger.LogInformation("Simulated funding of {AmountMinor} {Currency} posted to wallet {WalletId}", amount.MinorUnits, amount.Currency.Code, walletId);
        return new FundingResponse(walletId.Value, entry.Id.Value, amount.MinorUnits, amount.Currency.Code, description, now);
    }

    private sealed record FundingFingerprint(Guid WalletId, long AmountMinor, string Currency, string Description);
}
