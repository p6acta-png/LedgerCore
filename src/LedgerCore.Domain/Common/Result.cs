namespace LedgerCore.Domain.Common;

/// <summary>
/// Either a successful value or a <see cref="LedgerError"/>. Used for outcomes that are part of normal
/// business flow (insufficient funds, unsupported currency, ...), so callers must handle them explicitly.
/// </summary>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly LedgerError? _error;

    private Result(T value)
    {
        _value = value;
    }

    private Result(LedgerError error)
    {
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({_error}).");

    public LedgerError Error => _error
        ?? throw new InvalidOperationException("Cannot read the error of a successful result.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(LedgerError error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(LedgerError error) => Failure(error);

    public override string ToString() => IsSuccess ? $"Success({_value})" : $"Failure({_error})";
}
