namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;
using Engine.Domain.Trading;

/// <summary>What the worker trades, and how often it looks.</summary>
public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    /// <summary>
    /// Every instrument the screen may consider. The engine owns what it trades, so the
    /// universe is sent with each screen rather than held by the agent service - which also
    /// makes that endpoint a pure function of its input, and a stored shortlist reproducible
    /// from the request that produced it.
    /// </summary>
    /// <remarks>
    /// The cap mirrors <c>maxItems</c> on the universe in contracts/screen.schema.json, because
    /// a request is a unit of work with a timeout rather than a bulk load. A symbol in here
    /// that cannot be fetched does not fail the screen: it comes back named in the rejections,
    /// which is how a universe that is quietly rotting becomes visible.
    /// </remarks>
    [Required]
    [MinLength(1)]
    [MaxLength(100)]
    public string[] Universe { get; init; } = [];

    /// <summary>
    /// How many of the ranked instruments to actually analyse. This is the setting that bounds
    /// a cycle's cost: everything upstream of it is arithmetic, and everything downstream is
    /// three LLM calls per instrument.
    /// </summary>
    /// <remarks>The cap mirrors <c>maximum</c> on <c>limit</c> in the same contract.</remarks>
    [Range(1, 50)]
    public int ShortlistSize { get; init; }

    /// <summary>
    /// The liquidity floor the screen filters on, as typical daily turnover in the account's
    /// currency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable and <c>[Required]</c> rather than a plain <c>decimal</c>, because zero is a
    /// legitimate value here - it means "no floor" - so the usual trick of letting a missing
    /// setting bind to zero and fall outside the range would not catch an absent key.
    /// </para>
    /// <para>
    /// It is a data-quality guard rather than a liquidity constraint at this account size. A
    /// position is a few percent of a hundred thousand kronor, so no Stockholm large cap is
    /// anywhere near too thin to buy; what the floor actually catches is a symbol whose
    /// listing has gone inactive or whose data has gone stale. It has to be revisited if the
    /// account ever grows enough for the order size to matter.
    /// </para>
    /// </remarks>
    [Required]
    [Range(typeof(decimal), "0", "1000000000")]
    public decimal? MinDollarVolume { get; init; }

    /// <summary>
    /// How long to wait between cycles. Minutes rather than seconds, and the reason is that the
    /// number has stopped meaning what it used to.
    /// </summary>
    /// <remarks>
    /// It no longer paces the analyses at all: an instrument is screened once a trading day and
    /// analysed once a trading day, so a shorter interval buys nothing but a faster answer to
    /// "has anything changed?". What it does pace is the deterministic exits, which run every
    /// cycle and are the half that has to be timely - a stop-loss checked once an hour is a
    /// stop-loss with an hour of slack in it.
    /// </remarks>
    [Range(1, 1440)]
    public int CycleIntervalMinutes { get; init; }

    /// <summary>
    /// Which team setup the agent service should run. Configuration rather than code, because
    /// that is what makes the experiment cycle an experiment: change the team, let it run, and
    /// compare outcomes per team_version in stage 4. An id the service does not know is a 422.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string TeamId { get; init; } = string.Empty;

    /// <summary>
    /// What the account starts with, used exactly once: the first time the engine runs against
    /// an empty database. It is configuration rather than a literal in the worker because the
    /// position cap is a share of the portfolio's value, so this one number quietly sets the
    /// size of every trade that follows - and every measurement made from them.
    /// </summary>
    /// <remarks>
    /// The lower bound is what makes a missing value fail: an absent setting binds to zero,
    /// and zero is not a portfolio.
    /// </remarks>
    [Range(typeof(decimal), "1", "100000000")]
    public decimal OpeningBalance { get; init; }

    /// <summary>
    /// Whether an approved decision becomes an order. <see cref="TradingMode.Shadow"/> records it
    /// and places nothing; <see cref="TradingMode.Paper"/> executes it against the simulated
    /// portfolio; <see cref="TradingMode.Live"/> is refused, because there is no broker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable and <c>[Required]</c>, like the other settings whose first value is a legitimate
    /// one: <c>Shadow</c> is the enum's zero, so a missing key would otherwise bind to it and look
    /// deliberate. A setting that decides whether the engine trades should be one somebody wrote.
    /// </para>
    /// <para>
    /// Read once, at startup. Changing it is a restart, on purpose - the thing that stops a
    /// running engine without one is the kill switch, not this.
    /// </para>
    /// </remarks>
    [Required]
    public TradingMode? Mode { get; init; }

    public TimeSpan CycleInterval => TimeSpan.FromMinutes(CycleIntervalMinutes);
}
