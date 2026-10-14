using System.Security.Claims;
using System.Text;
using LedgerCore.Application.Security;
using LedgerCore.Domain.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LedgerCore.Api.Security;

public static class Policies
{
    public const string Customer = "customer";
    public const string Operator = "ledger-operator";
}

/// <summary>Claim types as they appear in the JWT (inbound claim mapping is disabled).</summary>
public static class LedgerClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
}

public sealed class JwtAuthenticationOptions
{
    public const string SectionName = "Authentication:Jwt";

    /// <summary>OpenID Connect issuer, e.g. https://login.example.com/realms/ledger. Required outside Development/Testing.</summary>
    public string? Authority { get; set; }

    public string Audience { get; set; } = "ledgercore-api";
}

public sealed class DevAuthenticationOptions
{
    public const string SectionName = "DevAuthentication";

    public bool Enabled { get; set; }

    public string Issuer { get; set; } = "ledgercore-dev";

    public string Audience { get; set; } = "ledgercore-api";

    /// <summary>HMAC key (at least 32 bytes). Dev-only; must come from configuration, never code.</summary>
    public string? SigningKey { get; set; }

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);
}

public static class AuthenticationSetup
{
    /// <summary>
    /// JWT bearer authentication in two mutually exclusive modes:
    ///   * Production: tokens from an external OpenID Connect provider (Authority + Audience).
    ///   * Development/Testing: tokens signed with a local HMAC key and minted by POST /api/dev/tokens.
    /// Which mode applies is decided from configuration when the options are first built; the startup
    /// validation in <see cref="StartupValidation"/> refuses to start with dev tokens outside
    /// Development/Testing, or with neither mode configured.
    /// </summary>
    public static void AddLedgerAuthentication(this IServiceCollection services)
    {
        services.AddOptions<DevAuthenticationOptions>().BindConfiguration(DevAuthenticationOptions.SectionName);
        services.AddOptions<JwtAuthenticationOptions>().BindConfiguration(JwtAuthenticationOptions.SectionName);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<DevAuthenticationOptions>, IOptions<JwtAuthenticationOptions>>((options, devOptions, jwtOptions) =>
            {
                options.MapInboundClaims = false;
                var dev = devOptions.Value;
                if (dev.Enabled)
                {
                    options.TokenValidationParameters = DevTokenIssuer.CreateValidationParameters(dev);
                    return;
                }

                var jwt = jwtOptions.Value;
                options.Authority = jwt.Authority;
                options.Audience = jwt.Audience;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters.NameClaimType = LedgerClaims.Name;
                options.TokenValidationParameters.RoleClaimType = LedgerClaims.Role;
            });

        services.AddSingleton<DevTokenIssuer>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Customer, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(Roles.Customer)
                .RequireAssertion(context => Guid.TryParse(context.User.FindFirstValue(LedgerClaims.Subject), out _)))
            .AddPolicy(Policies.Operator, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(Roles.LedgerOperator))
            // Secure by default: any endpoint without explicit metadata requires an authenticated caller.
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }
}

/// <summary>
/// Mints development tokens. The endpoint that uses it is only mapped when DevAuthentication is enabled,
/// and the issuer itself refuses to work otherwise.
/// </summary>
public sealed class DevTokenIssuer(IOptions<DevAuthenticationOptions> options)
{
    private static readonly string[] KnownRoles = [Roles.Customer, Roles.LedgerOperator];
    private readonly DevAuthenticationOptions _options = options.Value;
    private readonly Lazy<SymmetricSecurityKey> _key = new(() => CreateKey(options.Value));

    public static TokenValidationParameters CreateValidationParameters(DevAuthenticationOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = CreateKey(options),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = LedgerClaims.Name,
        RoleClaimType = LedgerClaims.Role,
    };

    public Result<DevToken> Issue(string? subject, string? name, IReadOnlyCollection<string>? roles, DateTimeOffset now)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Development tokens are disabled.");
        }

        var requestedRoles = roles is { Count: > 0 } ? roles.Distinct(StringComparer.Ordinal).ToArray() : [Roles.Customer];
        if (requestedRoles.Any(r => !KnownRoles.Contains(r, StringComparer.Ordinal)))
        {
            return Application.Common.ApplicationErrors.Validation($"roles may only contain: {string.Join(", ", KnownRoles)}.");
        }

        var resolvedSubject = string.IsNullOrWhiteSpace(subject) ? Guid.NewGuid().ToString() : subject.Trim();
        if (requestedRoles.Contains(Roles.Customer) && !Guid.TryParse(resolvedSubject, out _))
        {
            return Application.Common.ApplicationErrors.Validation("Customer subjects must be GUIDs.");
        }

        if (resolvedSubject.Length > 64)
        {
            return Application.Common.ApplicationErrors.Validation("subject must be at most 64 characters.");
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? "Demo user" : name.Trim();
        if (displayName.Length > 100)
        {
            return Application.Common.ApplicationErrors.Validation("name must be at most 100 characters.");
        }

        var expires = now + _options.TokenLifetime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [LedgerClaims.Subject] = resolvedSubject,
                [LedgerClaims.Name] = displayName,
                [LedgerClaims.Role] = requestedRoles,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(_key.Value, SecurityAlgorithms.HmacSha256),
        });
        return new DevToken(token, "Bearer", resolvedSubject, requestedRoles, expires);
    }

    private static SymmetricSecurityKey CreateKey(DevAuthenticationOptions options)
    {
        var bytes = Encoding.UTF8.GetBytes(options.SigningKey ?? string.Empty);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("DevAuthentication:SigningKey must be configured and at least 32 bytes long.");
        }

        return new SymmetricSecurityKey(bytes);
    }
}

public sealed record DevToken(string AccessToken, string TokenType, string Subject, IReadOnlyList<string> Roles, DateTimeOffset ExpiresAt);

public static class CallerContextExtensions
{
    private const string CorrelationItemKey = "LedgerCore.CorrelationId";

    /// <summary>Builds the application's view of the caller from the validated token.</summary>
    public static CallerContext GetCaller(this HttpContext context)
    {
        var subject = context.User.FindFirstValue(LedgerClaims.Subject)
            ?? throw new InvalidOperationException("Authenticated principal has no 'sub' claim.");
        var roles = context.User.FindAll(LedgerClaims.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        CustomerId? customerId = roles.Contains(Roles.Customer) && Guid.TryParse(subject, out var id) ? new CustomerId(id) : null;
        return new CallerContext(subject, customerId, roles, context.GetCorrelationId());
    }

    public static string? GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationItemKey, out var value) ? value as string : null;

    public static void SetCorrelationId(this HttpContext context, string correlationId) =>
        context.Items[CorrelationItemKey] = correlationId;
}
