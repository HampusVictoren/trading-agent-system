using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Trading;
using Engine.Hosting.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;

namespace Engine.Tests.Hosting;

/// <summary>
/// The engine's counters, read with <see cref="MetricCollector{T}"/> - which subscribes the way
/// an exporter does, so what these tests see is what a collector would receive.
/// </summary>
public class EngineTelemetryTests
{
    private static (EngineTelemetry Telemetry, ServiceProvider Provider) Build()
    {
        var provider = new ServiceCollection().AddEngineTelemetry().BuildServiceProvider();
        return (provider.GetRequiredService<EngineTelemetry>(), provider);
    }

    private static MetricCollector<long> Collect(ServiceProvider provider, string instrument) =>
        new(provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), EngineTelemetry.Name, instrument);

    [Fact]
    public void A_stored_decision_is_counted_by_outcome_and_mode()
    {
        var (telemetry, provider) = Build();
        using var _ = provider;
        using var decisions = Collect(provider, EngineTelemetry.DecisionsName);

        telemetry.DecisionStored(DecisionOutcome.Shadowed, TradingMode.Shadow);

        var measured = decisions.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measured.Value.ShouldBe(1);
        measured.Tags["outcome"].ShouldBe("Shadowed");
        measured.Tags["mode"].ShouldBe("Shadow");
    }

    [Fact]
    public void A_risk_rejection_is_counted_twice_and_the_second_time_by_side()
    {
        var (telemetry, provider) = Build();
        using var _ = provider;
        using var decisions = Collect(provider, EngineTelemetry.DecisionsName);
        using var rejections = Collect(provider, EngineTelemetry.RiskRejectionsName);

        telemetry.DecisionStored(DecisionOutcome.RejectedByRisk, TradingMode.Paper, OrderSide.Sell);

        decisions.GetMeasurementSnapshot().ShouldHaveSingleItem().Tags["outcome"].ShouldBe("RejectedByRisk");

        var rejected = rejections.GetMeasurementSnapshot().ShouldHaveSingleItem();
        rejected.Tags["mode"].ShouldBe("Paper");
        rejected.Tags["side"].ShouldBe("Sell");
    }

    [Theory]
    [InlineData(DecisionOutcome.Executed)]
    [InlineData(DecisionOutcome.NotSized)]
    [InlineData(DecisionOutcome.Halted)]
    [InlineData(DecisionOutcome.AgentUnavailable)]
    public void Nothing_else_is_a_risk_rejection(DecisionOutcome outcome)
    {
        var (telemetry, provider) = Build();
        using var _ = provider;
        using var rejections = Collect(provider, EngineTelemetry.RiskRejectionsName);

        telemetry.DecisionStored(outcome, TradingMode.Paper, OrderSide.Buy);

        rejections.GetMeasurementSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Every_label_is_a_closed_set()
    {
        // No ticker, no reason, nothing the agents wrote: each of those is a time series per
        // value, and a reason is the one place a model's words could reach a monitoring system.
        var (telemetry, provider) = Build();
        using var _ = provider;
        using var decisions = Collect(provider, EngineTelemetry.DecisionsName);
        using var rejections = Collect(provider, EngineTelemetry.RiskRejectionsName);

        telemetry.DecisionStored(DecisionOutcome.RejectedByRisk, TradingMode.Paper, OrderSide.Buy);

        decisions.GetMeasurementSnapshot().Single().Tags.Keys.Order().ShouldBe(["mode", "outcome"]);
        rejections.GetMeasurementSnapshot().Single().Tags.Keys.Order().ShouldBe(["mode", "side"]);
    }
}
