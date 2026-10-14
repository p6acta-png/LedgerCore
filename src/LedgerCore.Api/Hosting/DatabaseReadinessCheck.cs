using LedgerCore.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace LedgerCore.Api.Hosting;

/// <summary>
/// Readiness = the database answers AND the schema is fully migrated. An instance running against an
/// old schema could violate invariants that newer migrations enforce, so it must not receive traffic.
/// </summary>
public sealed class DatabaseReadinessCheck(NpgsqlDataSource dataSource, DatabaseMigrator migrator) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using (var ping = dataSource.CreateCommand("SELECT 1"))
            {
                await ping.ExecuteScalarAsync(cancellationToken);
            }

            var pending = await migrator.GetPendingMigrationsAsync(cancellationToken);
            return pending.Count == 0
                ? HealthCheckResult.Healthy("Database reachable and schema up to date.")
                : HealthCheckResult.Unhealthy($"{pending.Count} database migration(s) pending.");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            // The exception message is not returned to callers; only its type is.
            return HealthCheckResult.Unhealthy($"Database unavailable ({ex.GetType().Name}).");
        }
    }
}
