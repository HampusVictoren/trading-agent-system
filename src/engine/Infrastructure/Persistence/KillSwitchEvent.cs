namespace Engine.Infrastructure.Persistence;

/// <summary>
/// One flip of the kill switch: engaged or released, why, by whom and when. The switch's state is
/// the latest row.
/// </summary>
/// <remarks>
/// A log of flips rather than one row that is updated, because "who stopped trading on Tuesday,
/// and why" is a question an operator will ask, and an updated row has already forgotten the
/// answer. The table is append-only like the decisions it guards; releasing the switch is a new
/// row, not an edit.
///
/// The engine only reads it. Nothing in this codebase writes a flip - an operator does, with an
/// <c>INSERT</c> through psql - so every column the engine could get wrong is filled by the
/// database instead.
/// </remarks>
public sealed class KillSwitchEvent
{
    public long Id { get; private set; }

    public bool Engaged { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    /// <summary>The database's clock, so a flip is dated by the thing that stored it.</summary>
    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>The role that wrote the row - <c>postgres</c> for an operator in psql.</summary>
    public string ChangedBy { get; private set; } = string.Empty;
}
