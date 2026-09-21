namespace Engine.Domain.Aggregates.Portfolio;

using Engine.Domain.Exceptions;
using Engine.Domain.ValueObjects;

public class Position
{
    public Ticker Ticker { get; }
    public decimal Quantity { get; private set; }
    public Money AveragePurchasePrice { get; private set; }

    public Position(Ticker ticker, decimal quantity, Money averagePurchasePrice)
    {
        Ticker = ticker;
        Quantity = quantity;
        AveragePurchasePrice = averagePurchasePrice;
    }

    public void AddQuantity(decimal addedQuantity, Money price)
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
    }
}
