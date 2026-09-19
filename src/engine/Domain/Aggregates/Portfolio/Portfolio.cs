namespace Engine.Domain.Aggregates.Portfolio;

using Engine.Domain.ValueObjects;

public class Portfolio
{
    public Guid Id { get; } = Guid.NewGuid();
    public Money CashBalance { get; private set; }
    private readonly List<Position> _positions = new();
    public IReadOnlyCollection<Position> Positions => _positions.AsReadOnly();

    public Portfolio(Money initialBalance)
    {
        CashBalance = initialBalance;
    }

    public void ExecuteBuy(Ticker ticker, decimal quantity, Money price)
    {
        var totalCost = price.Amount * quantity;
        if (CashBalance.Amount < totalCost)
            throw new InvalidOperationException("Insufficient cash balance in the portfolio.");

        CashBalance = CashBalance.Subtract(new Money(totalCost, price.Currency));

        var existing = _positions.FirstOrDefault(p => p.Ticker == ticker);
        if (existing != null)
        {
            existing.AddQuantity(quantity, price);
        }
        else
        {
            _positions.Add(new Position(ticker, quantity, price));
        }
    }
}
