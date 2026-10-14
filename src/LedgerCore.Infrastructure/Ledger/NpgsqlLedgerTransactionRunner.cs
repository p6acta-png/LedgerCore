using System.Data;
using System.Diagnostics;
using System.Globalization;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LedgerCore.Infrastructure.Ledger;

/// <summary>
/// Owns the transaction boundary of every ledger command: open connection, BEGIN, set lock_timeout,
/// run the work, COMMIT, and retry the whole unit on transient failures.
/// </summary>
internal sealed class NpgsqlLedgerTransactionRunner(
    NpgsqlDataSource dataSource,
    IFailureInjector failureInjector,
    IOptions<LedgerOptions> options,
    ILogger<NpgsqlLedgerTransactionRunner> logger) : ILedgerTransactionRunner
{
    public async Task<T> ExecuteAsync<T>(
        string operationName,
        Func<ILedgerTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteOnceAsync(work, settings, cancellationToken);
            }
            catch (Exception ex) when (TransientFailures.IsRetryable(ex)
                                       && attempt < settings.MaxTransactionAttempts
                                       && !cancellationToken.IsCancellationRequested)
            {
                var reason = TransientFailures.Describe(ex);
                var delay = BackoffDelay(attempt, settings.RetryBaseDelay);
                LedgerTelemetry.TransactionRetries.Add(1, new TagList { { "operation", operationName }, { "reason", reason } });
                logger.LogWarning(
                    "Ledger transaction {Operation} attempt {Attempt} failed with {Reason}; rolled back, retrying in {DelayMs} ms",
                    operationName, attempt, reason, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (TransientFailures.IsLockTimeout(ex))
            {
                logger.LogWarning("Ledger transaction {Operation} timed out waiting for account locks", operationName);
                throw new LedgerTransientException(
                    "LOCK_TIMEOUT", "Timed out waiting for account locks. Retry later with the same Idempotency-Key.", ex);
            }
            catch (Exception ex) when (TransientFailures.IsRetryable(ex) && !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    "Ledger transaction {Operation} failed after {Attempts} attempts ({Reason})",
                    operationName, attempt, TransientFailures.Describe(ex));
                throw new LedgerTransientException(
                    "TRANSIENT_FAILURE", "The ledger is temporarily unavailable. Retry with the same Idempotency-Key.", ex);
            }
        }
    }

    private async Task<T> ExecuteOnceAsync<T>(
        Func<ILedgerTransaction, CancellationToken, Task<T>> work, LedgerOptions settings, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // `await using` guarantees ROLLBACK when anything below throws before CommitAsync completes.
        // If the process dies instead, PostgreSQL rolls back when the connection drops.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        await using (var setTimeout = new NpgsqlCommand("SELECT set_config('lock_timeout', @timeout, true)", connection, transaction))
        {
            setTimeout.Parameters.AddWithValue("timeout", ((long)settings.LockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms");
            await setTimeout.ExecuteNonQueryAsync(ct);
        }

        var ledger = new NpgsqlLedgerTransaction(connection, transaction, failureInjector);
        var result = await work(ledger, ct);

        await ledger.CheckpointAsync(FailurePoint.BeforeCommit, ct);
        await transaction.CommitAsync(ct);
        await ledger.CheckpointAsync(FailurePoint.AfterCommit, ct);
        return result;
    }

    private static TimeSpan BackoffDelay(int attempt, TimeSpan baseDelay)
    {
        var exponential = baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * baseDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(exponential + jitter);
    }
}
