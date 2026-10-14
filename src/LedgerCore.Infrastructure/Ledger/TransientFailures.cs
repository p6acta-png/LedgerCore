using Npgsql;

namespace LedgerCore.Infrastructure.Ledger;

/// <summary>Classifies database exceptions for the transaction retry policy.</summary>
internal static class TransientFailures
{
    /// <summary>
    /// Failures after which re-running the whole transaction is safe and likely to succeed:
    ///   40001 serialization_failure, 40P01 deadlock_detected, and connection-level failures
    ///   (network errors, 57P01 admin_shutdown, ...), which Npgsql flags with IsTransient.
    /// Re-running is safe even when the failure hit COMMIT and the outcome is unknown, because the retried
    /// transaction starts by claiming the same idempotency key and replays a committed result.
    /// Lock timeouts are excluded: the queue is long, so the client should back off instead.
    /// </summary>
    public static bool IsRetryable(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } => false,
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => true,
        IdempotencyRecordVanishedException => true,
        NpgsqlException { IsTransient: true } => true,
        _ => false,
    };

    public static bool IsLockTimeout(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable };

    /// <summary>Short, secret-free label for logs and metric tags.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        PostgresException pg => pg.SqlState,
        IdempotencyRecordVanishedException => "idempotency-record-vanished",
        NpgsqlException => "connection",
        _ => exception.GetType().Name,
    };
}

/// <summary>
/// The idempotency key conflicted, but the conflicting row disappeared before it could be read
/// (removed by retention cleanup in between). Retrying the transaction resolves it.
/// </summary>
internal sealed class IdempotencyRecordVanishedException()
    : Exception("Idempotency record conflicted but could not be read; retrying.");

/// <summary>
/// The database disagreed with the domain about a balance computed under lock. This would mean a bug
/// or out-of-band modification; the transaction is rolled back rather than committing a guess.
/// </summary>
public sealed class LedgerInvariantViolationException(string message) : Exception(message);
