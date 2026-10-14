using System.Globalization;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Application.Security;
using LedgerCore.Domain.Common;
using Microsoft.Extensions.Logging;

namespace LedgerCore.Application.Reconciliation;

public sealed record ReconciliationReport(
    DateTimeOffset GeneratedAt,
    bool IsConsistent,
    int AccountsChecked,
    long JournalEntriesChecked,
    long TotalDebitsMinor,
    long TotalCreditsMinor,
    IReadOnlyList<AccountDiscrepancy> Discrepancies,
    IReadOnlyList<Guid> UnbalancedJournalEntryIds,
    IReadOnlyList<Guid> NegativeWalletIds,
    IReadOnlyList<Guid> FrozenWalletIds);

/// <summary>
/// Recomputes balances from the journal (the source of truth) and compares them with the cached
/// projection. On a mismatch it does NOT silently "fix" the cache, because that would hide whatever bug
/// or manual tampering caused it. Instead it reports, alerts (metric + error log + audit event) and,
/// by default, freezes the affected wallets so nobody can spend against a balance that may be wrong.
/// </summary>
public sealed class ReconciliationService(
    ILedgerReadStore readStore,
    ILedgerTransactionRunner runner,
    IAuditLog auditLog,
    TimeProvider clock,
    ILogger<ReconciliationService> logger)
{
    public async Task<Result<ReconciliationReport>> RunAsync(CallerContext caller, bool freezeAffectedWallets, CancellationToken cancellationToken)
    {
        if (!caller.IsOperator)
        {
            return ApplicationErrors.OperatorRoleRequired;
        }

        using var activity = LedgerTelemetry.ActivitySource.StartActivity("ledger.reconciliation");
        var snapshot = await readStore.ComputeReconciliationAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var consistent = snapshot.Discrepancies.Count == 0
            && snapshot.UnbalancedJournalEntryIds.Count == 0
            && snapshot.NegativeWalletIds.Count == 0
            && snapshot.TotalDebitsMinor == snapshot.TotalCreditsMinor;

        IReadOnlyList<Guid> frozen = [];
        if (!consistent)
        {
            LedgerTelemetry.ReconciliationDiscrepancies.Add(snapshot.Discrepancies.Count);
            foreach (var d in snapshot.Discrepancies)
            {
                logger.LogError(
                    "Reconciliation mismatch on account {AccountCode} (wallet {WalletId}): cached {CachedBalance} vs journal {JournalBalance}",
                    d.AccountCode, d.WalletId, d.CachedBalanceMinor, d.JournalBalanceMinor);
            }

            if (snapshot.UnbalancedJournalEntryIds.Count > 0)
            {
                logger.LogCritical("Reconciliation found {Count} unbalanced journal entries", snapshot.UnbalancedJournalEntryIds.Count);
            }

            var affectedWallets = snapshot.Discrepancies
                .Where(d => d.WalletId is not null)
                .Select(d => new WalletId(d.WalletId!.Value))
                .Concat(snapshot.NegativeWalletIds.Select(id => new WalletId(id)))
                .Distinct()
                .ToArray();

            if (freezeAffectedWallets && affectedWallets.Length > 0)
            {
                frozen = await runner.ExecuteAsync("wallets.freeze", async (tx, ct) =>
                {
                    var changed = await tx.FreezeWalletsAsync(affectedWallets, ct);
                    foreach (var walletId in changed)
                    {
                        await tx.AppendAuditEventAsync(
                            Audit.Event(caller, AuditActions.WalletFreeze, AuditOutcome.Succeeded, "wallet", walletId.ToString(), now,
                                ("reason", "reconciliation mismatch")),
                            ct);
                    }

                    return changed.Select(id => id.Value).ToArray();
                }, cancellationToken);

                foreach (var walletId in frozen)
                {
                    logger.LogWarning("Wallet {WalletId} frozen after reconciliation mismatch", walletId);
                }
            }
        }

        await auditLog.AppendAsync(
            Audit.Event(caller, AuditActions.ReconciliationRun, consistent ? AuditOutcome.Succeeded : AuditOutcome.Rejected, "ledger", null, now,
                ("consistent", consistent ? "true" : "false"),
                ("accountsChecked", snapshot.AccountsChecked.ToString(CultureInfo.InvariantCulture)),
                ("discrepancies", snapshot.Discrepancies.Count.ToString(CultureInfo.InvariantCulture)),
                ("frozenWallets", frozen.Count.ToString(CultureInfo.InvariantCulture))),
            cancellationToken);

        activity?.SetTag("ledgercore.reconciliation.consistent", consistent);
        return new ReconciliationReport(
            now,
            consistent,
            snapshot.AccountsChecked,
            snapshot.JournalEntriesChecked,
            snapshot.TotalDebitsMinor,
            snapshot.TotalCreditsMinor,
            snapshot.Discrepancies,
            snapshot.UnbalancedJournalEntryIds,
            snapshot.NegativeWalletIds,
            frozen);
    }
}
