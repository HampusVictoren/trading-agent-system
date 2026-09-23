namespace Engine.Hosting.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Where the engine's own schema lives. The connection string carries the engine_svc
/// password, so it is <em>not</em> in appsettings.json: locally it lives in the user secrets
/// store, outside the repository, and elsewhere it comes from the environment. That is the
/// same rule AgentService:ApiKey follows.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Required, with no default. An engine that cannot reach its database must refuse to
    /// start rather than run a cycle whose decision is then lost - which is the one failure
    /// this stage exists to make impossible.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;
}
