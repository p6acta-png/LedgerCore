using LedgerCore.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LedgerCore.TestSupport;

/// <summary>
/// Hosts the real API (real Program.cs, real middleware, real DI) in memory, pointed at a test database.
/// Two factories on the same database behave like two API instances: separate processes in every
/// respect that matters here (separate DI containers, connection pools and in-memory state).
/// </summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "integration-tests-signing-key-0123456789-abcdef";

    private readonly string _connectionString;
    private readonly Dictionary<string, string?> _overrides;
    private readonly Action<IServiceCollection>? _configureServices;

    public LedgerApiFactory(
        string connectionString,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        string environment = EnvironmentGuards.Testing)
    {
        _connectionString = connectionString;
        _overrides = settings is null ? [] : new Dictionary<string, string?>(settings);
        _configureServices = configureServices;
        Environment = environment;
    }

    public string Environment { get; }

    public static Dictionary<string, string?> TestSettings(string connectionString) => new()
    {
        ["ConnectionStrings:Ledger"] = connectionString,
        ["DevAuthentication:Enabled"] = "true",
        ["DevAuthentication:SigningKey"] = SigningKey,
        ["DevAuthentication:Issuer"] = "ledgercore-dev",
        ["DevAuthentication:Audience"] = "ledgercore-api",
        ["FailureInjection:Enabled"] = "true",
        ["Features:SimulatedFunding"] = "true",
        ["Database:ApplyMigrationsOnStartup"] = "false",
        ["Database:SeedDemoDataOnStartup"] = "false",
        ["RateLimiting:TransferPermitsPerSecond"] = "1000000",
        ["RateLimiting:TransferBurst"] = "1000000",
        ["RateLimiting:GeneralPermitsPerMinute"] = "1000000",
        ["RateLimiting:DevTokensPerMinute"] = "1000000",
        ["Ledger:LockTimeout"] = "00:00:15",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = TestSettings(_connectionString);
            foreach (var (key, value) in _overrides)
            {
                settings[key] = value;
            }

            configuration.AddInMemoryCollection(settings);
        });
        builder.ConfigureLogging(logging => logging.ClearProviders());
        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }
}
