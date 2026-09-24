using Engine.Hosting;
using Engine.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Engine.Tests.Hosting.Options;

public class EngineOptionsTests
{
    private static readonly Dictionary<string, string?> ValidSettings = new()
    {
        ["AgentService:BaseUrl"] = "http://127.0.0.1:8000",
        ["AgentService:RequestTimeoutSeconds"] = "30",
        ["AgentService:ApiKey"] = "a-test-key",
        ["RiskPolicy:MaxPositionPercentage"] = "0.05",
        ["RiskPolicy:CashBufferPct"] = "0.10",
        ["RiskPolicy:MaxQuoteAgeSeconds"] = "300",
        ["Trading:Tickers:0"] = "AAPL",
        ["Trading:CycleIntervalSeconds"] = "15",
        ["Trading:TeamId"] = "default",
        ["Trading:OpeningBalanceUsd"] = "10000",
        ["Database:ConnectionString"] = "Host=127.0.0.1;Database=tradingdb;Username=engine_svc",
        ["Outcome:CommissionBps"] = "1",
        ["Outcome:SpreadBps"] = "2",
        ["Outcome:HoldBandPct"] = "0.02",
        ["Outcome:BenchmarkSymbol"] = "SPY",
        ["Outcome:FixedHorizonTradingDays:0"] = "1",
        ["Outcome:FixedHorizonTradingDays:1"] = "5",
        ["Outcome:FixedHorizonTradingDays:2"] = "20",
    };

