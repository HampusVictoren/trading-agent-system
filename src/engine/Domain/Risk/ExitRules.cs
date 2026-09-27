namespace Engine.Domain.Risk;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;

/// <summary>
/// Whether a holding should be sold without asking anyone. Two rules, both pure functions of
/// what the position already knows.
/// </summary>
/// <remarks>
/// These exist because the agents cannot be relied on to close what they opened. They are given
/// one instrument at a time with no memory of having bought it, and the stance they answer most
/// often is HOLD - so a thesis that stopped being true has no mechanism to end. The exits are
/// that mechanism, and being deterministic is the point: they fire on a number, at a size
/// nobody argues about, and they are the baseline stage 8 measures the agents' own exits
/// against.
///
/// A domain function rather than part of the use case, so the rules can be read and tested
/// without a portfolio repository, an HTTP client or a clock.
/// </remarks>
public static class ExitRules
{
    /// <summary>
    /// Which exit applies to <paramref name="position"/> at <paramref name="price"/>, or null
    /// when it should be left alone.
    /// </summary>
    /// <remarks>
    /// The stop-loss is checked first, so a position that has both fallen and run out of time is
    /// recorded as having been cut rather than as having merely expired. That order is about the
    /// ledger rather than the trade - the sale is the same either way - and the ledger is what
    /// the comparison is made from.
    /// </remarks>
    public static OrderTrigger? Triggered(
        Position position, Money price, DateTimeOffset now, RiskPolicy policy)
    {
        var cost = position.AveragePurchasePrice;

        // Subtracted rather than compared as two numbers, because Money refuses to combine
        // currencies: a price in the wrong currency throws here instead of being read as though
        // a krona and a dollar were the same thing.
        var change = price.Subtract(cost);
        var tolerated = cost.Multiply(policy.StopLossPct);

        if (change.Amount <= -tolerated.Amount)
            return OrderTrigger.StopLoss;

        // Calendar days, because that is the unit the prompt asked the model for. The engine
        // measures outcomes in trading days, and deliberately not here: a thesis that said "two
        // weeks" meant two weeks, not ten sessions.
        if (now - position.LastPurchasedAt >= TimeSpan.FromDays(position.ThesisHorizonDays))
            return OrderTrigger.TimeLimit;

        return null;
    }
}
