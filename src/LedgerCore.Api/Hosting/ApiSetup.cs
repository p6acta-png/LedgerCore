using System.Text.Json.Serialization;
using LedgerCore.Api.Endpoints;
using LedgerCore.Api.Http;
using LedgerCore.Api.Security;
using LedgerCore.Application;
using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Wallets;
using LedgerCore.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LedgerCore.Api.Hosting;

public static class ApiSetup
{
    /// <summary>
    /// Service registration. Configuration is consumed lazily through the options pattern, so every
    /// configuration source (including test overrides) is visible; validation happens in
    /// <see cref="StartupValidation"/> once the host is built.
    /// </summary>
    public static void AddLedgerCoreApi(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        // Structured JSON logs; scopes carry the correlation id into every line of a request.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });

        services.AddLedgerCoreApplication();
        services.AddLedgerCoreInfrastructure();
        services.AddOptions<IdempotencyOptions>().BindConfiguration(IdempotencyOptions.SectionName);
        services.AddOptions<WalletOptions>().BindConfiguration(WalletOptions.SectionName);
        services.AddOptions<FailureInjectionOptions>().BindConfiguration(FailureInjectionOptions.SectionName);
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Scoped<IFailureInjector, HttpHeaderFailureInjector>());
        services.AddScoped<DemoDataSeeder>();

        // Strict JSON: no numbers-as-strings, no unknown fields, required fields really required.
        services.Configure<JsonOptions>(options =>
        {
            var json = options.SerializerOptions;
            json.NumberHandling = JsonNumberHandling.Strict;
            json.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            json.RespectRequiredConstructorParameters = true;
            json.RespectNullableAnnotations = true;
            json.Converters.Add(new JsonStringEnumConverter());
        });
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            if (!problem.Extensions.ContainsKey("code"))
            {
                problem.Extensions["code"] = problem.Status switch
                {
                    StatusCodes.Status401Unauthorized => "UNAUTHENTICATED",
                    StatusCodes.Status403Forbidden => "FORBIDDEN",
                    StatusCodes.Status404NotFound => "NOT_FOUND",
                    StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
                    StatusCodes.Status415UnsupportedMediaType => "UNSUPPORTED_MEDIA_TYPE",
                    _ => "ERROR",
                };
            }

            problem.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();
            problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
        });
        services.AddExceptionHandler<LedgerExceptionHandler>();

        services.AddLedgerAuthentication();
        services.AddLedgerRateLimiting();
        services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
        services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);

        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("ledgercore-api"))
            .WithTracing(tracing => tracing
                .AddSource(LedgerTelemetry.SourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddMeter(LedgerTelemetry.SourceName)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql"));

        // Export only when an OTLP collector is configured (e.g. the Aspire dashboard in docker-compose).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }
    }

    public static void UseLedgerCoreApi(this WebApplication app)
    {
        StartupValidation.Validate(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        if (EnvironmentGuards.IsDevelopmentLike(app.Environment) || app.Configuration.GetValue<bool>("OpenApi:Enabled"))
        {
            app.MapOpenApi().AllowAnonymous();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/openapi/v1.json", "LedgerCore v1");
                options.RoutePrefix = "swagger";
            });
        }

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
            .AllowAnonymous().DisableRateLimiting();

        app.MapLedgerEndpoints();
    }

    /// <summary>Handles `migrate` and `seed` commands, or the optional startup tasks before serving traffic.</summary>
    public static async Task<int?> RunCommandOrStartupTasksAsync(this WebApplication app, string? command)
    {
        var configuration = app.Configuration;
        var migrate = command is "migrate" or "seed" || configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup");
        var seed = command is "seed" || (command is null && configuration.GetValue<bool>("Database:SeedDemoDataOnStartup"));

        if (seed && !EnvironmentGuards.IsDevelopmentLike(app.Environment))
        {
            app.Logger.LogError("Demo data seeding is only available in Development/Testing");
            return 1;
        }

        await using var scope = app.Services.CreateAsyncScope();
        if (migrate)
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(app.Lifetime.ApplicationStopping);
        }

        if (seed)
        {
            await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
        }

        return command is null ? null : 0;
    }
}
