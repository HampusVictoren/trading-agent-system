namespace Engine.Tests;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.Signals;
using Engine.Domain.ValueObjects;

/// <summary>
/// The three-argument buy the tests used before a purchase had to carry a timestamp and a
/// horizon.
/// </summary>
/// <remarks>
/// C# considers extension methods only when no instance method applies, so every existing
/// call site binds here without being edited - and a test that is about *when* something was
/// bought calls the real five-argument method explicitly, which is the point. Forty tests
/// about sizing, valuation and persistence should not have to name a purchase date to say
/// what they are about, and making the domain's parameters optional to spare them would let
/// production code forget the two values the deterministic exits depend on.
/// </remarks>
internal static class PortfolioTestExtensions
{
    /// <summary>A fixed instant, so nothing here depends on the clock.</summary>
    internal static readonly DateTimeOffset BoughtAt = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Inside the contract's cap, and not a round number, so a test asserting on it
    /// cannot pass by coincidence.</summary>
    internal const int ThesisHorizonDays = 15;

    internal static Order ExecuteBuy(
        this Portfolio portfolio, Ticker ticker, decimal quantity, Money price) =>
        portfolio.ExecuteBuy(ticker, quantity, price, BoughtAt, ThesisHorizonDays);

    internal static void AddQuantity(this Position position, decimal addedQuantity, Money price) =>
        position.AddQuantity(addedQuantity, price, BoughtAt, ThesisHorizonDays);

    /// <summary>The sale a test means when it is not about why the sale happened.</summary>
    internal static Order ExecuteSell(
        this Portfolio portfolio, Ticker ticker, decimal quantity, Money price) =>
        portfolio.ExecuteSell(ticker, quantity, price, OrderTrigger.Signal);

    /// <summary>A holding with the two new values filled in, for tests that predate them.</summary>
    internal static Position APosition(Ticker ticker, decimal quantity, Money averagePrice) =>
        new(ticker, quantity, averagePrice, BoughtAt, ThesisHorizonDays);
}

/// <summary>
/// The sizer and the gate as they read before a trading day had a budget of its own.
/// </summary>
/// <remarks>
/// Extension methods, not optional parameters on the real signatures. An optional
/// <c>deployedToday</c> would mean a production call site that forgot it silently said "nothing
/// spent today", which is the shape of mistake that cost #52 its screen key: a property that made
/// forgetting impossible was traded for a convention, and the first new call site broke it.
/// Here the default is a statement about the *test* - this case is not about the daily limit -
/// and the compiler still refuses to let the engine omit it.
/// </remarks>
public static class DailyBudgetTestExtensions
{
    public static OrderIntent Size(
        this PositionSizer sizer,
        TradeSignal signal,
        Portfolio portfolio,
        PriceSnapshot holdingPrices,
        RiskPolicy policy) =>
        sizer.Size(signal, portfolio, holdingPrices, policy, Money.Zero());

    public static RiskDecision Evaluate(
        this RiskEngine engine,
        OrderIntent.Buy order,
        TradeSignal signal,
        Portfolio portfolio,
        PriceSnapshot holdingPrices,
        RiskPolicy policy,
        DateTimeOffset now) =>
        engine.Evaluate(order, signal, portfolio, holdingPrices, policy, now, Money.Zero());
}
