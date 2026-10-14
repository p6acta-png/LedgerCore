using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LedgerCore.Application.Common;
using LedgerCore.Domain.Common;

namespace LedgerCore.Application.Idempotency;

/// <summary>A validated client-supplied Idempotency-Key header value.</summary>
public sealed record IdempotencyKey
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private IdempotencyKey(string value) => Value = value;

    public string Value { get; }

    public static Result<IdempotencyKey> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return ApplicationErrors.IdempotencyKeyMissing;
        }

        if (raw.Length is < MinLength or > MaxLength || !raw.All(IsAllowed))
        {
            return ApplicationErrors.IdempotencyKeyInvalid;
        }

        return new IdempotencyKey(raw);
    }

    // Deliberately narrow alphabet: keys end up in logs and SQL parameters, so no whitespace,
    // quotes or control characters are accepted. UUIDs and ULIDs fit.
    private static bool IsAllowed(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or ':';

    public override string ToString() => Value;
}

/// <summary>
/// Uniqueness scope of a key: (principal, operation, key). Two different callers can use the same key
/// without colliding, and a caller cannot observe another caller's stored responses.
/// </summary>
public sealed record IdempotencyScope(string PrincipalId, string Operation, IdempotencyKey Key);

/// <summary>Outcome of trying to claim an idempotency key.</summary>
public abstract record IdempotencyClaim
{
    private IdempotencyClaim()
    {
    }

    /// <summary>This transaction now owns the key and must complete it before committing.</summary>
    public sealed record Claimed : IdempotencyClaim;

    /// <summary>The key was completed by an earlier committed transaction.</summary>
    public sealed record AlreadyCompleted(string RequestHash, StoredOutcome Outcome) : IdempotencyClaim;
}

/// <summary>The serialized result kept for replay. Both successes and business rejections are stored.</summary>
public sealed record StoredOutcome(bool Succeeded, string ResponseJson, Guid? ResourceId)
{
    public static StoredOutcome From<T>(Result<T> result, Func<T, Guid?> resourceId) =>
        result.IsSuccess
            ? new StoredOutcome(true, JsonSerializer.Serialize(result.Value, LedgerJson.Options), resourceId(result.Value))
            : new StoredOutcome(false, JsonSerializer.Serialize(result.Error, LedgerJson.Options), null);

    public Result<T> ToResult<T>()
    {
        if (Succeeded)
        {
            var value = JsonSerializer.Deserialize<T>(ResponseJson, LedgerJson.Options)
                ?? throw new InvalidOperationException("Stored idempotent response is empty.");
            return value;
        }

        var error = JsonSerializer.Deserialize<LedgerError>(ResponseJson, LedgerJson.Options)
            ?? throw new InvalidOperationException("Stored idempotent error is empty.");
        return error;
    }
}

/// <summary>Result of an idempotent command plus whether it was served from the stored record.</summary>
public sealed record CommandResult<T>(Result<T> Result, bool Replayed)
{
    public static CommandResult<T> Fresh(Result<T> result) => new(result, Replayed: false);

    public static CommandResult<T> Rejected(LedgerError error) => new(error, Replayed: false);
}

/// <summary>
/// SHA-256 over a canonical JSON rendering of the validated request. Used to detect a key being
/// reused with a different payload. The canonical object is a record, so property order is fixed.
/// </summary>
public static class RequestFingerprint
{
    public static string Compute<T>(T canonicalRequest)
    {
        var json = JsonSerializer.Serialize(canonicalRequest, LedgerJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>
    /// How long a completed key is guaranteed to be remembered. Clients must finish retrying within this
    /// window; after it the cleanup job may delete the record and the key becomes unused again.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}
