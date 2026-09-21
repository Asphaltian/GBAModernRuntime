using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibRecomp.Mods;

/// <summary>
/// A mod's <c>mod.json</c>. Each key is named after its property in snake_case, so
/// <see cref="DisplayName"/> becomes <c>display_name</c>.
/// </summary>
public sealed record ModManifest
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>What other mods call this one. If your mod has code, its assembly has to be <c>id.dll</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The <see cref="GameEntry.ModGameId"/> of the game the mod is for.</summary>
    public required string GameId { get; init; }

    public required string DisplayName { get; init; }

    public required SemanticVersion Version { get; init; }

    public required IReadOnlyList<string> Authors { get; init; }

    /// <summary>The oldest <see cref="Recomp.ProjectVersion"/> the mod works with.</summary>
    public required SemanticVersion MinimumRecompVersion { get; init; }

    public string ShortDescription { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>Whether the mod starts out on when the player first adds it. It does, unless you set this to false.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Mods that have to be loaded before this one, each as <c>id</c>, or <c>id:version</c> if you need a minimum version.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Mods that load before this one if the player has them. Their versions aren't checked.</summary>
    public IReadOnlyList<string> OptionalDependencies { get; init; } = [];

    /// <summary>The settings the player can change.</summary>
    public ConfigSchema ConfigSchema { get; init; } = new();

    /// <summary>Reads a <c>mod.json</c>.</summary>
    /// <exception cref="JsonException">It isn't a valid <c>mod.json</c>.</exception>
    /// <exception cref="FormatException">A dependency's version isn't a valid version number.</exception>
    public static ModManifest Parse(byte[] json)
    {
        var manifest = JsonSerializer.Deserialize<ModManifest>(json, Options) ?? throw new JsonException("The manifest is empty.");
        foreach (string dependency in manifest.Dependencies.Concat(manifest.OptionalDependencies))
        {
            _ = ParseDependency(dependency);
        }

        return manifest;
    }

    internal static (string Id, SemanticVersion? Version) ParseDependency(string dependency)
    {
        string[] parts = dependency.Split(':', 2);
        return parts.Length == 1 ? (parts[0], null) : (parts[0], SemanticVersion.Parse(parts[1]));
    }
}

/// <summary>The <c>config_schema</c> of a manifest.</summary>
public sealed record ConfigSchema
{
    public IReadOnlyList<ConfigOption> Options { get; init; } = [];
}

public enum ConfigOptionType
{
    Enum,
    Number,
    String,
}

/// <summary>One setting of a mod.</summary>
public sealed record ConfigOption
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ConfigOptionType Type { get; init; }

    public string Description { get; init; } = "";

    /// <summary>The choices of an <see cref="ConfigOptionType.Enum"/> setting.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The smallest value a <see cref="ConfigOptionType.Number"/> setting takes.</summary>
    public double Min { get; init; }

    /// <summary>The largest value a <see cref="ConfigOptionType.Number"/> setting takes.</summary>
    public double Max { get; init; }

    /// <summary>How far one step moves a <see cref="ConfigOptionType.Number"/> setting.</summary>
    public double Step { get; init; }

    /// <summary>How many digits after the point a <see cref="ConfigOptionType.Number"/> setting shows.</summary>
    public int Precision { get; init; }

    /// <summary>Whether a <see cref="ConfigOptionType.Number"/> setting shows as a percentage.</summary>
    public bool Percent { get; init; }

    /// <summary>The value the setting starts at: one of <see cref="Options"/>, a number or a string, depending on <see cref="Type"/>.</summary>
    public JsonElement Default { get; init; }
}
