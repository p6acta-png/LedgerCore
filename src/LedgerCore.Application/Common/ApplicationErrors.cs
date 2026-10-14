using LedgerCore.Domain.Common;

namespace LedgerCore.Application.Common;

/// <summary>Errors that originate in the application layer (input shape, idempotency, access).</summary>
public static class ApplicationErrors
{
    public static readonly LedgerError IdempotencyKeyMissing =
        new("IDEMPOTENCY_KEY_REQUIRED", "The Idempotency-Key header is required for this operation.", ErrorKind.Validation);

    public static readonly LedgerError IdempotencyKeyInvalid =
        new("IDEMPOTENCY_KEY_INVALID",
            "Idempotency-Key must be 8-128 characters of A-Z, a-z, 0-9, '-', '_', '.', ':'.",
            ErrorKind.Validation);

    public static readonly LedgerError IdempotencyKeyReused =
        new("IDEMPOTENCY_KEY_REUSED",
            "This Idempotency-Key was already used with a different request payload. Use a new key for a new request.",
            ErrorKind.BusinessRule);

    public static readonly LedgerError CustomerRoleRequired =
        new("FORBIDDEN", "This operation requires an authenticated customer.", ErrorKind.Forbidden);

    public static readonly LedgerError OperatorRoleRequired =
        new("FORBIDDEN", "This operation requires the ledger-operator role.", ErrorKind.Forbidden);

    public static LedgerError Validation(string message) => new("VALIDATION_FAILED", message, ErrorKind.Validation);

    public static LedgerError TransferNotFound(Guid id) =>
        new("TRANSFER_NOT_FOUND", $"Transfer {id} was not found.", ErrorKind.NotFound);

    public static LedgerError JournalEntryNotFound(Guid id) =>
        new("JOURNAL_ENTRY_NOT_FOUND", $"Journal entry {id} was not found.", ErrorKind.NotFound);

    public static LedgerError WalletLimitReached(int max) =>
        new("WALLET_LIMIT_REACHED", $"A customer may own at most {max} wallets.", ErrorKind.BusinessRule);
}

/// <summary>
/// Thrown when a ledger transaction could not complete because of a temporary condition
/// (lock timeout, repeated deadlock/serialization failure, lost connection) after all retries.
/// The client may retry with the same Idempotency-Key; no partial state was committed by the failed attempt.
/// </summary>
public sealed class LedgerTransientException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
