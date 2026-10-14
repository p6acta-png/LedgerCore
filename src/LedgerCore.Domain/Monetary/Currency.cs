using LedgerCore.Domain.Common;

namespace LedgerCore.Domain.Monetary;

/// <summary>
/// An ISO 4217 currency the ledger supports. Instances are only obtainable through <see cref="Parse"/>
/// or the static fields, so an unsupported currency can never reach the ledger.
/// </summary>
public sealed record Currency
{
    /// <summary>Norwegian krone; 1 NOK = 100 Гёre (minor units).</summary>
    public static readonly Currency Nok = new("NOK", minorUnitDigits: 2);

    private static readonly Dictionary<string, Currency> SupportedByCode =
        new(StringComparer.Ordinal) { [Nok.Code] = Nok };

    private Currency(string code, int minorUnitDigits)
    {
        Code = code;
        MinorUnitDigits = minorUnitDigits;
    }

    /// <summary>Three-letter upper-case ISO 4217 code.</summary>
    public string Code { get; }

    /// <summary>Number of decimal digits in one major unit (2 for NOK: 100 Гёre = 1 krone).</summary>
    public int MinorUnitDigits { get; }

    public static IReadOnlyCollection<Currency> Supported => SupportedByCode.Values;

    /// <summary>Parses client input. Matching is exact and case-sensitive ("NOK", not "nok").</summary>
    public static Result<Currency> Parse(string? code) =>
        code is not null && SupportedByCode.TryGetValue(code, out var currency)
            ? currency
            : LedgerErrors.UnsupportedCurrency(code);

    /// <summary>
    /// Rehydrates a currency read from the database. An unknown value there means the schema and code
    /// disagree, which is a bug rather than a user error, hence the exception.
    /// </summary>
    public static Currency FromStorage(string code) =>
        SupportedByCode.TryGetValue(code.Trim(), out var currency)
            ? currency
            : throw new InvalidOperationException($"Database contains unsupported currency '{code}'.");

    public override string ToString() => Code;
}
