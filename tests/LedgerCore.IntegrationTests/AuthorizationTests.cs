using System.Net;
using System.Text;
using LedgerCore.Application.Transfers;
using LedgerCore.TestSupport;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LedgerCore.IntegrationTests;

public sealed class AuthorizationTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    [Trait("Challenge", "07")]
    public async Task A_customer_cannot_move_money_out_of_another_customers_wallet()
    {
        var alice = await Client.CreateFundedWalletAsync(100_000, "Alice");
        var mallory = await Client.CreateFundedWalletAsync(0, "Mallory");

        using var response = await Client.TransferAsync(mallory.Token, alice.WalletId, mallory.WalletId, 50_000, "authz-steal-0001");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await LedgerTestClient.ErrorCodeAsync(response));
        Assert.Equal(100_000, await Db.WalletBalanceAsync(alice.WalletId));
        Assert.Equal(0, await Db.WalletBalanceAsync(mallory.WalletId));
        Assert.Equal(0, await Db.CountAsync("transfers"));
        Assert.Equal(0, await Db.ScalarAsync("SELECT count(*) FROM idempotency_records WHERE idempotency_key = 'authz-steal-0001'"));
        Assert.Equal(1, await Db.ScalarAsync(
            "SELECT count(*) FROM audit_events WHERE action = 'transfer.create' AND outcome = 'Denied' AND actor_id = @actor",
            ("actor", mallory.CustomerId.ToString())));
    }

    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected_with_401()
    {
        var alice = await Client.CreateFundedWalletAsync(1_000);

        using var noToken = await Client.TransferAsync(null!, alice.WalletId, Guid.NewGuid(), 1, "authz-no-token-1");
        using var wrongKey = await Client.SendAsync(HttpMethod.Get, "/api/wallets", ForgedToken(alice.CustomerId, "attacker-key-0123456789-0123456789-abcd"));
        using var expired = await Client.SendAsync(HttpMethod.Get, "/api/wallets", ForgedToken(alice.CustomerId, LedgerApiFactory.SigningKey, expired: true));

        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal("UNAUTHENTICATED", await LedgerTestClient.ErrorCodeAsync(noToken));
    }

    [Fact]
    public async Task Other_customers_wallets_are_invisible()
    {
        var alice = await Client.CreateFundedWalletAsync(1_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        using var details = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}", bob.Token);
        using var balance = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}/balance", bob.Token);
        using var history = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}/transactions", bob.Token);

        // 404 rather than 403, so wallet ids cannot be probed.
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, balance.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
    }

    [Fact]
    public async Task Operators_can_inspect_but_cannot_move_customer_money()
    {
        var alice = await Client.CreateFundedWalletAsync(1_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var operatorToken = Client.OperatorToken();

        using var read = await Client.SendAsync(HttpMethod.Get, $"/api/wallets/{alice.WalletId}", operatorToken);
        using var transfer = await Client.TransferAsync(operatorToken, alice.WalletId, bob.WalletId, 100, "authz-operator-01");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, transfer.StatusCode);
        Assert.Equal(1_000, await Db.WalletBalanceAsync(alice.WalletId));
    }

    [Theory]
    [InlineData("POST", "/api/admin/reconciliation")]
    [InlineData("GET", "/api/journal-entries")]
    [InlineData("POST", "/api/dev/funding")]
    public async Task Customers_cannot_use_operator_endpoints(string method, string path)
    {
        var alice = await Client.CreateFundedWalletAsync(0);

        using var response = await Client.SendAsync(new HttpMethod(method), path, alice.Token,
            body: method == "POST" ? new { walletId = alice.WalletId, amountMinor = 1_000_000, currency = "NOK" } : null,
            idempotencyKey: "authz-escalation-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await Db.WalletBalanceAsync(alice.WalletId));
    }

    [Fact]
    public async Task A_customer_cannot_reverse_a_transfer()
    {
        var alice = await Client.CreateFundedWalletAsync(1_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var transfer = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 500, "authz-reverse-01");
        var created = await LedgerTestClient.ReadAsync<TransferResponse>(transfer);

        using var reversal = await Client.SendAsync(HttpMethod.Post, $"/api/transfers/{created.TransferId}/reversals", alice.Token, new { reason = "please" }, "authz-reverse-02");

        Assert.Equal(HttpStatusCode.Forbidden, reversal.StatusCode);
        Assert.Equal(0, await Db.CountAsync("transfer_reversals"));
    }

    [Fact]
    public async Task Only_participants_and_operators_can_see_a_transfer()
    {
        var alice = await Client.CreateFundedWalletAsync(1_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        var eve = await Client.CreateFundedWalletAsync(0);
        using var transfer = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 500, "authz-visibility");
        var id = (await LedgerTestClient.ReadAsync<TransferResponse>(transfer)).TransferId;

        using var asSender = await Client.SendAsync(HttpMethod.Get, $"/api/transfers/{id}", alice.Token);
        using var asReceiver = await Client.SendAsync(HttpMethod.Get, $"/api/transfers/{id}", bob.Token);
        using var asOutsider = await Client.SendAsync(HttpMethod.Get, $"/api/transfers/{id}", eve.Token);
        using var asOperator = await Client.SendAsync(HttpMethod.Get, $"/api/transfers/{id}", Client.OperatorToken());

        Assert.Equal(HttpStatusCode.OK, asSender.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asReceiver.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asOperator.StatusCode);
    }

    private static string ForgedToken(Guid subject, string key, bool expired = false)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ledgercore-dev",
            Audience = "ledgercore-api",
            IssuedAt = expired ? now.AddHours(-2) : now,
            NotBefore = expired ? now.AddHours(-2) : now,
            Expires = expired ? now.AddHours(-1) : now.AddHours(1),
            Claims = new Dictionary<string, object> { ["sub"] = subject.ToString(), ["role"] = new[] { "customer" } },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });
    }
}
