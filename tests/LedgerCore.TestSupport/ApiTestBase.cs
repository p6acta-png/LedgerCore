using LedgerCore.Api.Security;
using LedgerCore.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LedgerCore.TestSupport;

/// <summary>
/// Base class for API-level tests: each test (xUnit creates one class instance per test) gets its own
/// database cloned from the migrated template and its own in-memory API host.
/// </summary>
public abstract class ApiTestBase(PostgresServer server) : IAsyncLifetime
{
    protected PostgresServer Server { get; } = server;

    protected TestDatabase Db { get; private set; } = null!;

    protected LedgerApiFactory Factory { get; private set; } = null!;

    protected LedgerTestClient Client { get; private set; } = null!;

    public virtual async ValueTask InitializeAsync()
    {
        Db = await Server.CreateDatabaseAsync();
        Factory = CreateFactory(Db);
        Client = LedgerTestClient.For(Factory);
    }

    /// <summary>Override to customise configuration or replace services for one test class.</summary>
    protected virtual LedgerApiFactory CreateFactory(TestDatabase db) => new(db.ConnectionString);

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Mints tokens accepted by any API host configured with <see cref="LedgerApiFactory.SigningKey"/>.</summary>
public static class TestTokens
{
    private static readonly DevTokenIssuer Issuer = new(Options.Create(new DevAuthenticationOptions
    {
        Enabled = true,
        SigningKey = LedgerApiFactory.SigningKey,
        Issuer = "ledgercore-dev",
        Audience = "ledgercore-api",
        TokenLifetime = TimeSpan.FromHours(1),
    }));

    public static string Customer(Guid customerId, string name = "Customer") =>
        Issuer.Issue(customerId.ToString(), name, [Roles.Customer], DateTimeOffset.UtcNow).Value.AccessToken;

    public static string Operator(string subject = "test-operator") =>
        Issuer.Issue(subject, "Operator", [Roles.LedgerOperator], DateTimeOffset.UtcNow).Value.AccessToken;

    public static LedgerTestClient ClientFor(HttpClient http) => new(http, Issuer);

    public static LedgerTestClient ClientFor(LedgerApiFactory factory) =>
        new(factory.CreateClient(), factory.Services.GetRequiredService<DevTokenIssuer>());
}
