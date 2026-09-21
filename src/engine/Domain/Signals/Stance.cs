namespace Engine.Domain.Signals;

/// <summary>
/// The direction the agents argue for. The engine decides whether anything is traded; a
/// stance is an opinion, not an order.
/// </summary>
public enum Stance
{
    Buy,
    Sell,
    Hold
}
