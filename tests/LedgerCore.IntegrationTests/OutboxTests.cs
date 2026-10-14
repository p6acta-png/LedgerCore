using System.Net;
using LedgerCore.Application.Abstractions;
using LedgerCore.Infrastructure.Outbox;
using LedgerCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LedgerCore.IntegrationTests;

/// <summary>
/// Transactional outbox: messages are written atomically with the ledger change and delivered at least
/// once by any number of concurrent dispatchers. The consumer deduplicates by message id.
/// </summary>
public sealed class OutboxTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    public async Task A_message_exists_if_and_only_if_the_transfer_committed()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var ok = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "outbox-ok-000001");
        using var rejected = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 999_999, "outbox-rej-00001");
        using var rolledBack = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "outbox-fail-0001",
            headers: new Dictionary<string, string> { ["X-LedgerCore-Fail-At"] = "BeforeCommit" });

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, rolledBack.StatusCode);
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE message_type = 'transfer.completed'"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages o JOIN transfers t ON (o.payload->>'transferId')::uuid = t.id"));
    }

    [Fact]
    public async Task The_dispatcher_delivers_and_acknowledges_every_message()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var transfer = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "outbox-deliver-01");
        var pending = await Db.CountAsync("outbox_messages");

        await using var scope = Factory.Services.CreateAsyncScope();
        var leased = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync("test-worker", CancellationToken.None);

        Assert.Equal(pending, leased);
        Assert.Equal(pending, await Db.CountAsync("simulated_notifications"));
        Assert.Equal(0, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM simulated_notifications WHERE message_type = 'transfer.completed'"));
    }

    [Fact]
    public async Task Failed_deliveries_back_off_exponentially_and_are_dead_lettered_after_max_attempts()
    {
        var sink = new FailingSink();
        await using var factory = new LedgerApiFactory(
            Db.ConnectionString,
            new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "3", ["Outbox:RetryBaseDelay"] = "00:00:00" },
            services => services.Replace(ServiceDescriptor.Singleton<INotificationSink>(sink)));
        await InsertOutboxMessagesAsync(1);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync("w", CancellationToken.None));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync("w", CancellationToken.None));
        }

        Assert.Equal(3, sink.Calls);
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE dead_lettered_at IS NOT NULL AND attempt_count = 3 AND processed_at IS NULL"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE last_error LIKE 'InvalidOperationException: downstream unavailable%'"));
    }

    [Fact]
    public async Task A_failed_delivery_is_rescheduled_with_a_delay()
    {
        await using var factory = new LedgerApiFactory(
            Db.ConnectionString,
            new Dictionary<string, string?> { ["Outbox:RetryBaseDelay"] = "00:00:10" },
            services => services.Replace(ServiceDescriptor.Singleton<INotificationSink>(new FailingSink())));
        await InsertOutboxMessagesAsync(1);

        await using var scope = factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        Assert.Equal(1, await dispatcher.DispatchBatchAsync("w", CancellationToken.None));

        // Not eligible again until the backoff has elapsed.
        Assert.Equal(0, await dispatcher.DispatchBatchAsync("w", CancellationToken.None));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE available_at > now() + interval '9 seconds'"));
    }

    [Fact]
    [Trait("Challenge", "09")]
    public async Task A_crash_between_delivery_and_acknowledgement_causes_a_deduplicated_redelivery()
    {
        await using var factory = new LedgerApiFactory(
            Db.ConnectionString,
            new Dictionary<string, string?> { ["Outbox:LeaseDuration"] = "00:00:01" },
            services => services.Replace(ServiceDescriptor.Singleton<INotificationSink>(provider =>
                new CrashOnceAfterDeliverySink(ActivatorUtilities.CreateInstance<DatabaseNotificationSink>(provider)))));
        await InsertOutboxMessagesAsync(1);

        // Worker A delivers, then "crashes" before acknowledging.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<SimulatedFailureException>(() =>
                scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync("worker-a", CancellationToken.None));
        }

        // While A's lease is valid, worker B cannot take the message.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync("worker-b", CancellationToken.None));
        }

        await WaitUntilAsync(async () => await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE locked_until < now()") == 1);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync("worker-b", CancellationToken.None));
        }

        // Delivered twice (at-least-once), but only one notification exists (idempotent consumer).
        Assert.Equal(1, await Db.CountAsync("simulated_notifications"));
        Assert.Equal(2, await Db.ScalarAsync("SELECT delivery_count FROM simulated_notifications"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE processed_at IS NOT NULL AND locked_by = 'worker-b'"));
    }

    [Fact]
    public async Task Concurrent_dispatchers_split_the_work_without_processing_a_message_twice()
    {
        await InsertOutboxMessagesAsync(300);

        var workers = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            var leasedTotal = 0;
            while (true)
            {
                await using var scope = Factory.Services.CreateAsyncScope();
                var leased = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync($"worker-{i}", CancellationToken.None);
                if (leased == 0)
                {
                    return leasedTotal;
                }

                leasedTotal += leased;
            }
        })).ToArray();
        var perWorker = await Task.WhenAll(workers);

        Assert.Equal(300, perWorker.Sum());
        Assert.Equal(300, await Db.CountAsync("simulated_notifications"));
        Assert.Equal(0, await Db.ScalarAsync("SELECT count(*) FROM simulated_notifications WHERE delivery_count <> 1"));
        Assert.Equal(300, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE processed_at IS NOT NULL"));
    }

    private async Task InsertOutboxMessagesAsync(int count) =>
        await Db.ExecuteAsync("""
            INSERT INTO outbox_messages (id, message_type, payload, created_at, available_at, attempt_count)
            SELECT gen_random_uuid(), 'test.message', jsonb_build_object('n', n), now(), now(), 0
            FROM generate_series(1, @count) AS n
            """, ("count", count));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 30 seconds");
            await Task.Delay(50);
        }
    }

    private sealed class FailingSink : INotificationSink
    {
        public int Calls;

        public Task DeliverAsync(LeasedOutboxMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("downstream unavailable");
        }
    }

    /// <summary>Delivers through the real sink, then simulates the process dying before the acknowledgement.</summary>
    private sealed class CrashOnceAfterDeliverySink(INotificationSink inner) : INotificationSink
    {
        private int _crashed;

        public async Task DeliverAsync(LeasedOutboxMessage message, CancellationToken cancellationToken)
        {
            await inner.DeliverAsync(message, cancellationToken);
            if (Interlocked.Exchange(ref _crashed, 1) == 0)
            {
                throw new SimulatedFailureException(FailurePoint.AfterCommit);
            }
        }
    }
}
