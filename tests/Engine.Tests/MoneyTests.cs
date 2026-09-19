using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.ValueObjects;

public class MoneyTests
{
    [Fact]
    public void Add_sums_the_amounts_and_keeps_the_currency()
    {
        new Money(100m).Add(new Money(50m))
            .ShouldBe(new Money(150m, "USD"));
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
        Money.Zero().ShouldBe(new Money(0m, "USD"));
    }

    [Fact]
    public void Add_rejects_a_different_currency()
    {
        Should.Throw<InvalidOperationException>(
                () => new Money(100m, "USD").Add(new Money(100m, "EUR")))
            .Message.ShouldContain("USD");
    }

    [Fact]
    public void Subtract_rejects_a_different_currency()
    {
        Should.Throw<InvalidOperationException>(
            () => new Money(100m, "USD").Subtract(new Money(100m, "EUR")));
    }

    [Fact]
    public void Currency_matching_is_case_sensitive()
    {
        // Documents current behaviour: "usd" is not "USD". Stage 2 normalises this
        // when Money gains Multiply/Divide and a proper domain exception.
        Should.Throw<InvalidOperationException>(
            () => new Money(1m, "USD").Add(new Money(1m, "usd")));
    }

    [Fact]
    public void Equality_is_by_value_including_currency()
    {
        new Money(10m, "USD").ShouldBe(new Money(10m, "USD"));
        new Money(10m, "USD").ShouldNotBe(new Money(10m, "EUR"));
    }
}
