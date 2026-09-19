using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;
using Shouldly;

namespace Engine.Tests.Domain.Aggregates;

public class PositionTests
{
    private static Position AaplAt(decimal quantity, decimal price) =>
        new(new Ticker("AAPL"), quantity, new Money(price));

    [Fact]
    public void AddQuantity_averages_two_equal_lots()
    {
        var position = AaplAt(1m, 100m);

        position.AddQuantity(1m, new Money(200m));

        position.Quantity.ShouldBe(2m);
        position.AveragePurchasePrice.Amount.ShouldBe(150m);
    }

    [Fact]
    public void AddQuantity_weights_by_quantity_not_by_lot_count()
    {
        // 2 @ 100 plus 1 @ 400 is 200, not the 250 a naive mean would give.
        var position = AaplAt(2m, 100m);

        position.AddQuantity(1m, new Money(400m));

        position.AveragePurchasePrice.Amount.ShouldBe(200m);
    }

    [Fact]
    public void AddQuantity_accumulates_over_several_calls()
    {
        var position = AaplAt(1m, 100m);

        position.AddQuantity(1m, new Money(200m));
        position.AddQuantity(2m, new Money(300m));

        position.Quantity.ShouldBe(4m);
        position.AveragePurchasePrice.Amount.ShouldBe(225m);
    }

    [Fact]
    public void AddQuantity_adopts_the_currency_of_the_incoming_price()
    {
        // Documents a real gap: a EUR price silently redenominates a USD position.
        // Stage 2 gives Money a currency guard; until then this is the behaviour.
        var position = AaplAt(1m, 100m);

        position.AddQuantity(1m, new Money(200m, "EUR"));

        position.AveragePurchasePrice.Currency.ShouldBe("EUR");
    }
}
