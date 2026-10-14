namespace LedgerCore.Application.Abstractions;

/// <summary>Named points in a ledger transaction where a failure can be injected.</summary>
public enum FailurePoint
{
    AfterIdempotencyClaim,
    AfterAccountsLocked,
    AfterFirstPosting,
    BeforeCommit,
    AfterCommit,
}

public enum FailureMode
{
    /// <summary>Throws <see cref="SimulatedFailureException"/>: an in-process simulation of a crash.</summary>
    ThrowException,

    /// <summary>
    /// Runs pg_terminate_backend on the transaction's own connection: a real database-side failure.
    /// The server aborts the transaction and the client sees a genuine connection error.
    /// </summary>
    TerminateDatabaseConnection,

    /// <summary>Kills the current OS process immediately (no finally blocks, no graceful shutdown).</summary>
    KillProcess,
}

/// <summary>
/// Decides whether a failure should be injected at a checkpoint. The production implementation always
/// answers "no". Test implementations may also pause here to force a specific interleaving of two requests.
/// </summary>
public interface IFailureInjector
{
    ValueTask<FailureMode?> OnCheckpointAsync(FailurePoint point, CancellationToken cancellationToken);
}

public sealed class NoFailureInjector : IFailureInjector
{
    public static readonly NoFailureInjector Instance = new();

    public ValueTask<FailureMode?> OnCheckpointAsync(FailurePoint point, CancellationToken cancellationToken) =>
        ValueTask.FromResult<FailureMode?>(null);
}

public sealed class SimulatedFailureException(FailurePoint point)
    : Exception($"Simulated failure injected at checkpoint {point}.")
{
    public FailurePoint Point { get; } = point;
}
