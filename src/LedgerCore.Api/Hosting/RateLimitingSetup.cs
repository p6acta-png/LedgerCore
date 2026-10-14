using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using LedgerCore.Api.Security;

namespace LedgerCore.Api.Hosting;

public sealed class RateLimitingSettings
{
    public const string SectionName = "RateLimiting";

    public int TransferPermitsPerSecond { get; set; } = 10;

    public int TransferBurst { get; set; } = 20;

    public int GeneralPermitsPerMinute { get; set; } = 600;

    public int DevTokensPerMinute { get; set; } = 20;
}

public static class RateLimitingSetup
{
    public const string TransfersPolicy = "transfers";
    public const string DevTokensPolicy = "dev-tokens";

    /// <summary>
    /// Per-caller limits (token subject, or client IP when anonymous). These limiters live in process
    /// memory, so with N API instances the effective limit is N times higher; a shared limiter (gateway or
    /// Redis) would be needed for a strict global limit. Rate limiting protects capacity; correctness never
    /// depends on it.
    /// </summary>
    public static void AddLedgerRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitingSettings>().BindConfiguration(RateLimitingSettings.SectionName);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await Http.HttpResults
                    .Problem(StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "Too many requests. Slow down and retry later.")
                    .ExecuteAsync(context.HttpContext);
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetFixedWindowLimiter(PartitionKey(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Settings(http).GeneralPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            options.AddPolicy(TransfersPolicy, http =>
                RateLimitPartition.GetTokenBucketLimiter(PartitionKey(http), _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Settings(http).TransferBurst,
                    TokensPerPeriod = Settings(http).TransferPermitsPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            options.AddPolicy(DevTokensPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Settings(http).DevTokensPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });
    }

    private static RateLimitingSettings Settings(HttpContext http) =>
        http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitingSettings>>().Value;

    private static string PartitionKey(HttpContext http) =>
        http.User.FindFirstValue(LedgerClaims.Subject) is { } subject
            ? "sub:" + subject
            : "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}
