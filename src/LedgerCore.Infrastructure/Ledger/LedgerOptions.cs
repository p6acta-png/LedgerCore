namespace LedgerCore.Infrastructure.Ledger;

public sealed class LedgerOptions
{
    public const string SectionName = "Ledger";

    /// <summary>
    /// Maximum time a transaction waits for an account row lock (PostgreSQL lock_timeout). Bounded waits
    /// turn pathological contention into a clean, retryable 503 instead of piling up connections.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Total attempts (first try + retries) for deadlocks, serialization failures and lost connections.</summary>
    public int MaxTransactionAttempts { get; set; } = 4;

    /// <summary>First retry delay; doubles per attempt, plus random jitter of up to the same amount.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(20);
}
