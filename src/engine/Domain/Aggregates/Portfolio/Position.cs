namespace Engine.Domain.Aggregates.Portfolio;

using Engine.Domain.Exceptions;
using Engine.Domain.ValueObjects;

public class Position
{
    public Ticker Ticker { get; }
    public decimal Quantity { get; private set; }
    public Money AveragePurchasePrice { get; private set; }

    /// <summary>
    /// When this position was last added to, and the horizon the thesis behind that purchase
    /// asked for. Both are domain data rather than audit metadata, because stage 5's
    /// deterministic exits read them: the time limit sells once the horizon has passed since
    /// the last buy, and the minimum holding period is measured from the same moment.
    /// </summary>
    /// <remarks>
    /// Only <see cref="AddQuantity"/> moves them, which is what makes "a HOLD does not extend
    /// the clock" a property of the design rather than a rule somebody has to remember. A sell
    /// leaves them alone: reducing a position does not restart its thesis.
    /// </remarks>
    public DateTimeOffset LastPurchasedAt { get; private set; }

    public int ThesisHorizonDays { get; private set; }

    public Position(
        Ticker ticker,
        decimal quantity,
        Money averagePurchasePrice,
        DateTimeOffset purchasedAt,
        int thesisHorizonDays)
    {
        Ticker = ticker;
        Quantity = quantity;
        AveragePurchasePrice = averagePurchasePrice;
        LastPurchasedAt = purchasedAt;
        ThesisHorizonDays = thesisHorizonDays;
    }

    /// <summary>For the ORM only. EF Core writes every value through the backing fields
    /// immediately after this runs, so the placeholders below are never observed.</summary>
    private Position()
    {
        Ticker = default!;
        AveragePurchasePrice = Money.Zero();
    }

    /// <summary>Whether the whole holding has been sold, so the caller can drop it.</summary>
    public bool IsClosed => Quantity == 0m;

    public void AddQuantity(decimal addedQuantity, Money price, DateTimeOffset at, int horizonDays)
    {
        // Without this a EUR price silently redenominated a USD position, and the average
        // became a number with no meaning.
        if (price.Currency != AveragePurchasePrice.Currency)
        {
            throw new CurrencyMismatchException(
                $"Cannot add a price in {price.Currency} to a position held in {AveragePurchasePrice.Currency}.");
        }

        var totalCost = (Quantity * AveragePurchasePrice.Amount) + (addedQuantity * price.Amount);
        Quantity += addedQuantity;
        AveragePurchasePrice = new Money(totalCost / Quantity, price.Currency);

        // The clock restarts on every purchase, including one that adds to a winner - the
        // thesis being acted on is the new one, and it is the new one the exits should judge.
        LastPurchasedAt = at;
        ThesisHorizonDays = horizonDays;
    }

    /// <summary>
    /// Sells part or all of the holding and returns what was realised on the shares sold.
    /// </summary>
    /// <remarks>
    /// The realised figure is the whole reason this lives here rather than in the caller: the
    /// average purchase price is this type's own state, and computing a profit anywhere else
    /// would mean exposing it for arithmetic. It can be negative, which `Money` allows.
    ///
    /// The average price is deliberately *not* recomputed. Selling does not change what the
    /// remaining shares cost, and a weighted average that moved on a sale would make every
    /// later realised figure wrong.
    /// </remarks>
    public Money ReduceQuantity(decimal soldQuantity, Money price)
    {
        if (price.Currency != AveragePurchasePrice.Currency)
        {
            throw new CurrencyMismatchException(
                $"Cannot sell at a price in {price.Currency} from a position held in {AveragePurchasePrice.Currency}.");
        }

        if (soldQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(soldQuantity), soldQuantity, "A sale has to be of a positive quantity.");
        }

        // A caller that asks to sell more than is held has a bug: sizing and the risk gate
        // both bound the quantity by the holding before it gets here. Short selling is a
        // deliberate no, so this cannot be the beginning of one either.
        if (soldQuantity > Quantity)
        {
            throw new InvalidOperationException(
                $"Cannot sell {soldQuantity} of {Ticker.Value} when {Quantity} is held.");
        }

        var realised = price.Subtract(AveragePurchasePrice).Multiply(soldQuantity);
        Quantity -= soldQuantity;

        return realised;
    }
}
