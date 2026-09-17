namespace Engine.Domain.Exceptions;

public class RiskViolationException : Exception
{
    public RiskViolationException(string message) : base(message) { }
}
