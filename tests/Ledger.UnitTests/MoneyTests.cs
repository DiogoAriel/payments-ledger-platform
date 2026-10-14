using FluentAssertions;
using Ledger.Domain;

namespace Ledger.UnitTests;

public class MoneyTests
{
    [Fact]
    public void Adds_amounts_in_the_same_currency()
    {
        var total = Money.Of(10.50m, "BRL") + Money.Of(0.25m, "BRL");

        total.Should().Be(Money.Of(10.75m, "BRL"));
    }

    [Fact]
    public void Refuses_arithmetic_across_currencies()
    {
        var act = () => Money.Of(1m, "BRL") + Money.Of(1m, "USD");

        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Refuses_more_decimals_than_the_currency_allows()
    {
        var act = () => Money.Of(1.005m, "BRL");

        act.Should().Throw<LedgerException>().WithMessage("*decimal places*");
    }

    [Fact]
    public void Rejects_unknown_currency_codes()
    {
        var act = () => Currency.FromCode("XYZ");

        act.Should().Throw<LedgerException>().WithMessage("*not supported*");
    }

    [Fact]
    public void Formats_with_the_minor_units_of_the_currency()
    {
        Money.Of(5m, "USD").ToString().Should().Be("5.00 USD");
    }
}
