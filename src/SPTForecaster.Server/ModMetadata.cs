using SPTarkov.Server.Core.Models.Spt.Mod;

namespace SPTForecaster.Server;

public record ModMetadata : IModMetadata
{
    /// <summary>The same GUID the client plugin declares.</summary>
    public string ModGuid { get; init; } = "com.mybutthasarash.sptforecaster";

    public string Name { get; init; } = "SPTForecaster";

    public string Author { get; init; } = "JoelHauser";

    public List<string>? Contributors { get; init; }

    public SemanticVersioning.Version Version { get; init; } = new("0.1.0");

    /// <summary>A hard gate: outside this range the mod loads nothing and logs nothing.</summary>
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");

    public List<string>? Incompatibilities { get; init; }

    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }

    public string? Url { get; init; } = "https://github.com/JoelHauser/SPTForecaster";

    public string License { get; init; } = "MIT";

    public bool HasPrepatcher { get; init; }
}
