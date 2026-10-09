using Engine.Application.Contracts;
using Engine.Application.Interfaces;
using Engine.Application.UseCases;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Risk;
using Engine.Domain.ValueObjects;
using Engine.Hosting.Options;
using Engine.Hosting.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Engine.Tests.Hosting;

/// <summary>
/// A portfolio of many holdings, priced one quote at a time against an agent service that takes
/// its whole timeout to answer each one, must not leave the heartbeat's deadline behind.
/// </summary>
/// <remarks>
/// The real <see cref="QuoteReader"/> and the real <see cref="CycleHeartbeat"/> writing a real
/// file, with a clock the fake agent service moves forward by one full call - both attempts and
/// the pause, 245 s at the shipped 120 s timeout - every time it is asked for a quote. Eight
/// holdings are 1 960 s of quotes, well past the 1 390 s one beat buys, so a heartbeat that only
/// beat around the loop would expire inside it.
/// </remarks>
public sealed class QuoteHeartbeatTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneCall = TimeSpan.FromSeconds((2 * 120) + 5);

    private readonly string _directory = Directory.CreateTempSubdirectory("heartbeat-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly string[] Holdings =
        ["ABB.ST", "ALFA.ST", "AZN.ST", "BOL.ST", "ERIC-B.ST", "EVO.ST", "HEXA-B.ST", "VOLV-B.ST"];

    [Fact]
    public async Task Pricing_every_holding_against_a_hanging_agent_service_keeps_the_engine_healthy()
    {
        var path = Path.Combine(_directory, "engine.heartbeat");
        var clock = new MovingClock(Start);
        var heartbeat = new CycleHeartbeat(
            Microsoft.Extensions.Options.Options.Create(new HealthOptions { HeartbeatFile = path }),
            Microsoft.Extensions.Options.Options.Create(new TradingOptions { CycleIntervalMinutes = 15 }),
            Microsoft.Extensions.Options.Options.Create(new AgentServiceOptions { RequestTimeoutSeconds = 120 }),
            clock,
            NullLogger<CycleHeartbeat>.Instance);

        var portfolio = new Portfolio(new Money(100_000m, Money.DefaultCurrency));
        foreach (var symbol in Holdings)
            portfolio.ExecuteBuy(new Ticker(symbol), 1m, new Money(100m, Money.DefaultCurrency));

        // What the healthcheck would have said at the moment each call came back: healthy while the
        // deadline in the file is still ahead of the clock.
        var healthyAtEachAnswer = new List<bool>();
        var agents = Substitute.For<IAgentClient>();
        agents.GetQuoteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<QuoteDto?>>(_ =>
            {
                clock.Now += OneCall;
                healthyAtEachAnswer.Add(DeadlineIn(path) > clock.Now);
                return Task.FromException<QuoteDto?>(new AgentServiceUnavailableException("timed out"));
            });

        // The step's own beat, as the worker writes one before the exits.
        heartbeat.Beat();

        var quotes = new QuoteReader(
            agents, RiskPolicyFor(), heartbeat, NullLogger<QuoteReader>.Instance);

        await quotes.ForHoldingsAsync(portfolio, clock.Now, "c-1", cancellationToken: TestContext.Current.CancellationToken);

        healthyAtEachAnswer.Count.ShouldBe(Holdings.Length);
        healthyAtEachAnswer.ShouldAllBe(healthy => healthy);
        DeadlineIn(path).ShouldBe(clock.Now + heartbeat.Lease);
    }

    [Fact]
    public async Task Every_quote_beats_whether_or_not_it_was_answered()
    {
        var progress = Substitute.For<ICycleProgress>();
        var agents = Substitute.For<IAgentClient>();
        agents.GetQuoteAsync("AZN.ST", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<QuoteDto?>>(_ => throw new AgentServiceUnavailableException("503"));

        var portfolio = new Portfolio(new Money(100_000m, Money.DefaultCurrency));
        foreach (var symbol in Holdings[..3])
            portfolio.ExecuteBuy(new Ticker(symbol), 1m, new Money(100m, Money.DefaultCurrency));

        await new QuoteReader(agents, RiskPolicyFor(), progress, NullLogger<QuoteReader>.Instance)
            .ForHoldingsAsync(portfolio, Start, "c-1", cancellationToken: TestContext.Current.CancellationToken);

        progress.Received(3).Beat();
    }

    private static DateTimeOffset DeadlineIn(string path) =>
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture));

    private static RiskPolicy RiskPolicyFor() =>
        new RiskPolicyOptions
        {
            MaxPositionPercentage = 0.05m,
            CashBufferPct = 0.10m,
            MinHoldingPeriodDays = 3,
            StopLossPercentage = 0.10m,
            MaxDailyDeploymentPercentage = 0.20m,
            MaxQuoteAgeSeconds = 300,
        }.ToRiskPolicy();
}
