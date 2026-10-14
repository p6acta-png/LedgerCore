using System.Globalization;
using LedgerCore.Domain.Common;

namespace LedgerCore.Domain.Monetary;

/// <summary>
/// A strictly positive amount of money in integer minor units (Гёre for NOK).
/// Money is never represented as a floating-point number anywhere in LedgerCore.
/// </summary>
public sealed record Money
{
    /// <summary>
    /// Upper bound for a single operation: 1 000 000 000.00 NOK. Keeping single amounts far below
    /// <see cref="long.MaxValue"/> (~9.2e18) means sums of a handful of amounts can never overflow,
    /// and account balances need roughly 92 million maximum-size credits before overflow is possible.
    /// </summary>
    public const long MaxOperationMinorUnits = 100_000_000_000;

    private Money(long minorUnits, Currency currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    public long MinorUnits { get; }

    public Currency Currency { get; }

    public static Result<Money> CreatePositive(long minorUnits, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        if (minorUnits <= 0)
        {
            return LedgerErrors.AmountNotPositive(minorUnits);
        }

        if (minorUnits > MaxOperationMinorUnits)
        {
            return LedgerErrors.AmountTooLarge(minorUnits, MaxOperationMinorUnits);
        }

        return new Money(minorUnits, currency);
    }

    /// <summary>Formats with integer arithmetic only, e.g. 80000 Гёre -> "800.00 NOK".</summary>
    public override string ToString() => Format(MinorUnits, Currency);

    public static string Format(long minorUnits, Currency currency)
    {
        long divisor = 1;
        for (var i = 0; i < currency.MinorUnitDigits; i++)
        {
            divisor *= 10;
        }

        // Math.DivRem on the absolute value avoids rounding issues; long.MinValue cannot occur
        // because balances are bounded by database CHECK constraints and checked arithmetic.
        var sign = minorUnits < 0 ? "-" : string.Empty;
        var absolute = Math.Abs(minorUnits);
        var major = Math.DivRem(absolute, divisor, out var minor);
        var minorText = currency.MinorUnitDigits == 0
            ? string.Empty
            : "." + minor.ToString(CultureInfo.InvariantCulture).PadLeft(currency.MinorUnitDigits, '0');
        return $"{sign}{major.ToString(CultureInfo.InvariantCulture)}{minorText} {currency.Code}";
    }
}
