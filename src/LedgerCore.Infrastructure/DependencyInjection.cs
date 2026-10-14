using LedgerCore.Application.Abstractions;
using LedgerCore.Infrastructure.Ledger;
using LedgerCore.Infrastructure.Outbox;
using LedgerCore.Infrastructure.Persistence;
using LedgerCore.Infrastructure.Queries;
using LedgerCore.Infrastructure.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LedgerCore.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Ledger";

    /// <summary>
    /// Registers the PostgreSQL adapters. Configuration is read lazily from the container's IConfiguration,
    /// so hosts and tests can add configuration sources at any point before the first resolution.
    /// </summary>
    public static IServiceCollection AddLedgerCoreInfrastructure(this IServiceCollection services)
    {
        // One NpgsqlDataSource (one connection pool) shared by EF Core and the hand-written SQL.
        // Parameter values are never logged, so amounts and keys do not leak into logs.
        services.AddSingleton(provider =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ConnectionStringName} is not configured. Provide it via environment variable " +
                    $"ConnectionStrings__{ConnectionStringName}, user secrets, or appsettings.Development.json.");
            }

            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());
            builder.EnableParameterLogging(false);
            return builder.Build();
        });
        services.AddDbContext<LedgerDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>())
                   .UseSnakeCaseNamingConvention());

        services.AddOptions<LedgerOptions>().BindConfiguration(LedgerOptions.SectionName);
        services.AddOptions<OutboxOptions>().BindConfiguration(OutboxOptions.SectionName);
        services.AddOptions<SimulatedSinkOptions>().BindConfiguration(SimulatedSinkOptions.SectionName);

        services.AddScoped<ILedgerTransactionRunner, NpgsqlLedgerTransactionRunner>();
        services.AddScoped<ILedgerReadStore, LedgerReadStore>();
        services.AddScoped<IWalletRegistry, WalletRegistry>();
        services.AddScoped<IAuditLog, AuditLog>();

        services.AddSingleton<INotificationSink, DatabaseNotificationSink>();
        services.AddScoped<OutboxDispatcher>();
        services.AddScoped<IdempotencyCleaner>();
        services.AddScoped<DatabaseMigrator>();
        return services;
    }
}

/// <summary>Applies EF Core migrations. Run once per deployment (the `migrate` command), not by every instance.</summary>
public sealed class DatabaseMigrator(LedgerDbContext db, ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        logger.LogInformation("Applying {Count} pending migrations: {Migrations}", pending.Count, string.Join(", ", pending));

        // EF Core 9+ takes a database-level migration lock, so concurrent migrators queue instead of racing.
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken) =>
        (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
}