    /// <summary>Resolves an options instance from valid settings, with the given keys changed or removed.</summary>
    private static T Resolve<T>(params (string Key, string? Value)[] changes)
        where T : class
    {
        var settings = new Dictionary<string, string?>(ValidSettings);
        foreach (var (key, value) in changes)
        {
            if (value is null)
            {
                settings.Remove(key);
            }
            else
            {
                settings[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var provider = new ServiceCollection().AddEngineOptions(configuration).BuildServiceProvider();

        return provider.GetRequiredService<IOptions<T>>().Value;
    }

    [Fact]
    public void Valid_configuration_binds_every_section()
    {
        Resolve<AgentServiceOptions>().BaseUrl.ShouldBe("http://127.0.0.1:8000");
        Resolve<AgentServiceOptions>().RequestTimeoutSeconds.ShouldBe(30);
        Resolve<RiskPolicyOptions>().MaxPositionPercentage.ShouldBe(0.05m);
        Resolve<TradingOptions>().Tickers.ShouldBe(["AAPL"]);
        Resolve<TradingOptions>().CycleInterval.ShouldBe(TimeSpan.FromSeconds(15));
        Resolve<TradingOptions>().TeamId.ShouldBe("default");
        Resolve<TradingOptions>().OpeningBalanceUsd.ShouldBe(10_000m);
    }

    [Fact]
    public void A_missing_team_is_rejected()
    {
        // Which team runs is configuration, and there is no sensible default: a team the
        // agent service does not know answers 422, and guessing one would silently compare
        // outcomes from a setup nobody chose.
        Should.Throw<OptionsValidationException>(() => Resolve<TradingOptions>(("Trading:TeamId", null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0")]
    [InlineData("-0.1")]
    public void A_cash_buffer_that_is_not_a_share_is_rejected(string? value)
    {
        // A buffer of 1 reserves the whole portfolio and nothing could ever be bought. The
        // null case is why the setting is nullable: zero is a legitimate buffer, so a missing
        // value would otherwise bind to zero and look deliberate.
        Should.Throw<OptionsValidationException>(
            () => Resolve<RiskPolicyOptions>(("RiskPolicy:CashBufferPct", value)));
    }

    [Fact]
    public void The_risk_limits_map_onto_the_domains_own_policy()
    {
        // Stage 3 resolves this from the container; the mapping should not be where it breaks.
        var policy = Resolve<RiskPolicyOptions>().ToRiskPolicy();

        policy.MaxPositionPct.ShouldBe(0.05m);
        policy.CashBufferPct.ShouldBe(0.10m);
        policy.MaxQuoteAge.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("3601")]
    public void A_quote_age_limit_outside_the_allowed_range_is_rejected(string? value)
    {
        Should.Throw<OptionsValidationException>(
            () => Resolve<RiskPolicyOptions>(("RiskPolicy:MaxQuoteAgeSeconds", value)));
    }

    [Fact]
    public void A_cash_buffer_of_zero_is_a_deliberate_choice_and_is_accepted()
    {
        Resolve<RiskPolicyOptions>(("RiskPolicy:CashBufferPct", "0")).ToRiskPolicy()
            .CashBufferPct.ShouldBe(0m);
    }

    [Fact]
    public void A_missing_api_key_is_rejected()
    {
        // The agent service refuses an analysis without it, so starting without one only
        // buys a cycle of 401s. It is a secret, so it comes from user secrets or the
        // environment rather than appsettings.json - which is exactly why it can be absent.
        Should.Throw<OptionsValidationException>(() => Resolve<AgentServiceOptions>(("AgentService:ApiKey", null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-100")]
    public void An_opening_balance_that_is_not_a_portfolio_is_rejected(string? value)
    {
        // The number is used once in the account's life and then sets the size of every trade
        // that follows, because the position cap is a share of the portfolio's value. A
        // missing setting binds to zero, which the lower bound is there to catch.
        Should.Throw<OptionsValidationException>(
            () => Resolve<TradingOptions>(("Trading:OpeningBalanceUsd", value)));
    }

    [Fact]
    public void The_scoring_figures_map_onto_the_domains_own_policy()
    {
        var options = Resolve<OutcomeOptions>();

        options.ToOutcomePolicy().RoundTripFraction.ShouldBe(0.0006m);
        options.BenchmarkSymbol.ShouldBe("SPY");
        options.FixedHorizons().Select(horizon => horizon.Days).ShouldBe([1, 5, 20]);
    }

    [Theory]
    [InlineData("Outcome:CommissionBps")]
    [InlineData("Outcome:SpreadBps")]
    public void A_missing_cost_is_rejected_although_zero_would_be_legitimate(string key)
    {
        // Zero commission is a real arrangement, so "absent" and "none" would otherwise be
        // the same value - the same reason CashBufferPct is nullable and required.
        Should.Throw<OptionsValidationException>(() => Resolve<OutcomeOptions>((key, null)));

        Resolve<OutcomeOptions>((key, "0")).ToOutcomePolicy().ShouldNotBeNull();
    }

    [Fact]
    public void A_missing_hold_band_is_rejected()
    {
        // A band of zero means a HOLD is only right when the deviation is exactly nothing,
        // so the lower bound catches an absent setting without needing a nullable.
        Should.Throw<OptionsValidationException>(() => Resolve<OutcomeOptions>(("Outcome:HoldBandPct", null)));
    }

    [Fact]
    public void A_benchmark_that_is_not_a_symbol_is_rejected()
    {
        // It would otherwise surface as a 422 from the agent service, at midnight, for a
        // quote nobody could explain.
        var exception = Should.Throw<OptionsValidationException>(
            () => Resolve<OutcomeOptions>(("Outcome:BenchmarkSymbol", "../etc")));

        exception.Message.ShouldContain("BenchmarkSymbol");
    }

    [Fact]
    public void A_horizon_below_one_day_is_rejected()
    {
        Should.Throw<OptionsValidationException>(
            () => Resolve<OutcomeOptions>(("Outcome:FixedHorizonTradingDays:0", "0")));
    }

    [Fact]
    public void A_repeated_horizon_is_rejected()
    {
        // Two rows for one measurement, which the outcomes table will refuse anyway. Better
        // at startup than when the first horizon comes due.
        var exception = Should.Throw<OptionsValidationException>(
            () => Resolve<OutcomeOptions>(("Outcome:FixedHorizonTradingDays:2", "5")));

        exception.Message.ShouldContain("repeats");
    }

    [Fact]
    public void A_missing_horizon_list_is_rejected()
    {
        Should.Throw<OptionsValidationException>(
            () => Resolve<OutcomeOptions>(
                ("Outcome:FixedHorizonTradingDays:0", null),
                ("Outcome:FixedHorizonTradingDays:1", null),
                ("Outcome:FixedHorizonTradingDays:2", null)));
    }

    [Fact]
    public void A_missing_connection_string_is_rejected()
    {
        // From stage 4 a cycle that cannot be stored is a cycle whose evidence is lost, and
        // losing evidence quietly is worse than not running. Like the agent service's key it
        // is a secret, so it comes from user secrets or the environment - which is exactly
        // why it can be absent, and why its absence has to stop the service.
        Should.Throw<OptionsValidationException>(
            () => Resolve<DatabaseOptions>(("Database:ConnectionString", null)));
    }

    [Fact]
    public void A_missing_agent_service_url_is_rejected()
    {
        // A mistyped section name must stop the service, not fall back on a guessed default.
        Should.Throw<OptionsValidationException>(() => Resolve<AgentServiceOptions>(("AgentService:BaseUrl", null)));
    }

    [Fact]
    public void An_agent_service_url_that_is_not_a_url_is_rejected()
    {
        Should.Throw<OptionsValidationException>(() => Resolve<AgentServiceOptions>(("AgentService:BaseUrl", "127.0.0.1:8000")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("601")]
    public void A_request_timeout_outside_the_allowed_range_is_rejected(string seconds)
    {
        // Without a bound, a call that never answers blocks the worker for as long as it likes.
        Should.Throw<OptionsValidationException>(() => Resolve<AgentServiceOptions>(("AgentService:RequestTimeoutSeconds", seconds)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    public void A_position_limit_outside_the_allowed_range_is_rejected(string percentage)
    {
        Should.Throw<OptionsValidationException>(() => Resolve<RiskPolicyOptions>(("RiskPolicy:MaxPositionPercentage", percentage)));
    }

    [Fact]
    public void A_missing_position_limit_is_rejected()
    {
        // The risk limit is the one number that must never be guessed.
        Should.Throw<OptionsValidationException>(() => Resolve<RiskPolicyOptions>(("RiskPolicy:MaxPositionPercentage", null)));
    }

    [Fact]
    public void A_missing_ticker_list_is_rejected()
    {
        Should.Throw<OptionsValidationException>(() => Resolve<TradingOptions>(("Trading:Tickers:0", null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_ticker_the_domain_would_refuse_is_rejected(string ticker)
    {
        var exception = Should.Throw<OptionsValidationException>(() => Resolve<TradingOptions>(("Trading:Tickers:0", ticker)));

        exception.Message.ShouldContain("Trading:Tickers");
    }

    [Fact]
    public void A_cycle_interval_of_zero_is_rejected()
    {
        Should.Throw<OptionsValidationException>(() => Resolve<TradingOptions>(("Trading:CycleIntervalSeconds", "0")));
    }
}
