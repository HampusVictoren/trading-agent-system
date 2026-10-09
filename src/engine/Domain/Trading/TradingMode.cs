namespace Engine.Domain.Trading;

/// <summary>
/// What the engine does with a decision the risk gate has approved. Stored on every decision,
/// because a row made under one mode and a row made under another are different populations.
/// </summary>
/// <remarks>
/// <para>
/// There is no broker anywhere in this system, which is what decides what the three names mean
/// here rather than what they mean in general. Every order the engine has ever placed went to the
/// simulated portfolio in <c>trading.portfolios</c> - so <see cref="Paper"/> is not a new mode,
/// it is the name for what the engine already did. <see cref="Shadow"/> is the step below it:
/// the same analysis, the same sizing and the same risk gate, with the result written down and
/// the portfolio left alone.
/// </para>
/// <para>
/// <see cref="Live"/> is named so that it can be refused by name. Configuring it stops the engine
/// at startup, because there is nothing for it to send an order to - and when there is, whether
/// it should exist at all is the owner's decision rather than a configuration value's.
/// </para>
/// </remarks>
public enum TradingMode
{
    /// <summary>
    /// Analyse, size and gate as usual, record what would have been done, and place nothing: no
    /// order, no position change, no cash moved. The deterministic exits say what they would have
    /// sold and sell nothing.
    /// </summary>
    Shadow,

    /// <summary>Orders are executed against the simulated portfolio. Everything up to stage 7 ran like this.</summary>
    Paper,

    /// <summary>Orders to a real broker. There is none, so the engine refuses to start in this mode.</summary>
    Live
}
