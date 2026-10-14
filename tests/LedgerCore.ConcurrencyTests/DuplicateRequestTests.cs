using System.Net;
using LedgerCore.Application.Transfers;
using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.ConcurrencyTests;

public sealed class DuplicateRequestTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    [Trait("Challenge", "03")]
    public async Task Mandatory_100_simultaneous_identical_requests_record_exactly_one_transfer()
    {
        var a = await Client.CreateFundedWalletAsync(100_000);
        var b = await Client.CreateFundedWalletAsync(0);

        var responses = await StartGate.RunConcurrentlyAsync(100, _ =>
            Client.TransferAsync(a.Token, a.WalletId, b.WalletId, 1_000, "same-key-x100-0001"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => LedgerTestClient.ReadAsync<TransferResponse>(r)));
        Assert.Single(bodies.Select(t => t.TransferId).Distinct());
        Assert.Equal(99, responses.Count(r => r.Headers.Contains("Idempotent-Replayed")));

        Assert.Equal(1, await Db.CountAsync("transfers"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM journal_entries WHERE entry_type = 'Transfer'"));
        Assert.Equal(2, await Db.ScalarAsync("SELECT count(*) FROM postings p JOIN journal_entries e ON e.id = p.journal_entry_id WHERE e.entry_type = 'Transfer'"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM idempotency_records WHERE idempotency_key = 'same-key-x100-0001'"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE message_type = 'transfer.completed'"));
        Assert.Equal(99_000, await Db.WalletBalanceAsync(a.WalletId));
        Assert.Equal(1_000, await Db.WalletBalanceAsync(b.WalletId));
        await Db.AssertLedgerInvariantsAsync();
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    [Trait("Challenge", "04")]
    public async Task Concurrent_requests_sharing_a_key_with_different_payloads_produce_one_transfer()
    {
        var a = await Client.CreateFundedWalletAsync(100_000);
        var b = await Client.CreateFundedWalletAsync(0);

        // Half the requests ask for 1 000, the other half for 2 000, all with the same key.
        var responses = await StartGate.RunConcurrentlyAsync(40, i =>
            Client.TransferAsync(a.Token, a.WalletId, b.WalletId, i % 2 == 0 ? 1_000 : 2_000, "same-key-mixed-001"));

        var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToArray();
        var reused = responses.Where(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ToArray();
        Assert.Equal(40, created.Length + reused.Length);
        Assert.Equal(20, created.Length);
        Assert.All(await Task.WhenAll(reused.Select(LedgerTestClient.ErrorCodeAsync)), code => Assert.Equal("IDEMPOTENCY_KEY_REUSED", code));

        var winners = await Task.WhenAll(created.Select(r => LedgerTestClient.ReadAsync<TransferResponse>(r)));
        var winner = Assert.Single(winners.Select(w => (w.TransferId, w.AmountMinor)).Distinct());
        Assert.Equal(1, await Db.CountAsync("transfers"));
        Assert.Equal(100_000 - winner.AmountMinor, await Db.WalletBalanceAsync(a.WalletId));
        await Db.AssertLedgerInvariantsAsync();
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
