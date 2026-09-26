using Engine.Domain.Exceptions;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.ValueObjects;

public class MoneyTests
{
    [Fact]
    public void Add_sums_the_amounts_and_keeps_the_currency()
    {
        // Both sides are built from the default, because what this asserts is that Add keeps
        // the currency - not which currency the account happens to be in.
        new Money(100m).Add(new Money(50m))
            .ShouldBe(new Money(150m, Money.DefaultCurrency));
    }

    [Fact]
    public void Subtract_can_produce_a_negative_amount()
    {
        // Money itself does not guard against going negative; the portfolio does.
        new Money(100m).Subtract(new Money(150m)).Amount.ShouldBe(-50m);
    }

    [Fact]
    public void Arithmetic_leaves_the_original_untouched()
    {
        var original = new Money(100m);

        original.Add(new Money(1m));

        original.Amount.ShouldBe(100m);
    }

    [Fact]
    public void Zero_defaults_to_usd()
    {
        // Named rather than spelled, because this test is about the default being *the*
        // default - the account's currency moved from USD to SEK and the assertion should
        // not have to move with it.
        Money.Zero().ShouldBe(new Money(0m, Money.DefaultCurrency));
    }

    [Fact]
    public void Add_rejects_a_different_currency()
    {
        // A CurrencyMismatchException rather than InvalidOperationException, so the worker
        // can tell a domain rule from a bug. Until stage 2 this was logged as a crash.
        Should.Throw<CurrencyMismatchException>(
                () => new Money(100m, "USD").Add(new Money(100m, "EUR")))
            .Message.ShouldContain("USD");
    }

    [Fact]
    public void Subtract_rejects_a_different_currency()
    {
        Should.Throw<CurrencyMismatchException>(
            () => new Money(100m, "USD").Subtract(new Money(100m, "EUR")));
    }

    [Theory]
    [InlineData("usd")]
    [InlineData(" Usd ")]
    public void A_currency_is_normalised_rather_than_compared_literally(string written)
    {
        // Was a known gap: "usd" and "USD" were different currencies, so the same money
        // could refuse to add to itself depending on who wrote the string.
        new Money(1m, written).Currency.ShouldBe("USD");
        new Money(1m, "USD").Add(new Money(1m, written)).Amount.ShouldBe(2m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("dollars")]
    [InlineData("US")]
    [InlineData("US1")]
    public void A_currency_that_is_not_a_currency_code_is_refused(string written)
    {
        Should.Throw<ArgumentException>(() => new Money(1m, written));
    }

    [Fact]
    public void A_copy_normalises_too()
    {
        // `with` bypasses the constructor, so the rule lives in the property itself.
        (new Money(1m, "USD") with { Currency = "eur" }).Currency.ShouldBe("EUR");
    }

    [Theory]
    [InlineData(10, 0.05, 0.5)]
    [InlineData(10, 0, 0)]
    [InlineData(10, 3, 30)]
    public void Multiply_scales_the_amount_and_keeps_the_currency(decimal amount, decimal factor, decimal expected)
    {
        var result = new Money(amount, "USD").Multiply(factor);

        result.Amount.ShouldBe(expected);
        result.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Divide_splits_the_amount()
    {
        new Money(100m).Divide(4m).Amount.ShouldBe(25m);
    }

    [Fact]
    public void Divide_by_zero_is_refused()
    {
        Should.Throw<DivideByZeroException>(() => new Money(100m).Divide(0m));
    }

    [Fact]
    public void Min_picks_the_smaller_amount()
    {
        // The sizer takes the smaller of the position headroom and the spendable cash,
        // so whichever limit binds first is the one that applies.
        Money.Min(new Money(100m), new Money(40m)).Amount.ShouldBe(40m);
        Money.Min(new Money(-5m), new Money(40m)).Amount.ShouldBe(-5m);
    }

    [Fact]
    public void Min_refuses_to_compare_different_currencies()
    {
        Should.Throw<CurrencyMismatchException>(
            () => Money.Min(new Money(1m, "USD"), new Money(1m, "EUR")));
    }

    [Fact]
    public void Equality_is_by_value_including_currency()
    {
        new Money(10m, "USD").ShouldBe(new Money(10m, "USD"));
        new Money(10m, "USD").ShouldNotBe(new Money(10m, "EUR"));
    }
}
