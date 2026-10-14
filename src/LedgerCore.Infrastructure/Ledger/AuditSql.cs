using System.Diagnostics;
using System.Text.Json;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using Npgsql;
using NpgsqlTypes;

namespace LedgerCore.Infrastructure.Ledger;

internal static class AuditSql
{
    public static async Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO audit_events (occurred_at, actor_id, action, outcome, resource_type, resource_id, correlation_id, details)
            VALUES (@occurred_at, @actor_id, @action, @outcome, @resource_type, @resource_id, @correlation_id, @details)
            """, connection, transaction);
        insert.Parameters.AddWithValue("occurred_at", auditEvent.OccurredAt);
        insert.Parameters.AddWithValue("actor_id", Truncate(auditEvent.ActorId, 128));
        insert.Parameters.AddWithValue("action", auditEvent.Action);
        insert.Parameters.AddWithValue("outcome", auditEvent.Outcome.ToString());
        insert.Parameters.Add(new NpgsqlParameter("resource_type", NpgsqlDbType.Varchar) { Value = (object?)auditEvent.ResourceType ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("resource_id", NpgsqlDbType.Varchar) { Value = (object?)auditEvent.ResourceId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("correlation_id", NpgsqlDbType.Varchar) { Value = (object?)auditEvent.CorrelationId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(auditEvent.Details, LedgerJson.Options),
        });
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Real process termination for crash-recovery tests. Only reachable through failure injection.</summary>
internal static class ProcessKiller
{
    public static void KillCurrentProcess(FailurePoint point)
    {
        // Written synchronously: the async console logger would not get a chance to flush.
        Console.Error.WriteLine($"[failure-injection] Killing process {Environment.ProcessId} at checkpoint {point}.");
        Console.Error.Flush();

        // TerminateProcess / SIGKILL: no finally blocks, no transaction rollback calls, no graceful shutdown.
        Process.GetCurrentProcess().Kill();

        // Kill() on the current process does not return; this guards against platform surprises.
        Environment.FailFast($"Failure injection at {point}: process kill did not take effect.");
    }
}
