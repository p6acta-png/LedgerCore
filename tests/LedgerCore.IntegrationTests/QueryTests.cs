using System.Net;
using LedgerCore.Application.Abstractions;
using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.IntegrationTests;

/// <summary>Read side: pagination cursors, wallet listing and the per-customer wallet limit.</summary>
public sealed class QueryTests(PostgresServer server) : ApiTestBase(server)
{
    protected override LedgerApiFactory CreateFactory(TestDatabase db) =>
        new(db.ConnectionString, new Dictionary<string, string?> { ["Wallets:MaxWalletsPerCustomer"] = "2" });

    [Fact]
    public async Task Journal_entries_are_paged_newest_first_with_a_stable_cursor()
    {
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        for (var i = 0; i < 3; i++)
        {
            using var transfer = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100 + i, $"query-journal-{i:D4}");
            Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        }

        var operatorToken = Client.OperatorToken();
        using var firstPage = await Client.SendAsync(HttpMethod.Get, "/api/journal-entries?limit=2", operatorToken);
        var first = await LedgerTestClient.ReadAsync<List<JournalEntryView>>(firstPage);
        using var secondPage = await Client.SendAsync(HttpMethod.Get, $"/api/journal-entries?limit=10&beforeEntryId={first[^1].Id}", operatorToken);
        var second = await LedgerTestClient.ReadAsync<List<JournalEntryView>>(secondPage);

        // 1 funding entry + 3 transfers = 4 entries; no overlap, no gaps, newest first.
        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        var all = first.Concat(second).ToList();
        Assert.Equal(4, all.Select(e => e.Id).Distinct().Count());
        Assert.Equal(all.OrderByDescending(e => e.CreatedAt).Select(e => e.Id), all.Select(e => e.Id));
        Assert.All(all, e => Assert.Equal(e.Postings.Where(p => p.Direction == "Debit").Sum(p => p.AmountMinor),
                                          e.Postings.Where(p => p.Direction == "Credit").Sum(p => p.AmountMinor)));
    }

    [Fact]
    public async Task Wallet_history_pages_by_posting_id()
    {
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        for (var i = 0; i < 4; i++)
        {
            using var transfer = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1_000, $"query-history-{i:D4}");
        }

        using var page1 = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}/transactions?limit=3", alice.Token);
        var first = await LedgerTestClient.ReadAsync<List<WalletTransactionView>>(page1);
        using var page2 = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}/transactions?limit=3&beforePostingId={first[^1].PostingId}", alice.Token);
        var second = await LedgerTestClient.ReadAsync<List<WalletTransactionView>>(page2);

        Assert.Equal(3, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(100_000 - 4_000, first.Concat(second).Sum(t => t.SignedAmountMinor));
        Assert.True(first.Concat(second).Select(t => t.PostingId).SequenceEqual(first.Concat(second).Select(t => t.PostingId).OrderDescending()));
    }

    [Fact]
    public async Task Invalid_page_sizes_are_rejected()
    {
        var alice = await Client.CreateFundedWalletAsync(0);

        using var response = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}/transactions?limit=0", alice.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_customer_lists_only_their_own_wallets_and_the_wallet_limit_is_enforced()
    {
        var customer = Guid.NewGuid();
        var token = Client.CustomerToken(customer);
        await Client.CreateWalletAsync(token, "One");
        await Client.CreateWalletAsync(token, "Two");
        await Client.CreateFundedWalletAsync(0, "Someone else");

        using var third = await Client.SendAsync(HttpMethod.Post, "/api/wallets", token, new { name = "Three", currency = "NOK" });
        using var mine = await Client.SendAsync(HttpMethod.Get, "/api/wallets", token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal("WALLET_LIMIT_REACHED", await LedgerTestClient.ErrorCodeAsync(third));
        var wallets = await LedgerTestClient.ReadAsync<List<WalletView>>(mine);
        Assert.Equal(["One", "Two"], wallets.Select(w => w.Name).ToArray());
        Assert.All(wallets, w => Assert.Equal(customer, w.CustomerId));
    }
}
