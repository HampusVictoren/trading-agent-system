namespace Engine.Domain.Aggregates.Portfolio;

using Engine.Domain.ValueObjects;

/// <summary>Which way the money went. Selling arrives in stage 5; the column exists now so
/// that the ledger's meaning does not change when it does.</summary>
public enum OrderSide
{
    Buy,
    Sell
}

/// <summary>
/// One line in the ledger: money that actually moved. Kept apart from a decision on purpose -
/// a decision is what the system thought, an order is what it did, and most decisions produce
/// no order at all. The table is append-only, so this type has no mutating member.
/// </summary>
/// <remarks>
/// It is created by <see cref="Portfolio.ExecuteBuy"/> rather than by a caller, so that the
/// cash movement and the record of it cannot come apart. When an order is placed is the
/// database's <c>placed_at</c>: it is audit metadata rather than something the domain reasons
/// about. Stage 5 counts a holding period from the last purchase, and at that point the
/// timestamp becomes domain data and arrives through the engine's injected clock.
/// </remarks>
public sealed class Order
{
    public Guid Id { get; }
    public Guid PortfolioId { get; }
    public Ticker Ticker { get; }
    public OrderSide Side { get; }
    public decimal Quantity { get; }
    public Money Price { get; }

    public Order(Guid id, Guid portfolioId, Ticker ticker, OrderSide side, decimal quantity, Money price)
    {
        Id = id;
        PortfolioId = portfolioId;
        Ticker = ticker;
        Side = side;
        Quantity = quantity;
        Price = price;
    }

    /// <summary>For the ORM only. EF Core writes every value through the backing fields
    /// immediately after this runs, so the placeholders below are never observed.</summary>
    private Order()
    {
        Ticker = default!;
        Price = Money.Zero();
    }

    /// <summary>What the order cost, or raised. Currency comes from the price.</summary>
    public Money Notional => Price.Multiply(Quantity);
}
