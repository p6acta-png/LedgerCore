using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.IntegrationTests;

/// <summary>
/// The real LedgerCore.Worker process is killed between "delivered" and "acknowledged" and then restarted.
/// Nothing is lost, and the one message that was in flight is redelivered and deduplicated by the consumer.
/// </summary>
public sealed class WorkerProcessTests(PostgresServer server) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await server.CreateDatabaseAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    [Trait("Challenge", "09")]
    public async Task A_worker_killed_mid_batch_loses_nothing_and_its_replacement_finishes_the_work()
    {
        await _db.ExecuteAsync("""
            INSERT INTO outbox_messages (id, message_type, payload, created_at, available_at, attempt_count)
            SELECT gen_random_uuid(), 'test.message', jsonb_build_object('n', n), now(), now(), 0
            FROM generate_series(1, 10) AS n
            """);
        var settings = new Dictionary<string, string>
        {
            ["Outbox__LeaseDuration"] = "00:00:02",
            ["Outbox__PollInterval"] = "00:00:00.100",
            ["Worker__ReconciliationInterval"] = "00:00:00",
        };

        // First worker: dies right after its 3rd delivery, before acknowledging it.
        await using (var crashing = ProcessHost.StartWorker(_db.ConnectionString, new Dictionary<string, string>(settings) { ["WorkerFailureInjection__KillAfterDeliveries"] = "3" }))
        {
            Assert.True(await crashing.WaitForExitAsync(TimeSpan.FromSeconds(60)), "worker should have been killed");
            Assert.Contains("[failure-injection] Killing worker", crashing.Output, StringComparison.Ordinal);
        }

        Assert.Equal(3, await _db.CountAsync("simulated_notifications"));
        Assert.Equal(2, await _db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE processed_at IS NOT NULL"));

        // Replacement worker: waits for the dead worker's leases to expire, then finishes everything.
        await using (var replacement = ProcessHost.StartWorker(_db.ConnectionString, settings))
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (await _db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL") > 0)
            {
                Assert.False(replacement.HasExited, "replacement worker exited unexpectedly:" + Environment.NewLine + replacement.Output);
                Assert.True(DateTime.UtcNow < deadline, "replacement worker did not drain the outbox:" + Environment.NewLine + replacement.Output);
                await Task.Delay(100);
            }
        }

        Assert.Equal(10, await _db.CountAsync("simulated_notifications"));
        Assert.Equal(1, await _db.ScalarAsync("SELECT count(*) FROM simulated_notifications WHERE delivery_count = 2"));
        Assert.Equal(11, await _db.ScalarAsync("SELECT sum(delivery_count) FROM simulated_notifications"));
    }
}
