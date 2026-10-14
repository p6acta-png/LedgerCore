using LedgerCore.Application.Abstractions;
using LedgerCore.Application.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace LedgerCore.Api.Http;

/// <summary>
/// The single exception boundary of the API. Exceptions are logged with full detail on the server, but
/// clients only get a stable code and a generic message: no stack traces, SQL, or connection details.
/// </summary>
public sealed class LedgerExceptionHandler(IProblemDetailsService problemDetails, ILogger<LedgerExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "The request is malformed: check JSON syntax, required fields, field types and that no unknown fields are sent."),
            LedgerTransientException transient => (StatusCodes.Status503ServiceUnavailable, transient.Code, transient.Message),
            SimulatedFailureException simulated => (
                StatusCodes.Status500InternalServerError, "SIMULATED_FAILURE", $"Failure injected at {simulated.Point}; the transaction was rolled back."),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred. Use the correlation id when reporting it."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Request failed with {Code}", code);
        }
        else
        {
            logger.LogInformation("Rejected malformed request: {Reason}", exception.Message);
        }

        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.RetryAfter = "1";
        }

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = code,
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }
}
