using System.Net;
using LedgerCore.Application.Transfers;
using LedgerCore.Infrastructure.Outbox;
using LedgerCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LedgerCore.IntegrationTests;

public sealed class IdempotencyTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    public async Task Repeating_a_request_with_the_same_key_returns_the_same_result_without_a_second_transfer()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        using var first = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "idem-repeat-0001");
        using var second = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "idem-repeat-0001");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, await Db.CountAsync("transfers"));
        Assert.Equal(9_000, await Db.WalletBalanceAsync(alice.WalletId));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    [Trait("Challenge", "04")]
    public async Task Reusing_a_key_with_a_different_payload_is_rejected()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var first = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "idem-reuse-0001");

        using var differentAmount = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 2_000, "idem-reuse-0001");
        using var differentDescription = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "idem-reuse-0001", description: "other");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, differentAmount.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await LedgerTestClient.ErrorCodeAsync(differentAmount));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await LedgerTestClient.ErrorCodeAsync(differentDescription));
        Assert.Equal(1, await Db.CountAsync("transfers"));
        Assert.Equal(9_000, await Db.WalletBalanceAsync(alice.WalletId));
    }

    [Fact]
    public async Task A_business_rejection_is_replayed_deterministically_even_after_the_balance_changes()
    {
        var alice = await Client.CreateFundedWalletAsync(500);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var rejected = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "idem-reject-0001");
        Assert.Equal("INSUFFICIENT_FUNDS", await LedgerTestClient.ErrorCodeAsync(rejected));

        using (var funding = await Client.FundAsync(alice.WalletId, 10_000, "idem-reject-funding"))
        {
            Assert.Equal(HttpStatusCode.Created, funding.StatusCode);
        }

        using var retry = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, "idem-reject-0001");

        // Same key + same payload = same logical result, even though it would now succeed.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal("INSUFFICIENT_FUNDS", await LedgerTestClient.ErrorCodeAsync(retry));
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(0, await Db.CountAsync("transfers"));
    }

    [Fact]
    public async Task Keys_are_scoped_per_caller()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(10_000);

        using var fromAlice = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, "shared-key-0001");
        using var fromBob = await Client.TransferAsync(bob.Token, bob.WalletId, alice.WalletId, 300, "shared-key-0001");

        Assert.Equal(HttpStatusCode.Created, fromAlice.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fromBob.StatusCode);
        Assert.Equal(2, await Db.CountAsync("transfers"));
    }

    [Theory]
    [InlineData(null, "IDEMPOTENCY_KEY_REQUIRED")]
    [InlineData("short", "IDEMPOTENCY_KEY_INVALID")]
    [InlineData("contains spaces in it", "IDEMPOTENCY_KEY_INVALID")]
    public async Task A_missing_or_malformed_key_is_rejected(string? key, string expectedCode)
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await LedgerTestClient.ErrorCodeAsync(response));
        Assert.Equal(0, await Db.CountAsync("transfers"));
    }

    [Fact]
    public async Task Idempotency_survives_a_restart_of_the_api_host()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var first = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 700, "idem-restart-0001");
        var original = await LedgerTestClient.ReadAsync<TransferResponse>(first);

        // A completely new host: new DI container, new connection pool, no in-memory state.
        await using var restarted = new LedgerApiFactory(Db.ConnectionString);
        var client = TestTokens.ClientFor(restarted);
        using var retry = await client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 700, "idem-restart-0001");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(original.TransferId, (await LedgerTestClient.ReadAsync<TransferResponse>(retry)).TransferId);
        Assert.Equal(1, await Db.CountAsync("transfers"));
    }

    [Fact]
    public async Task Expired_records_are_removed_by_the_retention_cleanup_and_the_key_becomes_reusable()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using (var first = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, "idem-retention-01"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        // Move the record past its retention window, then run the same cleanup the worker runs hourly.
        await Db.ExecuteAsync("UPDATE idempotency_records SET expires_at = now() - interval '1 minute' WHERE idempotency_key = 'idem-retention-01'");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var deleted = await scope.ServiceProvider.GetRequiredService<IdempotencyCleaner>().DeleteExpiredAsync(DateTimeOffset.UtcNow, 100, CancellationToken.None);
            Assert.Equal(1, deleted);
        }

        using var reused = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, "idem-retention-01");

        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
        Assert.False(reused.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(2, await Db.CountAsync("transfers"));
    }
}
