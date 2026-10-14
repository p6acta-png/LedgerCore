using System.Net;
using LedgerCore.Application.Transfers;
using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.IntegrationTests;

/// <summary>
/// Real process termination. The API runs as a separate OS process and kills itself (TerminateProcess /
/// SIGKILL) at a checkpoint: no exception handler, finally block or rollback call runs. Recovery depends
/// only on PostgreSQL aborting the orphaned transaction and on the persisted idempotency record.
/// </summary>
public sealed class ProcessCrashTests(PostgresServer server) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await server.CreateDatabaseAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    [Trait("Challenge", "05")]
    public async Task Scenario_A_process_killed_before_commit_leaves_no_partial_transfer()
    {
        TestWallet alice;
        TestWallet bob;
        await using (var api = await ProcessHost.StartApiAsync(_db.ConnectionString))
        {
            var client = ClientFor(api);
            alice = await client.CreateFundedWalletAsync(100_000);
            bob = await client.CreateFundedWalletAsync(0);

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.TransferAsync(
                alice.Token, alice.WalletId, bob.WalletId, 30_000, "crash-before-0001",
                headers: new Dictionary<string, string> { ["X-LedgerCore-Fail-At"] = "BeforeCommit", ["X-LedgerCore-Fail-Mode"] = "KillProcess" }));

            Assert.True(await api.WaitForExitAsync(TimeSpan.FromSeconds(30)), "the API process should have died");
            Assert.Contains("[failure-injection] Killing process", api.Output, StringComparison.Ordinal);
        }

        // PostgreSQL saw the connection drop and rolled the open transaction back.
        Assert.Equal(0, await _db.CountAsync("transfers"));
        Assert.Equal(0, await _db.ScalarAsync("SELECT count(*) FROM idempotency_records WHERE idempotency_key = 'crash-before-0001'"));
        Assert.Equal(100_000, await _db.WalletBalanceAsync(alice.WalletId));
        await _db.AssertLedgerInvariantsAsync();

        await using (var restarted = await ProcessHost.StartApiAsync(_db.ConnectionString))
        {
            using var retry = await ClientFor(restarted).TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 30_000, "crash-before-0001");
            Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
            Assert.False(retry.Headers.Contains("Idempotent-Replayed"));
        }

        Assert.Equal(1, await _db.CountAsync("transfers"));
        Assert.Equal(70_000, await _db.WalletBalanceAsync(alice.WalletId));
        await _db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    [Trait("Challenge", "06")]
    public async Task Scenario_B_process_killed_after_commit_and_the_retry_does_not_transfer_twice()
    {
        TestWallet alice;
        TestWallet bob;
        await using (var api = await ProcessHost.StartApiAsync(_db.ConnectionString))
        {
            var client = ClientFor(api);
            alice = await client.CreateFundedWalletAsync(100_000);
            bob = await client.CreateFundedWalletAsync(0);

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.TransferAsync(
                alice.Token, alice.WalletId, bob.WalletId, 30_000, "crash-after-00001",
                headers: new Dictionary<string, string> { ["X-LedgerCore-Fail-At"] = "AfterCommit", ["X-LedgerCore-Fail-Mode"] = "KillProcess" }));

            Assert.True(await api.WaitForExitAsync(TimeSpan.FromSeconds(30)), "the API process should have died");
        }

        // The client never got a response, but the transfer committed.
        Assert.Equal(1, await _db.CountAsync("transfers"));

        await using (var restarted = await ProcessHost.StartApiAsync(_db.ConnectionString))
        {
            using var retry = await ClientFor(restarted).TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 30_000, "crash-after-00001");
            Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
            Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
            var replayed = await LedgerTestClient.ReadAsync<TransferResponse>(retry);
            Assert.Equal(1, await _db.ScalarAsync("SELECT count(*) FROM transfers WHERE id = @id", ("id", replayed.TransferId)));
        }

        Assert.Equal(1, await _db.CountAsync("transfers"));
        Assert.Equal(70_000, await _db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(30_000, await _db.WalletBalanceAsync(bob.WalletId));
        await _db.AssertLedgerInvariantsAsync();
    }

    private static LedgerTestClient ClientFor(ProcessHost host) =>
        TestTokens.ClientFor(new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(30) });
}
