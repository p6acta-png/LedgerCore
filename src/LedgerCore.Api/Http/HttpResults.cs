using System.Diagnostics;
using LedgerCore.Application.Idempotency;
using LedgerCore.Domain.Common;

namespace LedgerCore.Api.Http;

/// <summary>Translates application results into HTTP responses with RFC 9457 problem details.</summary>
public static class HttpResults
{
    public const string ReplayHeader = "Idempotent-Replayed";

    public static int StatusCodeFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.Transient => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static IResult Problem(LedgerError error) => Problem(StatusCodeFor(error.Kind), error.Code, error.Message);

    public static IResult Problem(int statusCode, string code, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: code,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult ToHttp<T>(this Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);

    /// <summary>
    /// Idempotent commands: the first execution returns 201; a replay returns the identical status and
    /// body plus the Idempotent-Replayed header, so clients can safely retry without special handling.
    /// </summary>
    public static IResult ToHttp<T>(this CommandResult<T> outcome, HttpContext context, Func<T, string> location)
    {
        if (outcome.Replayed)
        {
            context.Response.Headers[ReplayHeader] = "true";
        }

        return outcome.Result.IsSuccess
            ? Results.Created(location(outcome.Result.Value), outcome.Result.Value)
            : Problem(outcome.Result.Error);
    }
}

/// <summary>
/// Accepts a client-supplied X-Correlation-Id (validated) or creates one, echoes it on the response,
/// adds it to every log line of the request, and tags the current trace with it.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsValid(incoming)
            ? incoming
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        Security.CallerContextExtensions.SetCorrelationId(context, correlationId);
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("ledgercore.correlation_id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    // Untrusted input ends up in logs and the database: restrict length and alphabet.
    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
