using System.Globalization;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LedgerCore.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How long a worker owns a leased message before another worker may take it over.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public int BatchSize { get; set; } = 50;

    /// <summary>After this many attempts a message is dead-lettered and left for an operator.</summary>
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>A leased outbox message handed to a sink.</summary>
public sealed record LeasedOutboxMessage(Guid Id, string Type, string PayloadJson, string? CorrelationId, int AttemptCount);

/// <summary>Destination of outbox messages. Implementations MUST be idempotent: delivery is at-least-once.</summary>
public interface INotificationSink
{
    Task DeliverAsync(LeasedOutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Moves outbox messages to the sink with at-least-once semantics.
///
///   1. Lease a batch: UPDATE ... WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED). Concurrent workers
///      skip each other's rows instead of blocking, and a lease expires if its worker dies.
///   2. Deliver each message (outside any database transaction).
///   3. Acknowledge (processed_at) or reschedule with exponential backoff; dead-letter after MaxAttempts.
///
/// A crash between 2 and 3 means the message is delivered again after the lease expires. That is why
/// this is at-least-once and not exactly-once, and why the sink deduplicates by message id.
/// </summary>
public sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    INotificationSink sink,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger)
{
    public async Task<int> DispatchBatchAsync(string workerId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var batch = await LeaseBatchAsync(workerId, settings, cancellationToken);
        foreach (var message in batch)
        {
            try
            {
                await sink.DeliverAsync(message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SimulatedFailureException)
            {
                // SimulatedFailureException stands for a process crash, so it deliberately skips this
                // bookkeeping: the lease simply expires, exactly as after a real crash.
                await RecordFailureAsync(message, workerId, ex, settings, cancellationToken);
                continue;
            }

            await AcknowledgeAsync(message, workerId, cancellationToken);
        }

        return batch.Count;
    }

    private async Task<IReadOnlyList<LeasedOutboxMessage>> LeaseBatchAsync(string workerId, OutboxOptions settings, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages
            SET locked_until = now() + make_interval(secs => @lease_seconds),
                locked_by = @worker_id,
                attempt_count = attempt_count + 1
            WHERE id IN (
                SELECT id FROM outbox_messages
                WHERE processed_at IS NULL
                  AND dead_lettered_at IS NULL
                  AND available_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                ORDER BY available_at, id
                LIMIT @batch_size
                FOR UPDATE SKIP LOCKED)
            RETURNING id, message_type, payload::text, correlation_id, attempt_count
            """);
        command.Parameters.AddWithValue("lease_seconds", settings.LeaseDuration.TotalSeconds);
        command.Parameters.AddWithValue("worker_id", workerId);
        command.Parameters.AddWithValue("batch_size", settings.BatchSize);

        var messages = new List<LeasedOutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new LeasedOutboxMessage(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)));
        }

        return messages;
    }

    private async Task AcknowledgeAsync(LeasedOutboxMessage message, string workerId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages
            SET processed_at = now(), locked_until = NULL, last_error = NULL
            WHERE id = @id AND locked_by = @worker_id AND processed_at IS NULL
            """);
        command.Parameters.AddWithValue("id", message.Id);
        command.Parameters.AddWithValue("worker_id", workerId);
        if (await command.ExecuteNonQueryAsync(ct) == 1)
        {
            LedgerTelemetry.OutboxDelivered.Add(1);
        }
        else
        {
            // Our lease expired and another worker took the message over: it will be (or was) delivered
            // again. Harmless because the sink deduplicates, but worth knowing about.
            logger.LogWarning("Outbox message {MessageId} was delivered but its lease had moved to another worker", message.Id);
        }
    }

    private async Task RecordFailureAsync(LeasedOutboxMessage message, string workerId, Exception error, OutboxOptions settings, CancellationToken ct)
    {
        var backoff = TimeSpan.FromTicks(Math.Min(
            settings.MaxRetryDelay.Ticks,
            settings.RetryBaseDelay.Ticks * (long)Math.Pow(2, Math.Min(message.AttemptCount - 1, 20))));
        var deadLetter = message.AttemptCount >= settings.MaxAttempts;

        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages
            SET locked_until = NULL,
                locked_by = NULL,
                last_error = @last_error,
                available_at = now() + make_interval(secs => @backoff_seconds),
                dead_lettered_at = CASE WHEN @dead_letter THEN now() ELSE NULL END
            WHERE id = @id AND locked_by = @worker_id
            """);
        command.Parameters.AddWithValue("id", message.Id);
        command.Parameters.AddWithValue("worker_id", workerId);
        command.Parameters.AddWithValue("backoff_seconds", backoff.TotalSeconds);
        command.Parameters.AddWithValue("dead_letter", deadLetter);

        // Only the exception type and message are stored: no stack traces or connection details.
        var text = $"{error.GetType().Name}: {error.Message}";
        command.Parameters.AddWithValue("last_error", text.Length > 1000 ? text[..1000] : text);
        await command.ExecuteNonQueryAsync(ct);

        if (deadLetter)
        {
            LedgerTelemetry.OutboxDeadLettered.Add(1);
            logger.LogError("Outbox message {MessageId} ({MessageType}) dead-lettered after {Attempts} attempts", message.Id, message.Type, message.AttemptCount);
        }
        else
        {
            LedgerTelemetry.OutboxFailures.Add(1);
            logger.LogWarning(
                "Outbox message {MessageId} delivery attempt {Attempt} failed ({ErrorType}); next attempt in {BackoffSeconds} s",
                message.Id, message.AttemptCount, error.GetType().Name, backoff.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }
}

public sealed class SimulatedSinkOptions
{
    public const string SectionName = "Outbox:SimulatedSink";

    /// <summary>Probability (0..1) that a delivery fails, to exercise retries and backoff in demos.</summary>
    public double FailureRate { get; set; }
}

/// <summary>
/// Stand-in for an external notification service, implemented as its own table written over its own
/// connection (a separate transaction from the outbox acknowledgement, just as a real remote system would be).
/// Idempotent consumer: the message id is the primary key, so duplicates do not create a second
/// notification; delivery_count makes the duplicates observable.
/// </summary>
public sealed class DatabaseNotificationSink(NpgsqlDataSource dataSource, IOptions<SimulatedSinkOptions> options) : INotificationSink
{
    public async Task DeliverAsync(LeasedOutboxMessage message, CancellationToken cancellationToken)
    {
        if (options.Value.FailureRate > 0 && Random.Shared.NextDouble() < options.Value.FailureRate)
        {
            throw new SimulatedDeliveryException(message.Id);
        }

        await using var command = dataSource.CreateCommand("""
            INSERT INTO simulated_notifications (message_id, message_type, payload, delivery_count, first_delivered_at, last_delivered_at)
            VALUES (@id, @type, @payload::jsonb, 1, now(), now())
            ON CONFLICT (message_id) DO UPDATE
            SET delivery_count = simulated_notifications.delivery_count + 1, last_delivered_at = now()
            """);
        command.Parameters.AddWithValue("id", message.Id);
        command.Parameters.AddWithValue("type", message.Type);
        command.Parameters.AddWithValue("payload", message.PayloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class SimulatedDeliveryException(Guid messageId)
    : Exception($"Simulated downstream failure while delivering outbox message {messageId}.");

/// <summary>Deletes idempotency records past their retention window, in small batches.</summary>
public sealed class IdempotencyCleaner(NpgsqlDataSource dataSource, ILogger<IdempotencyCleaner> logger)
{
    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            await using var command = dataSource.CreateCommand("""
                DELETE FROM idempotency_records r
                USING (
                    SELECT principal_id, operation, idempotency_key
                    FROM idempotency_records
                    WHERE expires_at < @now
                    LIMIT @batch_size
                    FOR UPDATE SKIP LOCKED) expired
                WHERE r.principal_id = expired.principal_id
                  AND r.operation = expired.operation
                  AND r.idempotency_key = expired.idempotency_key
                """);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("batch_size", batchSize);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < batchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Deleted {Count} expired idempotency records", total);
        }

        return total;
    }
}
