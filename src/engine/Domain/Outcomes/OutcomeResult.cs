namespace Engine.Domain.Outcomes;

/// <summary>
/// What measuring one signal at one horizon came to. Three cases, and the difference between
/// the last two is what a scheduled job needs: "not due" means try again tomorrow, "not
/// measurable" means stop trying.
/// </summary>
public abstract record OutcomeResult
{
    private OutcomeResult() { }

    /// <summary>
    /// The measurement. Everything needed to re-derive the verdict is here, so that changing
    /// the band or the cost model later does not throw away what was already measured.
    /// </summary>
    /// <param name="On">The bar the horizon landed on.</param>
    /// <param name="Price">That bar's close.</param>
    /// <param name="InstrumentReturn">From the signal's reference price, gross.</param>
    /// <param name="BenchmarkReturn">The benchmark over the same period, gross.</param>
    /// <param name="ExcessReturn">Instrument minus benchmark, gross, whatever the stance.</param>
    /// <param name="CostFraction">
    /// What acting on this stance would have cost, as a fraction of notional. Zero for HOLD,
    /// which implies no trade. Stored rather than left in configuration, so that a row is
    /// still interpretable after somebody changes the settings.
    /// </param>
    /// <param name="NetEdge">
    /// What the stance would have earned over the benchmark after costs: the excess for a
    /// BUY, its negative for a SELL, and null for a HOLD, which earns nothing by design.
    /// This is the number the verdict is made on.
    /// </param>
    /// <param name="Hit">Whether the view was right, by the definition in the roadmap.</param>
    public sealed record Measured(
        DateOnly On,
        decimal Price,
        decimal InstrumentReturn,
        decimal BenchmarkReturn,
        decimal ExcessReturn,
        decimal CostFraction,
        decimal? NetEdge,
        bool Hit) : OutcomeResult;

    /// <summary>The horizon has not passed, or the data has not caught up with it. Ask again later.</summary>
    public sealed record NotDue(string Reason) : OutcomeResult;

    /// <summary>
    /// There is a hole where the measurement should be, and waiting will not fill it - a
    /// benchmark with no close before the signal, for instance. Recorded rather than
    /// retried, because a signal that quietly never gets measured is a signal missing from
    /// the population.
    /// </summary>
    public sealed record NotMeasurable(string Reason) : OutcomeResult;
}
