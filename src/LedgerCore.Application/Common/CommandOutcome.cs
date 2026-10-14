using System.Diagnostics;
using LedgerCore.Application.Idempotency;

namespace LedgerCore.Application.Common;

/// <summary>Wraps a command with a trace span, a duration histogram and an outcome counter.</summary>
internal static class CommandInstrumentation
{
    public static async Task<CommandResult<T>> MeasureAsync<T>(string operation, Func<Task<CommandResult<T>>> command)
    {
        using var activity = LedgerTelemetry.ActivitySource.StartActivity(operation);
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            var result = await command();
            outcome = result switch
            {
                { Replayed: true } => "replayed",
                { Result.IsSuccess: true } => "completed",
                _ => "rejected:" + result.Result.Error.Code,
            };
            return result;
        }
        finally
        {
            activity?.SetTag("ledgercore.outcome", outcome);
            LedgerTelemetry.RecordCommand(operation, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
