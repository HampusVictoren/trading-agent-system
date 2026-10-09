namespace Engine.Application.UseCases;

using Engine.Domain.Aggregates.Portfolio;
using Engine.Domain.ValueObjects;

/// <summary>
/// Which shadow exits have already been reported, so each is said once rather than every cycle.
/// </summary>
/// <remarks>
/// <para>
/// A Paper exit sells, and the position is gone. A Shadow exit sells nothing, so the same
/// position trips the same rule on the next cycle, and the one after that. Logging it each time
/// was a line every fifteen minutes saying something already said. The log reads as a list of
/// changes instead: an exit is reported when it starts firing, again if what it would sell
/// changes, and again if it stops firing and later starts again.
/// </para>
/// <para>
/// A singleton, because the use case is resolved per pass and this has to outlive one. It is
/// process memory on purpose: a restart reports every firing exit once more, which is the right
/// thing for whoever reads the log from that restart. Comparing whole passes rather than keeping
/// a growing set is what lets an exit that stopped firing be reported again.
/// </para>
/// </remarks>
public sealed class ShadowExitNotices
{
    private readonly Lock _lock = new();
    private HashSet<(Ticker Ticker, OrderTrigger Trigger, decimal Quantity)> _lastPass = [];
    private HashSet<(Ticker Ticker, OrderTrigger Trigger, decimal Quantity)> _thisPass = [];

    /// <summary>Starts a pass over the portfolio. Whatever the previous pass reported is kept to compare against.</summary>
    public void BeginPass()
    {
        lock (_lock)
            _thisPass = [];
    }

    /// <summary>Records that this exit fired in Shadow, and answers whether it is news.</summary>
    /// <returns>True when the previous pass did not report the same exit at the same quantity.</returns>
    public bool Fired(Ticker ticker, OrderTrigger trigger, decimal quantity)
    {
        lock (_lock)
        {
            var key = (ticker, trigger, quantity);
            _thisPass.Add(key);
            return !_lastPass.Contains(key);
        }
    }

    /// <summary>Ends the pass: what fired in it is what the next pass compares against.</summary>
    public void EndPass()
    {
        lock (_lock)
            _lastPass = _thisPass;
    }
}
