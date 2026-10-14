using System.Net;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Transfers;
using LedgerCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LedgerCore.IntegrationTests;

/// <summary>
/// In-process failure injection against real PostgreSQL. "ThrowException" simulates a crash inside the
/// process (the exception unwinds and the transaction is rolled back); "TerminateDatabaseConnection" is a
/// genuine database-side failure. Real process termination is covered by <see cref="ProcessCrashTests"/>.
/// </summary>
public sealed class FailureInjectionTests(PostgresServer server) : ApiTestBase(server)
{
    private static Dictionary<string, string> FailAt(FailurePoint point, FailureMode mode = FailureMode.ThrowException) => new()
    {
        ["X-LedgerCore-Fail-At"] = point.ToString(),
        ["X-LedgerCore-Fail-Mode"] = mode.ToString(),
    };

    [Theory]
    [Trait("Challenge", "05")]
    [InlineData(FailurePoint.AfterIdempotencyClaim)]
    [InlineData(FailurePoint.AfterAccountsLocked)]
    [InlineData(FailurePoint.AfterFirstPosting)]
    [InlineData(FailurePoint.BeforeCommit)]
    public async Task Scenario_A_and_D_a_failure_before_commit_leaves_no_trace(FailurePoint point)
    {
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var entriesBefore = await Db.CountAsync("journal_entries");
        var postingsBefore = await Db.CountAsync("postings");

        using var failed = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 30_000, "fail-before-0001", headers: FailAt(point));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("SIMULATED_FAILURE", await LedgerTestClient.ErrorCodeAsync(failed));
        Assert.Equal(entriesBefore, await Db.CountAsync("journal_entries"));
        Assert.Equal(postingsBefore, await Db.CountAsync("postings"));
        Assert.Equal(0, await Db.CountAsync("transfers"));
        Assert.Equal(0, await Db.ScalarAsync("SELECT count(*) FROM idempotency_records WHERE idempotency_key = 'fail-before-0001'"));
        Assert.Equal(0, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE message_type = 'transfer.completed'"));
        Assert.Equal(100_000, await Db.WalletBalanceAsync(alice.WalletId));

        // The key was never committed, so the retry is a fresh, successful execution.
        using var retry = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 30_000, "fail-before-0001");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, await Db.CountAsync("transfers"));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    [Trait("Challenge", "06")]
    public async Task Scenario_B_commit_succeeds_but_the_response_is_lost_and_the_retry_replays()
    {
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        using var lost = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 30_000, "fail-after-00001", headers: FailAt(FailurePoint.AfterCommit));

        // The client saw an error, but the transfer is durable.
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.Equal(1, await Db.CountAsync("transfers"));

        using var retry = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 30_000, "fail-after-00001");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        var replayed = await LedgerTestClient.ReadAsync<TransferResponse>(retry);
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM transfers WHERE id = @id", ("id", replayed.TransferId)));
        Assert.Equal(1, await Db.CountAsync("transfers"));
        Assert.Equal(70_000, await Db.WalletBalanceAsync(alice.WalletId));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    [Trait("Challenge", "15")]
    public async Task Scenario_D_a_real_connection_loss_mid_postings_is_rolled_back_and_retried()
    {
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        // The server kills our backend between the first and second posting of the transfer.
        using var response = await Client.TransferAsync(
            alice.Token, alice.WalletId, bob.WalletId, 30_000, "fail-dbkill-0001",
            headers: FailAt(FailurePoint.AfterFirstPosting, FailureMode.TerminateDatabaseConnection));

        // The runner classified 57P01 as transient, discarded the broken transaction and retried once.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await LedgerTestClient.ReadAsync<TransferResponse>(response);
        Assert.Equal(1, await Db.CountAsync("transfers"));
        Assert.Equal(2, await Db.ScalarAsync("SELECT count(*) FROM postings WHERE journal_entry_id = @id", ("id", transfer.JournalEntryId)));
        Assert.Equal(70_000, await Db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(30_000, await Db.WalletBalanceAsync(bob.WalletId));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    public async Task Scenario_C_two_overlapping_spends_cannot_both_succeed_even_when_one_is_held_mid_transaction()
    {
        // Covered in depth (with a deterministic lock gate) by LedgerCore.ConcurrencyTests; this is the
        // sequential sanity check that the second spend sees the first one's committed balance.
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var carol = await Client.CreateFundedWalletAsync(0);

        using var first = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 80_000, "fail-c-first-001");
        using var second = await Client.TransferAsync(alice.Token, alice.WalletId, carol.WalletId, 80_000, "fail-c-second-01");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(20_000, await Db.WalletBalanceAsync(alice.WalletId));
    }
}

/// <summary>Uses a test injector that kills the connection on every attempt, to exhaust the retries.</summary>
public sealed class RetryExhaustionTests(PostgresServer server) : ApiTestBase(server)
{
    private sealed class AlwaysTerminate : IFailureInjector
    {
        public int Attempts;

        public ValueTask<FailureMode?> OnCheckpointAsync(FailurePoint point, CancellationToken cancellationToken)
        {
            if (point != FailurePoint.BeforeCommit)
            {
                return ValueTask.FromResult<FailureMode?>(null);
            }

            Interlocked.Increment(ref Attempts);
            return ValueTask.FromResult<FailureMode?>(FailureMode.TerminateDatabaseConnection);
        }
    }

    private readonly AlwaysTerminate _injector = new();

    protected override LedgerApiFactory CreateFactory(TestDatabase db) => new(
        db.ConnectionString,
        configureServices: services => services.Replace(ServiceDescriptor.Singleton<IFailureInjector>(_injector)));

    [Fact]
    public async Task When_every_attempt_fails_the_client_gets_a_retryable_503_and_nothing_is_committed()
    {
        // Wallets are created directly via SQL-free API calls before the injector matters: funding also
        // passes BeforeCommit, so create balances through a separate host without the injector.
        await using var setupHost = new LedgerApiFactory(Db.ConnectionString);
        var setup = TestTokens.ClientFor(setupHost);
        var alice = await setup.CreateFundedWalletAsync(10_000);
        var bob = await setup.CreateFundedWalletAsync(0);

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "retry-exhausted-1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("TRANSIENT_FAILURE", await LedgerTestClient.ErrorCodeAsync(response));
        Assert.True(response.Headers.RetryAfter is not null);
        Assert.Equal(4, _injector.Attempts);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
        Assert.DoesNotContain("57P01", body, StringComparison.Ordinal);
        Assert.Equal(0, await Db.CountAsync("transfers"));
        Assert.Equal(10_000, await Db.WalletBalanceAsync(alice.WalletId));
        await Db.AssertLedgerInvariantsAsync();
    }
}
