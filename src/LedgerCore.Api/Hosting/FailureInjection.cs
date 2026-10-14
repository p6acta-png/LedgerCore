using LedgerCore.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace LedgerCore.Api.Hosting;

public sealed class FailureInjectionOptions
{
    public const string SectionName = "FailureInjection";

    public bool Enabled { get; set; }
}

/// <summary>
/// Lets a test or a reviewer trigger a failure at a named checkpoint of one specific request:
///
///   X-LedgerCore-Fail-At:   AfterIdempotencyClaim | AfterAccountsLocked | AfterFirstPosting | BeforeCommit | AfterCommit
///   X-LedgerCore-Fail-Mode: ThrowException (default) | TerminateDatabaseConnection | KillProcess
///
/// Inert unless FailureInjection:Enabled is true, which StartupValidation refuses outside
/// Development/Testing. Fires at most once per request, so with TerminateDatabaseConnection the runner's
/// automatic retry can succeed and demonstrate that the failed attempt left nothing behind.
/// </summary>
public sealed class HttpHeaderFailureInjector(IHttpContextAccessor accessor, IOptions<FailureInjectionOptions> options) : IFailureInjector
{
    public const string PointHeader = "X-LedgerCore-Fail-At";
    public const string ModeHeader = "X-LedgerCore-Fail-Mode";
    private const string FiredItemKey = "LedgerCore.FailureInjected";

    public ValueTask<FailureMode?> OnCheckpointAsync(FailurePoint point, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return ValueTask.FromResult<FailureMode?>(null);
        }

        var context = accessor.HttpContext;
        if (context is null
            || context.Items.ContainsKey(FiredItemKey)
            || !Enum.TryParse<FailurePoint>(context.Request.Headers[PointHeader].ToString(), ignoreCase: true, out var requested)
            || requested != point)
        {
            return ValueTask.FromResult<FailureMode?>(null);
        }

        context.Items[FiredItemKey] = true;
        var mode = Enum.TryParse<FailureMode>(context.Request.Headers[ModeHeader].ToString(), ignoreCase: true, out var parsed)
            ? parsed
            : FailureMode.ThrowException;
        return ValueTask.FromResult<FailureMode?>(mode);
    }
}
