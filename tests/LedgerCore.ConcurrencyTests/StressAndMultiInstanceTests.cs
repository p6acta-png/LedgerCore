using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.ConcurrencyTests;

/// <summary>
/// Many wallets, many simultaneous transfers in both directions (A->B and B->A at the same time is the
/// classic deadlock recipe). Deterministic lock ordering means PostgreSQL never has to break a deadlock.
/// </summary>
public sealed class ContentionStressTests(PostgresServer server) : ApiTestBase(server)
{
    [Theory]
    [Trait("Challenge", "12")]
    [InlineData(7)]
    [InlineData(2026)]
    public async Task Random_crossing_transfers_conserve_money_and_never_deadlock(int seed)
    {
        const int walletCount = 8;
        const long opening = 100_000;
        var wallets = new List<TestWallet>();
        for (var i = 0; i < walletCount; i++)
        {
            wallets.Add(await Client.CreateFundedWalletAsync(opening, $"W{i}"));
        }

        var random = new Random(seed);
        var plan = Enumerable.Range(0, 400).Select(i =>
        {
            var from = random.Next(walletCount);
            var to = (from + 1 + random.Next(walletCount - 1)) % walletCount;
            return (From: wallets[from], To: wallets[to], Amount: (long)random.Next(1, 40_000), Key: $"stress-{seed}-{i:D5}");
        }).ToArray();

        var deadlockRetries = 0;
        using var listener = ListenForRetries(reason => { if (reason == "40P01") { Interlocked.Increment(ref deadlockRetries); } });

        using var throttle = new SemaphoreSlim(32);
        var statuses = await Task.WhenAll(plan.Select(async step =>
        {
            await throttle.WaitAsync();
            try
            {
                using var response = await Client.TransferAsync(step.From.Token, step.From.WalletId, step.To.WalletId, step.Amount, step.Key);
                return response.StatusCode;
            }
            finally
            {
                throttle.Release();
            }
        }));

        Assert.All(statuses, s => Assert.True(s is HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity, $"unexpected status {s}"));
        Assert.Equal(0, deadlockRetries);
        Assert.Equal(statuses.Count(s => s == HttpStatusCode.Created), await Db.CountAsync("transfers"));

        long total = 0;
        foreach (var wallet in wallets)
        {
            total += await Db.WalletBalanceAsync(wallet.WalletId);
        }

        Assert.Equal(walletCount * opening, total);
        await Db.AssertLedgerInvariantsAsync();
    }

    internal static MeterListener ListenForRetries(Action<string> onRetry)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "LedgerCore" && instrument.Name == "ledgercore.db.transaction.retries")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    onRetry((string)tag.Value!);
                }
            }
        });
        listener.Start();
        return listener;
    }
}

/// <summary>
/// Two independent API hosts (separate DI containers, separate connection pools, no shared memory)
/// against one database: the same topology as two containers behind a load balancer. Correctness must
/// come from PostgreSQL, because there is nothing else they share.
/// </summary>
public sealed class MultiInstanceTests(PostgresServer server) : ApiTestBase(server)
{
    private LedgerApiFactory _secondInstance = null!;
    private LedgerTestClient _second = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _secondInstance = new LedgerApiFactory(Db.ConnectionString);
        _second = TestTokens.ClientFor(_secondInstance);
    }

    public override async ValueTask DisposeAsync()
    {
        await _secondInstance.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    [Trait("Challenge", "13")]
    public async Task Competing_spends_through_different_instances_cannot_both_succeed()
    {
        const int rounds = 10;
        for (var round = 0; round < rounds; round++)
        {
            var a = await Client.CreateFundedWalletAsync(100_000);
            var b = await Client.CreateFundedWalletAsync(0);
            var c = await Client.CreateFundedWalletAsync(0);

            var responses = await StartGate.RunConcurrentlyAsync(2, i => i == 0
                ? Client.TransferAsync(a.Token, a.WalletId, b.WalletId, 80_000, $"multi-{round:D3}-0")
                : _second.TransferAsync(a.Token, a.WalletId, c.WalletId, 80_000, $"multi-{round:D3}-1"));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
            Assert.Equal(20_000, await Db.WalletBalanceAsync(a.WalletId));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    [Trait("Challenge", "13")]
    public async Task Duplicate_requests_split_across_instances_create_one_transfer()
    {
        var a = await Client.CreateFundedWalletAsync(100_000);
        var b = await Client.CreateFundedWalletAsync(0);

        var responses = await StartGate.RunConcurrentlyAsync(100, i => (i % 2 == 0 ? Client : _second)
            .TransferAsync(a.Token, a.WalletId, b.WalletId, 5_000, "multi-dup-key-001"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(99, responses.Count(r => r.Headers.Contains("Idempotent-Replayed")));
        Assert.Equal(1, await Db.CountAsync("transfers"));
        Assert.Equal(95_000, await Db.WalletBalanceAsync(a.WalletId));
        await Db.AssertLedgerInvariantsAsync();
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Mixed_load_across_instances_keeps_every_invariant()
    {
        var wallets = new List<TestWallet>();
        for (var i = 0; i < 5; i++)
        {
            wallets.Add(await Client.CreateFundedWalletAsync(50_000));
        }

        var random = new Random(99);
        var outcomes = new ConcurrentBag<HttpStatusCode>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 300), new ParallelOptions { MaxDegreeOfParallelism = 24 }, async (i, _) =>
        {
            int from, to;
            long amount;
            lock (random)
            {
                from = random.Next(wallets.Count);
                to = (from + 1 + random.Next(wallets.Count - 1)) % wallets.Count;
                amount = random.Next(1, 30_000);
            }

            var client = i % 2 == 0 ? Client : _second;
            using var response = await client.TransferAsync(wallets[from].Token, wallets[from].WalletId, wallets[to].WalletId, amount, $"mixed-{i:D6}");
            outcomes.Add(response.StatusCode);
        });

        Assert.All(outcomes, s => Assert.True(s is HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity, $"unexpected status {s}"));
        long total = 0;
        foreach (var wallet in wallets)
        {
            total += await Db.WalletBalanceAsync(wallet.WalletId);
        }

        Assert.Equal(250_000, total);
        await Db.AssertLedgerInvariantsAsync();
    }
}
