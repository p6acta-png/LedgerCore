using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerCore.Api.Security;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Security;
using LedgerCore.Application.Transfers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LedgerCore.TestSupport;

/// <summary>Thin HTTP helpers so tests read like the scenario they describe.</summary>
public sealed class LedgerTestClient(HttpClient http, DevTokenIssuer tokens)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; } = http;

    public static LedgerTestClient For(LedgerApiFactory factory) =>
        new(factory.CreateClient(), factory.Services.GetRequiredService<DevTokenIssuer>());

    public string CustomerToken(Guid customerId, string name = "Test Customer") =>
        tokens.Issue(customerId.ToString(), name, [Roles.Customer], DateTimeOffset.UtcNow).Value.AccessToken;

    public string OperatorToken(string subject = "test-operator") =>
        tokens.Issue(subject, "Test Operator", [Roles.LedgerOperator], DateTimeOffset.UtcNow).Value.AccessToken;

    public async Task<WalletView> CreateWalletAsync(string customerToken, string name = "Main")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wallets") { Content = JsonContent.Create(new { name, currency = "NOK" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        using var response = await Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WalletView>(Json))!;
    }

    /// <summary>Creates a customer with one wallet funded through the real (balanced) funding endpoint.</summary>
    public async Task<TestWallet> CreateFundedWalletAsync(long openingBalanceMinor, string name = "Customer")
    {
        var customerId = Guid.NewGuid();
        var token = CustomerToken(customerId, name);
        var wallet = await CreateWalletAsync(token);
        if (openingBalanceMinor > 0)
        {
            using var response = await FundAsync(wallet.Id, openingBalanceMinor, $"fund-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return new TestWallet(customerId, wallet.Id, token);
    }

    public Task<HttpResponseMessage> FundAsync(Guid walletId, long amountMinor, string idempotencyKey) =>
        SendAsync(HttpMethod.Post, "/api/dev/funding", OperatorToken(), new { walletId, amountMinor, currency = "NOK" }, idempotencyKey);

    public Task<HttpResponseMessage> TransferAsync(
        string token, Guid from, Guid to, long amountMinor, string? idempotencyKey,
        string currency = "NOK", string? description = null, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Post, "/api/transfers", token,
            new CreateTransferCommand(from, to, amountMinor, currency, description), idempotencyKey, headers);

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? token, object? body = null, string? idempotencyKey = null, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        return await Http.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>Reads the stable machine-readable error code from a problem-details response.</summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

public sealed record TestWallet(Guid CustomerId, Guid WalletId, string Token);
