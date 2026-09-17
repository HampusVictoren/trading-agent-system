namespace Engine.Domain.Aggregates.Portfolio;

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
        var totalCost = (Quantity * AveragePurchasePrice.Amount) + (addedQuantity * price.Amount);
        Quantity += addedQuantity;
        AveragePurchasePrice = new Money(totalCost / Quantity, price.Currency);
    }
}
