namespace LedgerCore.ConcurrencyTests;

/// <summary>
/// Releases many prepared requests at the same instant to maximise overlap. Every task first signals
/// that it is ready and then awaits the gate, so no request starts before all of them are queued.
/// </summary>
internal static class StartGate
{
    public static async Task<T[]> RunConcurrentlyAsync<T>(int count, Func<int, Task<T>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(count);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            ready.Signal();
            await gate.Task;
            return await action(i);
        })).ToArray();

        ready.Wait(TimeSpan.FromSeconds(30));
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}
