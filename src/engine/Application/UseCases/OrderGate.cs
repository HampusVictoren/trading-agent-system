namespace Engine.Application.UseCases;

using Engine.Application.Persistence;
using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.Trading;
using Engine.Hosting.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// What the gate answered when asked whether an approved order may be placed now.
/// </summary>
/// <remarks>
/// Closed, like <see cref="TradeDecisionResult"/>: only the cases nested here can derive from it,
/// so a caller that handles all of them handles everything the gate can say.
/// </remarks>
public abstract record OrderPermission
{
    private OrderPermission() { }

    /// <summary>Place it.</summary>
    public sealed record Granted : OrderPermission;

    /// <summary>The engine is in Shadow mode: record what would have been done, and do none of it.</summary>
    public sealed record ShadowOnly : OrderPermission;

    /// <summary>The kill switch is engaged, or could not be read, and this is a buy. Place nothing.</summary>
    public sealed record Halted(string Reason) : OrderPermission;
}

/// <summary>
/// The last thing between an approved order and the portfolio. Both paths that place orders ask
/// it - an analysis that ended in a trade, and the deterministic exits - and neither touches the
/// portfolio unless it answers <see cref="OrderPermission.Granted"/>.
/// </summary>
/// <remarks>
/// <para>
/// It sits after the risk gate rather than in front of the whole cycle, because Shadow is meant to
/// be the real thing with the last step removed. A mode that skipped the analysis would measure
/// nothing; one that skipped sizing would record a decision that could not say how big it would
/// have been.
/// </para>
/// <para>
/// <b>The kill switch stops new buys, and only those.</b> A sale reduces exposure, which is what
/// someone pulling the switch wants more of, not less: a stop-loss that waited for the release
/// would be a stop-loss that did not fire. So a sale is granted without the switch being read at
/// all, and that holds for the agents' SELLs as much as for the exits. Decided by the owner on
/// 2026-10-09.
/// </para>
/// <para>
/// For a buy the switch is read here, on every ask, rather than trusted from the start of the
/// cycle. A cycle is minutes of LLM calls, and an operator who pulls the switch during one means
/// the next buy, not the next cycle. It fails closed: a switch that cannot be read reads as
/// engaged, so a buy is never placed on "I could not tell". Shadow does not read it, because
/// Shadow places nothing to stop.
/// </para>
/// <para>
/// A mode that is missing reads as Shadow - see <see cref="TradingOptions.EffectiveMode"/>, which is
/// where that fallback lives.
/// </para>
/// </remarks>
public sealed class OrderGate
{
    private readonly IKillSwitch _killSwitch;

    public OrderGate(IOptions<TradingOptions> trading, IKillSwitch killSwitch)
    {
        Mode = trading.Value.EffectiveMode;
        _killSwitch = killSwitch;
    }

    /// <summary>The mode every decision this engine records was made under.</summary>
    public TradingMode Mode { get; }

    /// <summary>Asked immediately before an order would be placed, and never earlier.</summary>
    /// <param name="side">
    /// Which way the order goes. Only a buy can be halted, so only a buy reads the switch.
    /// </param>
    public async Task<OrderPermission> AskAsync(OrderSide side, CancellationToken cancellationToken = default)
    {
        switch (Mode)
        {
            case TradingMode.Shadow:
                return new OrderPermission.ShadowOnly();

            case TradingMode.Paper when side == OrderSide.Sell:
                return new OrderPermission.Granted();

            case TradingMode.Paper:
                var state = await _killSwitch.ReadAsync(cancellationToken);

                return state.Engaged
                    ? new OrderPermission.Halted($"Kill switch engaged: {state.Reason}")
                    : new OrderPermission.Granted();

            // Unreachable: TradingOptionsValidator refuses Live at startup. Throwing rather than
            // answering Granted, because the wrong answer here sends an order nobody can receive.
            default:
                throw new InvalidOperationException(
                    $"Trading mode {Mode} has no broker behind it, so nothing may be placed.");
        }
    }
}
