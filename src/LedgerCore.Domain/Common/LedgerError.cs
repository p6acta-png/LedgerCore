namespace LedgerCore.Domain.Common;

/// <summary>
/// Broad category of a failure. The API layer maps each kind to exactly one HTTP status code,
/// so the domain never needs to know about HTTP.
/// </summary>
public enum ErrorKind
{
    /// <summary>The request is malformed or violates an input rule (HTTP 400).</summary>
    Validation,

    /// <summary>A referenced resource does not exist or is not visible to the caller (HTTP 404).</summary>
    NotFound,

    /// <summary>The caller is authenticated but not allowed to perform the operation (HTTP 403).</summary>
    Forbidden,

    /// <summary>The operation conflicts with the current state of a resource (HTTP 409).</summary>
    Conflict,

    /// <summary>The request is well-formed but a business rule rejects it (HTTP 422).</summary>
    BusinessRule,

    /// <summary>A temporary condition; retrying with the same idempotency key is safe (HTTP 503).</summary>
    Transient,
}

/// <summary>
/// An expected, explainable failure with a stable machine-readable <see cref="Code"/>.
/// Unexpected failures (bugs, infrastructure crashes) are exceptions instead.
/// </summary>
public sealed record LedgerError(string Code, string Message, ErrorKind Kind)
{
    public override string ToString() => $"{Code}: {Message}";
}
