namespace Engine.Application.Persistence;

using Engine.Application.UseCases;
using Engine.Domain.Screening;
using Engine.Domain.Signals;
using Engine.Domain.Trading;
using Engine.Domain.ValueObjects;

/// <summary>
/// One analysis cycle, exactly as the engine saw it: what it asked, what came back, and what
/// it did about it. Append-only in practice - nothing updates a decision once it is written.
/// </summary>
/// <remarks>
/// <para>
/// The engine stores what reaches the engine. The agent service's own working - the fact
/// sheet each step was given and what the intermediate steps answered - is stored on the
/// Python side against the same correlation id, because it is that service's data and
/// widening the contract with fields the engine never reads would make the engine the owner
/// of somebody else's internals. Attributing a result to one agent is then a join across two
/// databases, done when the question is asked rather than on every cycle.
/// </para>
/// <para>
/// The prices are plain numbers with a currency beside them rather than <see cref="Money"/>.
/// This is a log row, not a domain object: nothing computes from it inside the engine, and a
/// report reads the columns directly.
/// </para>
/// </remarks>
public sealed class DecisionRecord
{
    /// <summary>Set by the database. The order it hands out is the order decisions happened in.</summary>
    public long Id { get; private set; }

    /// <summary>
    /// The id the cycle ran under, shared with the agent service's logs and its stored fact
    /// sheet. Unique, so a call that is somehow made twice cannot become two decisions.
    /// </summary>
    public required string CorrelationId { get; init; }

    public required Guid PortfolioId { get; init; }

    /// <summary>What was asked about - not what the answer was about. An answer naming another
    /// instrument is an outcome, and the row has to say which question produced it.</summary>
    public required Ticker Symbol { get; init; }

    public required string TeamId { get; init; }

    /// <summary>
    /// Why this instrument was analysed at all: the screen ranked it, or the portfolio holds it.
    /// </summary>
    /// <remarks>
    /// Two populations that must not be averaged together. A holding is analysed whatever the
    /// ranking says about it, and a candidate is there precisely because the ranking put it near
    /// the top - so a hit rate over both is measuring the screen's selection and the portfolio's
    /// inertia as one number. It cannot be derived afterwards either: whether an instrument was on
    /// a shortlist months ago is a fact about that shortlist, and a position that has since been
    /// sold leaves nothing to infer from.
    /// </remarks>
    public required SelectionSource Selection { get; init; }

    /// <summary>
    /// Whether an approved order from this decision could reach the portfolio: Paper, or Shadow,
    /// where nothing is placed.
    /// </summary>
    /// <remarks>
    /// Two populations again, and for a less obvious reason than "one of them traded". A Shadow
    /// engine never builds a position, so it never analyses a holding it bought and never tells the
    /// agents it owns anything - the questions it asks differ from Paper's, not only what it does
    /// with the answers. Every row written before stage 7 is Paper, because that is what the
    /// engine did.
    /// </remarks>
    public required TradingMode TradingMode { get; init; }

    /// <summary>The <c>as_of</c> the engine sent, from its injected clock.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    // What the engine was willing to spend at the moment it asked. No agent reads these, but
    // a decision is only interpretable next to the room it was made in.
    public required decimal AvailableRiskBudget { get; init; }
    public required decimal MaxPositionPct { get; init; }
    public decimal? ExistingQuantity { get; init; }
    public decimal? ExistingAveragePrice { get; init; }

    // The answer, when there was one. Null all the way across means the call failed before a
    // signal existed, which is itself worth measuring.
    public string? TeamVersion { get; init; }
    public int? Revisions { get; init; }
    public Stance? Stance { get; init; }
    public double? Conviction { get; init; }
    public string? Thesis { get; init; }
    public string[] KeyRisks { get; init; } = [];
    public int? HorizonDays { get; init; }
    public decimal? ReferencePrice { get; init; }
    public string? ReferenceCurrency { get; init; }
    public DateTimeOffset? QuoteAsOf { get; init; }

    // What the engine did about it.
    public required DecisionOutcome Outcome { get; init; }
    public string? OutcomeReason { get; init; }

    /// <summary>The ledger line, when the decision produced one. Only an executed buy does.</summary>
    public Guid? OrderId { get; init; }

    /// <summary>
    /// What a buy Shadow mode did not place would have cost, in the account's currency. Null on
    /// every other row.
    /// </summary>
    /// <remarks>
    /// The daily deployment limit is a sum over the ledger, and Shadow writes no ledger. Without
    /// this, every shadow buy of a day was sized against the whole day's budget, so ten of them
    /// "would have bought" up to ten times what the limit lets Paper buy. Shadow adds these to
    /// what the ledger says the day spent, and the limit binds as it would have. Cash and position
    /// headroom are still not consumed. A shadow engine holds nothing, so that would take a
    /// second portfolio, and at today's balances the daily limit binds long before cash does.
    /// </remarks>
    public decimal? ShadowCost { get; init; }

    /// <summary>Set by the database, so the row's own clock is the one that ordered it.</summary>
    public DateTimeOffset RecordedAt { get; private set; }
}
