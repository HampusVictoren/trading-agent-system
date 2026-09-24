namespace Engine.Domain.Outcomes;

using Engine.Domain.Signals;

/// <summary>
/// The parts of a stored decision an outcome is computed from. It is not the decision row:
/// measuring needs four values, and taking the whole row would tie this to how it is stored.
/// </summary>
/// <param name="Stance">Which way the view pointed. It decides what counts as a hit.</param>
/// <param name="ReferencePrice">
/// The price the view was formed on, which is what a return is measured from. Not the close
/// of the signal's day: the decision was made at a moment, and that moment's price is the
/// one the engine would have paid.
/// </param>
/// <param name="SignalDate">The day the signal was made, in the market's own terms.</param>
/// <param name="Horizon">How far ahead to look, in the unit the horizon was expressed in.</param>
public sealed record SignalToMeasure(
    Stance Stance,
    decimal ReferencePrice,
    DateOnly SignalDate,
    Horizon Horizon);
