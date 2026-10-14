using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using LedgerCore.TestSupport;
using Xunit;

namespace LedgerCore.IntegrationTests;

public sealed class ObservabilityTests(PostgresServer server) : ApiTestBase(server)
{
    [Fact]
    public async Task A_client_correlation_id_is_echoed_and_stored_with_the_journal_entry_and_audit_event()
    {
        var alice = await Client.CreateFundedWalletAsync(10_000);
        var bob = await Client.CreateFundedWalletAsync(0);

        using var response = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, "obs-correlation-1",
            headers: new Dictionary<string, string> { ["X-Correlation-Id"] = "checkout-42.retry-1" });

        Assert.Equal("checkout-42.retry-1", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM journal_entries WHERE correlation_id = 'checkout-42.retry-1'"));
        Assert.Equal(1, await Db.ScalarAsync("SELECT count(*) FROM audit_events WHERE correlation_id = 'checkout-42.retry-1' AND action = 'transfer.create'"));
    }

    [Fact]
    public async Task An_unsafe_correlation_id_is_replaced_not_reflected()
    {
        using var response = await Client.SendAsync(HttpMethod.Get, "/health/live", null,
            headers: new Dictionary<string, string> { ["X-Correlation-Id"] = "<script>alert(1)</script>" });

        var echoed = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.DoesNotContain("<", echoed, StringComparison.Ordinal);
        Assert.Equal(32, echoed.Length);
    }

    [Fact]
    public async Task Liveness_and_readiness_report_healthy_with_a_migrated_database()
    {
        using var live = await Client.Http.GetAsync("/health/live");
        using var ready = await Client.Http.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Development_hosts_serve_swagger_ui_and_the_openapi_document_anonymously()
    {
        // Swagger UI is middleware, not an endpoint: it must run before the secure-by-default fallback policy.
        using var swagger = await Client.Http.GetAsync("/swagger/index.html");
        using var openApi = await Client.Http.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        Assert.Equal("text/html", swagger.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_when_the_database_is_unreachable_while_liveness_stays_up()
    {
        var unreachable = new Npgsql.NpgsqlConnectionStringBuilder(Db.ConnectionString) { Database = "ledgercore_does_not_exist", Timeout = 2 }.ConnectionString;
        await using var factory = new LedgerApiFactory(unreachable);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Ledger_commands_emit_outcome_metrics()
    {
        var outcomes = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "LedgerCore" && instrument.Name == "ledgercore.commands")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcomes.Add((string)tag.Value!);
                }
            }
        });
        listener.Start();

        var alice = await Client.CreateFundedWalletAsync(1_000);
        var bob = await Client.CreateFundedWalletAsync(0);
        using var ok = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, "obs-metrics-0001");
        using var replay = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100, "obs-metrics-0001");
        using var rejected = await Client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 100_000, "obs-metrics-0002");

        Assert.Contains("completed", outcomes);
        Assert.Contains("replayed", outcomes);
        Assert.Contains("rejected:INSUFFICIENT_FUNDS", outcomes);
    }
}

public sealed class SecurityConfigurationTests(PostgresServer server) : ApiTestBase(server)
{
    private Dictionary<string, string?> ProductionSettings(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DevAuthentication:Enabled"] = "false",
            ["FailureInjection:Enabled"] = "false",
            ["Features:SimulatedFunding"] = "false",
            ["Authentication:Jwt:Authority"] = "https://login.example.invalid/realms/ledger",
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return settings;
    }

    [Theory]
    [InlineData("DevAuthentication:Enabled", "true")]
    [InlineData("FailureInjection:Enabled", "true")]
    [InlineData("Features:SimulatedFunding", "true")]
    [InlineData("Authentication:Jwt:Authority", "")]
    public async Task Production_refuses_to_start_with_unsafe_configuration(string key, string value)
    {
        var settings = ProductionSettings((key, value));
        if (key == "DevAuthentication:Enabled")
        {
            settings["DevAuthentication:SigningKey"] = LedgerApiFactory.SigningKey;
        }

        await using var factory = new LedgerApiFactory(Db.ConnectionString, settings, environment: "Production");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Invalid LedgerCore configuration", Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_correctly_configured_production_host_exposes_no_development_endpoints()
    {
        await using var factory = new LedgerApiFactory(Db.ConnectionString, ProductionSettings(), environment: "Production");
        using var client = factory.CreateClient();

        using var tokens = await client.PostAsync("/api/dev/tokens", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        using var swagger = await client.GetAsync("/swagger/index.html");
        using var openApi = await client.GetAsync("/openapi/v1.json");
        using var wallets = await client.GetAsync("/api/wallets");

        // Unknown routes still pass the secure-by-default fallback policy, so anonymous callers see 401.
        Assert.Contains(tokens.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });
        Assert.Contains(swagger.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });
        Assert.Contains(openApi.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });
        Assert.Equal(HttpStatusCode.Unauthorized, wallets.StatusCode);
    }

    [Fact]
    public async Task Transfer_bursts_above_the_rate_limit_get_429()
    {
        await using var factory = new LedgerApiFactory(Db.ConnectionString, new Dictionary<string, string?>
        {
            ["RateLimiting:TransferBurst"] = "2",
            ["RateLimiting:TransferPermitsPerSecond"] = "1",
        });
        var client = TestTokens.ClientFor(factory);
        var alice = await client.CreateFundedWalletAsync(10_000);
        var bob = await client.CreateFundedWalletAsync(0);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.TransferAsync(alice.Token, alice.WalletId, bob.WalletId, 1, $"rate-limit-{i:D6}");
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.Equal("RATE_LIMITED", await LedgerTestClient.ErrorCodeAsync(response));
            }
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.Created, statuses[0]);
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        if (error is AggregateException aggregate)
        {
            messages.AddRange(aggregate.Flatten().InnerExceptions.Select(e => e.Message));
        }

        return string.Join(" | ", messages);
    }
}
