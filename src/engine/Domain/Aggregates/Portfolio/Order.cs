namespace Engine.Domain.Aggregates.Portfolio;

using Engine.Domain.ValueObjects;

/// <summary>Which way the money went.</summary>
public enum OrderSide
{
    Buy,
    Sell
}

/// <summary>
/// Why the order was placed. A buy is always <see cref="Signal"/>; a sale can also come from
/// a rule the engine agreed to in advance.
/// </summary>
/// <remarks>
/// It is on the ledger rather than derived when somebody asks, because it cannot be derived:
/// a sale of a whole holding looks the same whichever of the three produced it. And the
/// distinction is the one stage 8 needs most - "the agents' exits beat the mechanical ones"
/// is a question about these values, and a row without one cannot answer it.
///
/// The minimum holding period reads this too. A stop-loss is not a change of mind, so it is
/// not what that rule exists to slow down.
/// </remarks>
public enum OrderTrigger
{
    /// <summary>The agents argued for it. Every buy, and a sale they asked for.</summary>
    Signal,

    /// <summary>The price fell past what the policy tolerates against the average purchase price.</summary>
    StopLoss,

    /// <summary>The horizon the thesis asked for has passed without the thesis being renewed.</summary>
    TimeLimit
}

/// <summary>
/// One line in the ledger: money that actually moved. Kept apart from a decision on purpose -
/// a decision is what the system thought, an order is what it did, and most decisions produce
/// no order at all. The table is append-only, so this type has no mutating member.
/// </summary>
/// <remarks>
/// It is created by <see cref="Portfolio.ExecuteBuy"/> and <see cref="Portfolio.ExecuteSell"/>
/// rather than by a caller, so that the cash movement and the record of it cannot come apart.
/// When an order is placed is the database's <c>placed_at</c>: it is audit metadata rather than
/// something the domain reasons about. The holding period that stage 5's exits count is domain
/// data and lives on the position, written by the engine's own clock.
/// </remarks>
public sealed class Order
{
    public Guid Id { get; }
    public Guid PortfolioId { get; }
    public Ticker Ticker { get; }
    public OrderSide Side { get; }
    public decimal Quantity { get; }
    public Money Price { get; }

    /// <summary>
    /// What the shares sold made or lost against what they cost. Null on a buy, because a
    /// purchase realises nothing - the position it opens is where the result lives until it
    /// is sold. Negative on a loss, which is a figure rather than an error.
    /// </summary>
    /// <remarks>
    /// On the order rather than on the position, because it is a fact about a transaction and
    /// the position it came from may no longer exist by the time anybody asks. It is also the
    /// only number in this ledger that cannot be recomputed from the row: the average purchase
    /// price it was measured against is gone once the holding is closed.
    /// </remarks>
    public Money? RealisedProfitAndLoss { get; }

    /// <summary>What made the engine place this order. Never null: an order nobody can account
    /// for is a row that makes the ledger less trustworthy than no row would.</summary>
    public OrderTrigger Trigger { get; }

    public Order(
        Guid id,
        Guid portfolioId,
        Ticker ticker,
        OrderSide side,
        decimal quantity,
        Money price,
        OrderTrigger trigger,
        Money? realisedProfitAndLoss = null)
    {
        Id = id;
        PortfolioId = portfolioId;
        Ticker = ticker;
        Side = side;
        Quantity = quantity;
        Price = price;
        Trigger = trigger;
        RealisedProfitAndLoss = realisedProfitAndLoss;
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
