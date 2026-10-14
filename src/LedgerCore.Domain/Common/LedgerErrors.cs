namespace LedgerCore.Domain.Common;

/// <summary>
/// Catalogue of domain errors. Codes are part of the public API contract: clients may branch on them,
/// so they must stay stable.
/// </summary>
public static class LedgerErrors
{
    public static LedgerError UnsupportedCurrency(string? code) =>
        new("UNSUPPORTED_CURRENCY", $"Currency '{code}' is not supported. Supported currencies: NOK.", ErrorKind.Validation);

    public static LedgerError AmountNotPositive(long minorUnits) =>
        new("INVALID_AMOUNT", $"Amount must be a positive integer number of minor units; got {minorUnits}.", ErrorKind.Validation);

    public static LedgerError AmountTooLarge(long minorUnits, long max) =>
        new("INVALID_AMOUNT", $"Amount {minorUnits} exceeds the per-operation maximum of {max} minor units.", ErrorKind.Validation);

    public static LedgerError InvalidDescription(string reason) =>
        new("VALIDATION_FAILED", $"Description is invalid: {reason}", ErrorKind.Validation);

    public static readonly LedgerError SelfTransfer =
        new("SELF_TRANSFER", "Source and destination wallets must be different.", ErrorKind.Validation);

    public static LedgerError WalletNotFound(WalletId id) =>
        new("WALLET_NOT_FOUND", $"Wallet {id} was not found.", ErrorKind.NotFound);

    public static readonly LedgerError SourceWalletNotOwned =
        new("FORBIDDEN", "The caller does not own the source wallet.", ErrorKind.Forbidden);

    public static LedgerError WalletNotActive(WalletId id) =>
        new("WALLET_NOT_ACTIVE", $"Wallet {id} is frozen and cannot be debited or credited.", ErrorKind.BusinessRule);

    public static LedgerError CurrencyMismatch(string expected, string actual) =>
        new("CURRENCY_MISMATCH", $"Wallet currency {expected} does not match requested currency {actual}.", ErrorKind.BusinessRule);

    public static LedgerError InsufficientFunds(long availableMinor, long requestedMinor) =>
        new("INSUFFICIENT_FUNDS", $"Available balance {availableMinor} is less than the requested {requestedMinor} minor units.", ErrorKind.BusinessRule);

    public static LedgerError NegativeBalanceNotAllowed(string accountCode) =>
        new("INSUFFICIENT_FUNDS", $"Posting would make account {accountCode} negative.", ErrorKind.BusinessRule);

    public static LedgerError BalanceOverflow(string accountCode) =>
        new("BALANCE_OVERFLOW", $"Posting would overflow the balance of account {accountCode}.", ErrorKind.BusinessRule);

    public static LedgerError UnbalancedEntry(string reason) =>
        new("UNBALANCED_JOURNAL_ENTRY", $"Journal entry rejected: {reason}", ErrorKind.BusinessRule);

    public static readonly LedgerError CannotReverseReversal =
        new("REVERSAL_NOT_ALLOWED", "A reversal entry cannot itself be reversed.", ErrorKind.BusinessRule);

    public static LedgerError TransferAlreadyReversed(TransferId id) =>
        new("TRANSFER_ALREADY_REVERSED", $"Transfer {id} has already been reversed.", ErrorKind.Conflict);

    public static LedgerError ReversalInsufficientFunds(WalletId walletId, long availableMinor, long requiredMinor) =>
        new("REVERSAL_INSUFFICIENT_FUNDS",
            $"Wallet {walletId} holds {availableMinor} minor units but the reversal needs {requiredMinor}; the receiver has already spent the funds.",
            ErrorKind.BusinessRule);
}
