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
        ["Trading:Tickers:0"] = "AAPL",
        ["Trading:CycleIntervalSeconds"] = "15",
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
