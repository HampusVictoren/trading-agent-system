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

    /// <summary>
    /// Cash plus the market value of every holding, or which holding stopped it being
    /// worked out. Sizing is measured against this rather than against cash: a portfolio
    /// that is fully invested still has a position limit.
    /// </summary>
    public PortfolioValuation Value(PriceSnapshot prices)
    {
        var total = CashBalance;

        foreach (var position in _positions)
        {
            if (!prices.TryGet(position.Ticker, out var price))
                return new PortfolioValuation.PriceMissing(position.Ticker);

            // A price in another currency is a bug, not an outcome, so it throws.
            total = total.Add(price.Multiply(position.Quantity));
        }

        return new PortfolioValuation.Valued(total);
    }

    /// <summary>
    /// What is already held of one instrument, at the given price. Zero when nothing is
    /// held, so a first buy gets the whole position cap rather than a special case.
    /// </summary>
    public Money MarketValueOf(Ticker ticker, Money price)
    {
        var position = _positions.FirstOrDefault(held => held.Ticker == ticker);

        return position is null ? Money.Zero(price.Currency) : price.Multiply(position.Quantity);
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
