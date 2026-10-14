using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LedgerCore.Application.Common;

/// <summary>
/// Metrics and tracing instruments. Built on System.Diagnostics, so they cost nothing unless a listener
/// (OpenTelemetry, dotnet-counters, a test) subscribes. Instrument names follow OpenTelemetry conventions.
/// </summary>
public static class LedgerTelemetry
{
    public const string SourceName = "LedgerCore";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>Ledger commands by operation and outcome (completed, replayed, rejected:CODE, failed).</summary>
    public static readonly Counter<long> Commands =
        Meter.CreateCounter<long>("ledgercore.commands", unit: "{command}", description: "Ledger commands by operation and outcome.");

    public static readonly Histogram<double> CommandDuration =
        Meter.CreateHistogram<double>("ledgercore.command.duration", unit: "ms", description: "End-to-end duration of ledger commands.");

    /// <summary>Time spent waiting in SELECT ... FOR UPDATE; a direct measure of row-lock contention.</summary>
    public static readonly Histogram<double> LockWaitDuration =
        Meter.CreateHistogram<double>("ledgercore.db.lock_wait.duration", unit: "ms", description: "Time spent acquiring ledger account row locks.");

    public static readonly Counter<long> TransactionRetries =
        Meter.CreateCounter<long>("ledgercore.db.transaction.retries", unit: "{retry}", description: "Ledger transactions retried after a transient database failure.");

    public static readonly Counter<long> OutboxDelivered =
        Meter.CreateCounter<long>("ledgercore.outbox.delivered", unit: "{message}", description: "Outbox messages delivered and acknowledged.");

    public static readonly Counter<long> OutboxFailures =
        Meter.CreateCounter<long>("ledgercore.outbox.failures", unit: "{message}", description: "Outbox delivery attempts that failed and were rescheduled.");

    public static readonly Counter<long> OutboxDeadLettered =
        Meter.CreateCounter<long>("ledgercore.outbox.dead_lettered", unit: "{message}", description: "Outbox messages that exhausted their retries.");

    public static readonly Counter<long> ReconciliationDiscrepancies =
        Meter.CreateCounter<long>("ledgercore.reconciliation.discrepancies", unit: "{account}", description: "Accounts whose cached balance disagreed with the journal.");

    public static void RecordCommand(string operation, string outcome, double elapsedMs)
    {
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        Commands.Add(1, tags);
        CommandDuration.Record(elapsedMs, tags);
    }
}
