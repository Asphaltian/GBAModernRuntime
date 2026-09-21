using System.Text.Json;
using System.Text.Json.Nodes;

namespace LibRecomp.Mods;

/// <summary>
/// A mod's settings, as its <c>config_schema</c> lists them. Whatever the player sets is saved for
/// you, in the <c>mod_config</c> folder in <see cref="Recomp.ConfigPath"/>.
/// </summary>
/// <example>
/// <code>
/// bool isHard = mod.Config.GetEnum("difficulty") == 2;
///
/// // Pick up changes the player makes while the game runs
/// mod.Config.Changed += id =>
/// {
///     if (id == "speed") speed = mod.Config.GetNumber("speed");
/// };
/// </code>
/// </example>
public sealed class ModConfig
{
    private readonly ModManifest _manifest;
    private readonly string _path;
    private readonly Dictionary<string, object> _values = [];

    internal ModConfig(ModManifest manifest, string path)
    {
        _manifest = manifest;
        _path = path;

        foreach (var option in Options)
        {
            _values[option.Id] = option.Type switch
            {
                ConfigOptionType.Enum => IndexOf(option, option.Default.ValueKind == JsonValueKind.String ? option.Default.GetString()! : ""),
                ConfigOptionType.Number => option.Default.ValueKind == JsonValueKind.Number ? option.Default.GetDouble() : 0.0,
                _ => option.Default.ValueKind == JsonValueKind.String ? option.Default.GetString()! : "",
            };
        }

        if (BackedUpFile.TryRead(path, json => JsonNode.Parse(json)?["storage"]?.AsObject(), out var storage) && storage is not null)
        {
            foreach (var option in Options)
            {
                if (storage[option.Id] is JsonValue value)
                {
                    Load(option, value);
                }
            }
        }
    }

    /// <summary>Every setting the mod has.</summary>
    public IReadOnlyList<ConfigOption> Options => _manifest.ConfigSchema.Options;

    /// <summary>Tells you the id of a setting whenever it changes.</summary>
    public event Action<string>? Changed;

    /// <summary>Which option an <see cref="ConfigOptionType.Enum"/> setting is on, counting from 0.</summary>
    public uint GetEnum(string id) => (uint)Get(id, ConfigOptionType.Enum);

    /// <summary>What a <see cref="ConfigOptionType.Number"/> setting is set to.</summary>
    public double GetNumber(string id) => (double)Get(id, ConfigOptionType.Number);

    /// <summary>What a <see cref="ConfigOptionType.String"/> setting is set to.</summary>
    public string GetString(string id) => (string)Get(id, ConfigOptionType.String);

    /// <summary>Picks an option for an <see cref="ConfigOptionType.Enum"/> setting, and saves it.</summary>
    public void SetEnum(string id, uint index)
    {
        if (index >= Find(id, ConfigOptionType.Enum).Options.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"{id} has no option {index}.");
        }

        Set(id, index);
    }

    /// <summary>Sets a <see cref="ConfigOptionType.Number"/> setting, and saves it.</summary>
    public void SetNumber(string id, double value)
    {
        _ = Find(id, ConfigOptionType.Number);
        Set(id, value);
    }

    /// <summary>Sets a <see cref="ConfigOptionType.String"/> setting, and saves it.</summary>
    public void SetString(string id, string value)
    {
        _ = Find(id, ConfigOptionType.String);
        Set(id, value);
    }

    private static uint IndexOf(ConfigOption option, string choice) => (uint)Math.Max(0, option.Options.ToList().IndexOf(choice));

    private void Load(ConfigOption option, JsonValue value)
    {
        switch (option.Type)
        {
            case ConfigOptionType.Enum when value.TryGetValue(out string? choice) && option.Options.Contains(choice):
                _values[option.Id] = IndexOf(option, choice);
                break;
            case ConfigOptionType.Number when value.TryGetValue(out double number):
                _values[option.Id] = number;
                break;
            case ConfigOptionType.String when value.TryGetValue(out string? text):
                _values[option.Id] = text;
                break;
        }
    }

    private ConfigOption Find(string id, ConfigOptionType type)
    {
        var option = Options.FirstOrDefault(o => o.Id == id) ?? throw new KeyNotFoundException($"{_manifest.Id} has no setting {id}.");
        return option.Type == type ? option : throw new InvalidOperationException($"{id} is a {option.Type} setting.");
    }

    private object Get(string id, ConfigOptionType type)
    {
        _ = Find(id, type);
        return _values[id];
    }

    private void Set(string id, object value)
    {
        _values[id] = value;
        Save();
        Changed?.Invoke(id);
    }

    private void Save()
    {
        var storage = new JsonObject();
        foreach (var option in Options)
        {
            storage[option.Id] = option.Type switch
            {
                ConfigOptionType.Enum => JsonValue.Create(option.Options[(int)(uint)_values[option.Id]]),
                ConfigOptionType.Number => JsonValue.Create((double)_values[option.Id]),
                _ => JsonValue.Create((string)_values[option.Id]),
            };
        }

        var file = new JsonObject
        {
            ["mod_id"] = _manifest.Id,
            ["mod_version"] = _manifest.Version.ToString(),
            ["recomp_version"] = Recomp.ProjectVersion.ToString(),
            ["storage"] = storage,
        };

        BackedUpFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(file, ModManager.Indented));
    }
}
