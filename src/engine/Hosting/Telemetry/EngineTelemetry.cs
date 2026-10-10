namespace Engine.Hosting.Telemetry;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Trading;

/// <summary>
/// The engine's own instruments: three counters and a histogram on one <see cref="Meter"/>, and
/// the <see cref="ActivitySource"/> a cycle's trace is made of. Both are named
/// <see cref="Name"/>, which is what an exporter subscribes to.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here exports anything. <see cref="System.Diagnostics.Metrics"/> and
/// <see cref="System.Diagnostics.Activity"/> cost next to nothing when nobody listens, so the
/// instruments are always there and recording is unconditional. Whether anything leaves the
/// process is decided once, at startup, by <c>TelemetryExportExtensions</c> - and when no
/// endpoint is configured, nothing does. <c>dotnet-counters monitor --counters Engine</c> can
/// still read the counters of a running engine, which is the cheapest way to look at them.
/// </para>
/// <para>
/// The names are the roadmap's, Prometheus-style: <c>decisions_total</c>,
/// <c>risk_rejections_total</c>, <c>agent_latency_seconds</c>. Every label is a closed set - an
/// outcome, a mode, a side, an operation - and never a ticker, a reason or anything the agents
/// wrote. A label with free text in it is a new time series per value, and a reason is also the
/// one place a model's words could leak into a monitoring system.
/// </para>
/// </remarks>
public sealed class EngineTelemetry
{
    public const string Name = "Engine";

    public const string DecisionsName = "decisions_total";
    public const string RiskRejectionsName = "risk_rejections_total";
    public const string AgentLatencyName = "agent_latency_seconds";

    /// <summary>
    /// Static, as the guidance for an <see cref="ActivitySource"/> is: it holds no state of its
    /// own, a listener subscribes to it by name, and one per process is the shape tracing expects.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    private readonly Counter<long> _decisions;
    private readonly Counter<long> _riskRejections;
    private readonly Histogram<double> _agentLatency;

    public EngineTelemetry(IMeterFactory meters)
    {
        var meter = meters.Create(Name);

        _decisions = meter.CreateCounter<long>(
            DecisionsName,
            unit: "{decision}",
            description: "Analyses that ended in a stored decision, by outcome and trading mode.");

        _riskRejections = meter.CreateCounter<long>(
            RiskRejectionsName,
            unit: "{rejection}",
            description: "Orders the risk rules refused, by trading mode and side.");

        // Buckets for an LLM chain rather than the default's milliseconds-to-ten-seconds: a quote
        // answers in under a second, an analysis in 20-30 s, and the ceiling is the 245 s the
        // resilience pipeline allows a signal request with both of its attempts.
        _agentLatency = meter.CreateHistogram(
            AgentLatencyName,
            unit: "s",
            description: "How long one call to the agent service took, retry included, by operation and outcome.",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30, 45, 60, 90, 120, 180, 245]
            });
    }

    /// <summary>
    /// One stored decision. Called after the commit, like the log line about it, so the counter
    /// counts rows rather than intentions: an analysis whose transaction failed is not counted.
    /// </summary>
    /// <param name="side">
    /// Which way the refused order went. Only read for a risk rejection, the one outcome where it
    /// is needed to say anything the decisions counter does not already say.
    /// </param>
    public void DecisionStored(DecisionOutcome outcome, TradingMode mode, OrderSide? side = null)
    {
        _decisions.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            new KeyValuePair<string, object?>("mode", mode.ToString()));

        // A second counter for something the first already counts, because the roadmap names it
        // and because it can carry what the first cannot: the side. A sale refused by the holding
        // period and a buy refused by the daily limit are different stories, and only this one
        // tells them apart.
        if (outcome == DecisionOutcome.RejectedByRisk)
        {
            _riskRejections.Add(
                1,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("side", side?.ToString() ?? "Unknown"));
        }
    }

    /// <summary>One call to the agent service, from the engine's side of the wire.</summary>
    public void AgentCallCompleted(string operation, string outcome, TimeSpan elapsed) =>
        _agentLatency.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));
}
