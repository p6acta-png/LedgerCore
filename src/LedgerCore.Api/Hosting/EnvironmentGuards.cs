using LedgerCore.Api.Security;
using Npgsql;

namespace LedgerCore.Api.Hosting;

public static class EnvironmentGuards
{
    /// <summary>Environment name used by the automated tests.</summary>
    public const string Testing = "Testing";

    public static bool IsDevelopmentLike(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(Testing);
}

/// <summary>
/// Runs after the host is built and before any request is served. Misconfiguration stops the process
/// instead of degrading security: dangerous development features cannot be enabled by accident in a
/// real deployment, and the API never runs without an authentication mode.
/// </summary>
public static class StartupValidation
{
    public static void Validate(WebApplication app)
    {
        var configuration = app.Configuration;
        var environment = app.Environment;
        var errors = new List<string>();
        var developmentLike = EnvironmentGuards.IsDevelopmentLike(environment);

        void RequireDevelopmentLike(string setting)
        {
            if (configuration.GetValue<bool>(setting) && !developmentLike)
            {
                errors.Add($"{setting} is enabled in environment '{environment.EnvironmentName}'; it is restricted to Development and Testing.");
            }
        }

        RequireDevelopmentLike($"{DevAuthenticationOptions.SectionName}:Enabled");
        RequireDevelopmentLike($"{FailureInjectionOptions.SectionName}:Enabled");
        RequireDevelopmentLike("Features:SimulatedFunding");
        RequireDevelopmentLike("Database:SeedDemoDataOnStartup");

        var devAuthEnabled = configuration.GetValue<bool>($"{DevAuthenticationOptions.SectionName}:Enabled");
        if (!devAuthEnabled && string.IsNullOrWhiteSpace(configuration[$"{JwtAuthenticationOptions.SectionName}:Authority"]))
        {
            errors.Add("Authentication:Jwt:Authority is not configured. Configure an OpenID Connect provider, or use DevAuthentication in Development.");
        }

        if (devAuthEnabled && (configuration[$"{DevAuthenticationOptions.SectionName}:SigningKey"]?.Length ?? 0) < 32)
        {
            errors.Add("DevAuthentication:SigningKey must be at least 32 characters.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid LedgerCore configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        // Resolving the data source validates the connection string now rather than on the first request.
        _ = app.Services.GetRequiredService<NpgsqlDataSource>();
    }
}
