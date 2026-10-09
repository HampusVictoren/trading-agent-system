namespace Engine.Application.UseCases;

using Engine.Application.Persistence;
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

    /// <summary>The kill switch is engaged, or could not be read. Place nothing.</summary>
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
/// The kill switch is read here, on every ask, rather than trusted from the start of the cycle. A
/// cycle is minutes of LLM calls, and an operator who pulls the switch during one means the next
/// order, not the next cycle. Shadow does not read it, because Shadow places nothing to stop -
/// the worker reads it separately, to stop spending on analyses.
/// </para>
/// <para>
/// A mode that is missing reads as Shadow. Validation at startup makes that unreachable in a
/// running engine; the fallback is for anything that constructs the options by hand, and it goes
/// the safe way because the other direction places orders.
/// </para>
/// </remarks>
public sealed class OrderGate
{
    private readonly IKillSwitch _killSwitch;

    public OrderGate(IOptions<TradingOptions> trading, IKillSwitch killSwitch)
    {
        Mode = trading.Value.Mode ?? TradingMode.Shadow;
        _killSwitch = killSwitch;
    }

    /// <summary>The mode every decision this engine records was made under.</summary>
    public TradingMode Mode { get; }

    /// <summary>Asked immediately before an order would be placed, and never earlier.</summary>
    public async Task<OrderPermission> AskAsync(CancellationToken cancellationToken = default)
    {
        switch (Mode)
        {
            case TradingMode.Shadow:
                return new OrderPermission.ShadowOnly();

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
