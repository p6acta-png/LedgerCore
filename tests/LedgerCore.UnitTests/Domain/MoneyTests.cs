using LedgerCore.Domain.Monetary;
using Xunit;

namespace LedgerCore.UnitTests.Domain;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(80_000)]
    [InlineData(Money.MaxOperationMinorUnits)]
    public void Positive_amounts_within_the_limit_are_accepted(long minor)
    {
        var money = Money.CreatePositive(minor, Currency.Nok);

        Assert.True(money.IsSuccess);
        Assert.Equal(minor, money.Value.MinorUnits);
        Assert.Same(Currency.Nok, money.Value.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Zero_and_negative_amounts_are_rejected(long minor)
    {
        var money = Money.CreatePositive(minor, Currency.Nok);

        Assert.True(money.IsFailure);
        Assert.Equal("INVALID_AMOUNT", money.Error.Code);
    }

    [Theory]
    [InlineData(Money.MaxOperationMinorUnits + 1)]
    [InlineData(long.MaxValue)]
    public void Amounts_above_the_per_operation_maximum_are_rejected(long minor)
    {
        var money = Money.CreatePositive(minor, Currency.Nok);

        Assert.True(money.IsFailure);
        Assert.Equal("INVALID_AMOUNT", money.Error.Code);
    }

    [Theory]
    [InlineData(80_000, "800.00 NOK")]
    [InlineData(1, "0.01 NOK")]
    [InlineData(0, "0.00 NOK")]
    [InlineData(-150, "-1.50 NOK")]
    [InlineData(100_000_000_000, "1000000000.00 NOK")]
    public void Formatting_uses_integer_arithmetic(long minor, string expected) =>
        Assert.Equal(expected, Money.Format(minor, Currency.Nok));

    [Fact]
    public void Only_exact_upper_case_NOK_is_supported()
    {
        Assert.True(Currency.Parse("NOK").IsSuccess);
        Assert.Equal("UNSUPPORTED_CURRENCY", Currency.Parse("nok").Error.Code);
        Assert.Equal("UNSUPPORTED_CURRENCY", Currency.Parse("EUR").Error.Code);
        Assert.Equal("UNSUPPORTED_CURRENCY", Currency.Parse(null).Error.Code);
        Assert.Equal("UNSUPPORTED_CURRENCY", Currency.Parse(" NOK").Error.Code);
    }

    [Fact]
    public void Unknown_currency_in_storage_is_treated_as_a_bug()
    {
        Assert.Same(Currency.Nok, Currency.FromStorage("NOK"));
        Assert.Throws<InvalidOperationException>(() => Currency.FromStorage("XXX"));
    }
}
