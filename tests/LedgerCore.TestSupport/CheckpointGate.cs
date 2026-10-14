using LedgerCore.Application.Abstractions;

namespace LedgerCore.TestSupport;

/// <summary>
/// Test failure injector that pauses the first request reaching a checkpoint until the test releases it.
/// This reproduces a race deterministically: request A is frozen while holding its row locks, request B is
/// started, the test proves B is blocked by A in PostgreSQL, and only then lets A continue.
/// </summary>
public sealed class CheckpointGate(FailurePoint point) : IFailureInjector
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    /// <summary>Completes when the first request is paused at the checkpoint.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Starts intercepting: setup traffic before this call passes through untouched.</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Release() => _released.TrySetResult();

    public async ValueTask<FailureMode?> OnCheckpointAsync(FailurePoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint == point && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _reached.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        return null;
    }
}

/// <summary>Counts how often each checkpoint was passed; useful to prove a retry really happened.</summary>
public sealed class CheckpointCounter(FailurePoint point, FailureMode? failFirstWith = null) : IFailureInjector
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public ValueTask<FailureMode?> OnCheckpointAsync(FailurePoint checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint != point)
        {
            return ValueTask.FromResult<FailureMode?>(null);
        }

        var n = Interlocked.Increment(ref _count);
        return ValueTask.FromResult(n == 1 ? failFirstWith : null);
    }
}
