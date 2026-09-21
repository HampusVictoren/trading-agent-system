namespace Engine.Application.Contracts;

using System.Text.Json;

public static class ContractSerialization
{
    /// <summary>
    /// The options every reader of the contract must use, production and tests alike.
    /// </summary>
    /// <remarks>
    /// <c>AllowOutOfOrderMetadataProperties</c> is the one that matters. System.Text.Json
    /// otherwise insists the type discriminator is the first property of the object and
    /// throws <see cref="NotSupportedException"/> when it is not. Pydantic writes "type"
    /// first only because the field happens to be declared first - an accident of field
    /// order, not a guarantee. contracts/examples/signal-discriminator-last.json exists to
    /// fail the contract test if this setting ever disappears.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        AllowOutOfOrderMetadataProperties = true
    };
}
