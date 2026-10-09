namespace Engine.Application.UseCases;

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
/// A mode that is missing reads as Shadow. Validation at startup makes that unreachable in a
/// running engine; the fallback is for anything that constructs the options by hand, and it goes
/// the safe way because the other direction places orders.
/// </para>
/// </remarks>
public sealed class OrderGate
{
    public OrderGate(IOptions<TradingOptions> trading)
    {
        Mode = trading.Value.Mode ?? TradingMode.Shadow;
    }

    /// <summary>The mode every decision this engine records was made under.</summary>
    public TradingMode Mode { get; }

    /// <summary>Asked immediately before an order would be placed, and never earlier.</summary>
    public Task<OrderPermission> AskAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<OrderPermission>(Mode switch
        {
            TradingMode.Paper => new OrderPermission.Granted(),
            TradingMode.Shadow => new OrderPermission.ShadowOnly(),

            // Unreachable: TradingOptionsValidator refuses Live at startup. Throwing rather than
            // answering Granted, because the wrong answer here sends an order nobody can receive.
            _ => throw new InvalidOperationException(
                $"Trading mode {Mode} has no broker behind it, so nothing may be placed.")
        });
}
