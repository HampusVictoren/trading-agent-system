namespace Engine.Hosting.Options;

using Microsoft.Extensions.Options;

/// <summary>
/// The one risk rule that is about two settings at once, and therefore cannot be a data
/// annotation: a daily deployment limit must be at least the position limit.
/// </summary>
/// <remarks>
/// <para>
/// Below it, the position limit becomes unreachable - a single BUY could never be sized to its
/// full allowance - and the two numbers would be quietly fighting each other, with the engine
/// behaving as though the position limit were the smaller of the two.
/// </para>
/// <para>
/// <b>It exists because the domain's own guard fires too late.</b> <see cref="RiskPolicy"/> refuses
/// the combination in its constructor, which is right - the domain does not trust that
/// configuration was validated - but <c>RiskPolicy</c> is a singleton built by a factory, so it is
/// resolved the first time a cycle asks for it rather than at startup. The refusal would therefore
/// arrive as an "Unexpected failure" line from inside the worker's own catch, minutes after a
/// deploy, which is exactly the failure this project builds configuration to avoid. Here it reaches
/// <c>ValidateOnStart</c> and the engine refuses to come up at all.
/// </para>
/// </remarks>
public sealed class RiskPolicyOptionsValidator : IValidateOptions<RiskPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, RiskPolicyOptions options)
    {
        if (options.MaxDailyDeploymentPercentage < options.MaxPositionPercentage)
        {
            return ValidateOptionsResult.Fail(
                $"{RiskPolicyOptions.SectionName}:MaxDailyDeploymentPercentage is "
                + $"{options.MaxDailyDeploymentPercentage}, below "
                + $"{RiskPolicyOptions.SectionName}:MaxPositionPercentage of "
                + $"{options.MaxPositionPercentage}. A day that may deploy less than one position "
                + "makes the position limit unreachable.");
        }

        return ValidateOptionsResult.Success;
    }
}
