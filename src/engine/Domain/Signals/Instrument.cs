namespace Engine.Domain.Signals;

using Engine.Domain.ValueObjects;

/// <summary>
/// What the engine may be asked to trade. A closed hierarchy: the private constructor keeps
/// every variant in this file, so the compiler can see the whole set. Only equity exists
/// today; a derivative becomes a new variant here rather than a breaking change on the wire.
/// </summary>
public abstract record Instrument
{
    private Instrument() { }

    /// <summary>How the instrument is written in a log or a database row.</summary>
    public abstract string Describe();

    public sealed record Equity(Ticker Ticker) : Instrument
    {
        public override string Describe() => Ticker.Value;
    }
}
