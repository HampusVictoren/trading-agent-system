namespace Engine.Domain.Aggregates.Portfolio;

using Engine.Domain.ValueObjects;

public class Portfolio
{
    // Version 7 rather than 4: the value is time-ordered, so rows arrive at the end of the
    // primary key's index instead of scattering across it.
    public Guid Id { get; } = Guid.CreateVersion7();
    public Money CashBalance { get; private set; }
    private readonly List<Position> _positions = new();
    public IReadOnlyCollection<Position> Positions => _positions.AsReadOnly();

    private readonly List<Order> _newOrders = new();

    /// <summary>
    /// The orders this instance has placed since it was loaded - not the ledger. The
    /// repository deliberately does not read the existing orders back: nothing in the domain
    /// needs them, and loading every order ever placed in order to append one more would grow
    /// with the account's history. The ledger is <c>trading.orders</c>, and it is queried.
    /// </summary>
    public IReadOnlyCollection<Order> NewOrders => _newOrders.AsReadOnly();

    public Portfolio(Money initialBalance)
    {
        CashBalance = initialBalance;
    }

    /// <summary>
    /// For the ORM only: EF Core rebuilds a stored portfolio through this and writes the
    /// identity, the balance and the positions straight to the backing fields. Domain code
    /// uses the public constructor, so a portfolio that code creates always has a balance.
    /// </summary>
    private Portfolio()
    {
        CashBalance = Money.Zero();
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

    /// <summary>
    /// Moves the cash, opens or grows the position, and returns the ledger line for it.
    /// The order is built here rather than by the caller so that the money moving and the
    /// record of it cannot come apart - and it is appended only once the balance has been
    /// checked, so a refused buy leaves nothing behind.
    /// </summary>
    public Order ExecuteBuy(Ticker ticker, decimal quantity, Money price)
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

        var order = new Order(Guid.CreateVersion7(), Id, ticker, OrderSide.Buy, quantity, price);
        _newOrders.Add(order);
        return order;
    }
}
