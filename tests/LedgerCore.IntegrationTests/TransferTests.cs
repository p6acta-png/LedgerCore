using System.Net;
using System.Net.Http.Json;
using System.Text;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Transfers;
using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.IntegrationTests;

public sealed class TransferTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    public async Task A_transfer_moves_money_and_writes_one_balanced_entry()
    {
        var alice = await Client.CreateFundedWalletAsync(100_000, "Alice");
        var bob = await Client.CreateFundedWalletAsync(0, "Bob");

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 80_000, "t-happy-path-1", description: "rent");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await LedgerTestClient.ReadAsync<TransferResponse>(response);
        Assert.Equal("Completed", transfer.Status);
        Assert.Equal($"/api/transfers/{transfer.TransferId}", response.Headers.Location?.ToString());
        Assert.Equal(20_000, await Db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(80_000, await Db.WalletBalanceAsync(bob.WalletId));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM transfers WHERE id = @id", ("id", transfer.TransferId)));
        Assert.Equal(2, await Db.ScalarAsync("SELECT count(*) FROM postings WHERE journal_entry_id = @id", ("id", transfer.JournalEntryId)));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM outbox_messages WHERE message_type = 'transfer.completed'"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM audit_events WHERE action = 'transfer.create' AND outcome = 'Succeeded'"));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    [Trait("Challenge", "01")]
    public async Task Overdrawing_a_wallet_is_rejected_and_changes_nothing()
    {
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var entriesBefore = await Db.CountAsync("journal_entries");

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100_001, "t-overdraw-01");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("INSUFFICIENT_FUNDS", await LedgerTestClient.ErrorCodeAsync(response));
        Assert.Equal(100_000, await Db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(entriesBefore, await Db.CountAsync("journal_entries"));
        Assert.Equal(0, await Db.CountAsync("transfers"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM audit_events WHERE action = 'transfer.create' AND outcome = 'Rejected'"));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Fact]
    public async Task Spending_the_exact_balance_leaves_zero()
    {
        var alice = await Client.CreateFundedWalletAsync(55_555);
        var bob = await Client.CreateFundedWalletAsync(0);

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 55_555, "t-exact-balance");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await Db.WalletBalanceAsync(alice.WalletId));
        await Db.AssertLedgerInvariantsAsync();
    }

    [Theory]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":0,"currency":"NOK"}""", "INVALID_AMOUNT")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":-500,"currency":"NOK"}""", "INVALID_AMOUNT")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":100000000001,"currency":"NOK"}""", "INVALID_AMOUNT")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":100,"currency":"EUR"}""", "UNSUPPORTED_CURRENCY")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{from}","amountMinor":100,"currency":"NOK"}""", "SELF_TRANSFER")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":"100","currency":"NOK"}""", "VALIDATION_FAILED")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":1.5,"currency":"NOK"}""", "VALIDATION_FAILED")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":99999999999999999999,"currency":"NOK"}""", "VALIDATION_FAILED")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amount":100,"currency":"NOK"}""", "VALIDATION_FAILED")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":100}""", "VALIDATION_FAILED")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":100,"currency":null}""", "VALIDATION_FAILED")]
    [InlineData("""{"fromWalletId":"{from}",""", "VALIDATION_FAILED")]
    [InlineData("""{"fromWalletId":"{from}","toWalletId":"{to}","amountMinor":100,"currency":"NOK","description":"line\nbreak"}""", "VALIDATION_FAILED")]
    public async Task Malformed_or_invalid_requests_are_rejected_with_400(string bodyTemplate, string expectedCode)
    {
        var alice = await Client.CreateFundedWalletAsync(100_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var body = bodyTemplate.Replace("{from}", alice.WalletId.ToString(), StringComparison.Ordinal)
            .Replace("{to}", bob.WalletId.ToString(), StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", alice.Token);
        request.Headers.Add("Idempotency-Key", $"t-invalid-{Guid.NewGuid():N}");
        using var response = await Client.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await LedgerTestClient.ErrorCodeAsync(response));
        Assert.Equal(100_000, await Db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(0, await Db.CountAsync("transfers"));
    }

    [Fact]
    public async Task Unknown_destination_wallet_returns_404()
    {
        var alice = await Client.CreateFundedWalletAsync(1_000);

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, Guid.NewGuid(), 100, "t-unknown-destination");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("WALLET_NOT_FOUND", await LedgerTestClient.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Balance_overflow_on_the_receiver_is_rejected_not_wrapped()
    {
        var alice = await Client.CreateFundedWalletAsync(1_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        // Out-of-band edit to put the receiver next to long.MaxValue (this desynchronizes the cache on
        // purpose; the point of this test is only the overflow guard).
        await Db.ExecuteAsync(
            "UPDATE ledger_accounts SET balance_minor = 9223372036854775800 WHERE id = (SELECT ledger_account_id FROM wallets WHERE id = @id)",
            ("id", bob.WalletId));

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, "t-overflow-001");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("BALANCE_OVERFLOW", await LedgerTestClient.ErrorCodeAsync(response));
        Assert.Equal(1_000, await Db.WalletBalanceAsync(alice.WalletId));
    }

    [Fact]
    public async Task Both_parties_see_the_transfer_in_their_history()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var transferResponse = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 2_500, "t-history-01", description: "coffee");
        var transfer = await LedgerTestClient.ReadAsync<TransferResponse>(transferResponse);

        using var history = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{bob.WalletId}/transactions", bob.Token);
        var rows = await LedgerTestClient.ReadAsync<List<WalletTransactionView>>(history);

        var credit = Assert.Single(rows);
        Assert.Equal("Credit", credit.Direction);
        Assert.Equal(2_500, credit.SignedAmountMinor);
        Assert.Equal(transfer.TransferId, credit.TransferId);
        Assert.Equal("coffee", credit.Description);

        using var aliceHistory = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}/transactions?limit=1", alice.Token);
        var latest = Assert.Single(await LedgerTestClient.ReadAsync<List<WalletTransactionView>>(aliceHistory));
        Assert.Equal(-2_500, latest.SignedAmountMinor);
    }

    [Fact]
    public async Task Balance_endpoint_reports_minor_units_and_a_formatted_value()
    {
        var alice = await Client.CreateFundedWalletAsync(123_456);

        using var response = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}/balance", alice.Token);
        var balance = await response.Content.ReadFromJsonAsync<Api.Endpoints.WalletBalanceResponse>(LedgerTestClient.Json);

        Assert.Equal(123_456, balance!.AvailableBalanceMinor);
        Assert.Equal("1234.56 NOK", balance.AvailableBalanceFormatted);
    }
}
