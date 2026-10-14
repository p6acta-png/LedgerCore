using LedgerCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace LedgerCore.TestSupport;

/// <summary>
/// One real PostgreSQL server per test assembly.
///
///   * Default: a Testcontainers-managed postgres:17-alpine container (needs Docker).
///   * If LEDGERCORE_TEST_POSTGRES holds an admin connection string, that server is used instead
///     (for machines without Docker; see scripts/local-postgres.ps1). It is still real PostgreSQL:
///     persistence is never mocked.
///
/// Migrations run once into a template database; every test then gets its own database cloned with
/// CREATE DATABASE ... TEMPLATE, which is fast and gives perfect isolation between tests.
/// </summary>
public sealed class PostgresServer : IAsyncLifetime
{
    public const string ExternalServerVariable = "LEDGERCORE_TEST_POSTGRES";
    public const string Image = "postgres:17-alpine";

    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly List<string> _createdDatabases = [];
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;
    private string _templateDatabase = string.Empty;

    /// <summary>"testcontainers" or "external": reported so test logs show which server was used.</summary>
    public string Mode { get; private set; } = "uninitialized";

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            Mode = "external";
            _adminConnectionString = external;
        }
        else
        {
            Mode = "testcontainers";
            _container = new PostgreSqlBuilder(Image)
                .WithCommand("-c", "max_connections=300")
                .Build();
            await _container.StartAsync();
            _adminConnectionString = _container.GetConnectionString();
        }

        _templateDatabase = $"ledgercore_template_{Guid.NewGuid():N}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{_templateDatabase}\"");

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(ConnectionStringFor(_templateDatabase, pooling: false))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new LedgerDbContext(options))
        {
            await db.Database.MigrateAsync();
        }
    }

    /// <summary>Creates a fresh, fully migrated database for one test.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"ledgercore_test_{Guid.NewGuid():N}";

        // PostgreSQL refuses to clone a template that is being cloned concurrently, so serialize.
        await _createLock.WaitAsync();
        try
        {
            await ExecuteAdminAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{_templateDatabase}\"");
            _createdDatabases.Add(name);
        }
        finally
        {
            _createLock.Release();
        }

        return new TestDatabase(this, name, ConnectionStringFor(name, pooling: true));
    }

    internal async Task DropDatabaseAsync(string name)
    {
        await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        lock (_createdDatabases)
        {
            _createdDatabases.Remove(name);
        }
    }

    public string ConnectionStringFor(string database, bool pooling) =>
        new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = database,
            Pooling = pooling,
            MaxPoolSize = 120,
            IncludeErrorDetail = true,
        }.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        // External server: leave it as we found it.
        string[] leftovers;
        lock (_createdDatabases)
        {
            leftovers = [.. _createdDatabases];
        }

        foreach (var name in leftovers)
        {
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }

        await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{_templateDatabase}\" WITH (FORCE)");
        _createLock.Dispose();
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        // Identifiers above are generated GUID names, never user input; DDL cannot take parameters.
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_adminConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
