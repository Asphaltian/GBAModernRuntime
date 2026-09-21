using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LibRecomp.Mods;

/// <summary>
/// Every mod the player has, both from the <c>mods</c> folder in <see cref="Recomp.ConfigPath"/>
/// and the ones built into your project, along with which ones are on and the order they load in.
/// <see cref="Recomp.Start"/> finds them, and <see cref="Recomp.StartGame"/> loads the ones that
/// are on.
/// </summary>
public static class ModManager
{
    private const string EmbeddedSource = "(built in)";

    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly Dictionary<string, byte[]> EmbeddedMods = [];
    private static readonly List<Mod> OpenedMods = [];
    private static readonly List<ModError> OpenErrors = [];
    private static readonly List<Mod> LoadedMods = [];
    private static readonly HashSet<string> EnabledIds = [];
    private static readonly ModAssemblies Assemblies = new();

    private static string _settingsPath = "";
    private static string _configPath = "";

    /// <summary>The folder the player puts mods in.</summary>
    public static string Folder { get; private set; } = "";

    /// <summary>Every mod found, in the order they load.</summary>
    public static IReadOnlyList<Mod> Mods => OpenedMods;

    /// <summary>Anything in the mods folder that couldn't be opened as a mod, and why.</summary>
    public static IReadOnlyList<ModError> Errors => OpenErrors;

    /// <summary>The mods that loaded for the running game, in the order they loaded.</summary>
    public static IReadOnlyList<Mod> Loaded => LoadedMods;

    /// <summary>Builds a mod into your project from its zip file's bytes. Make sure to call it before <see cref="Recomp.Start"/>.</summary>
    public static void RegisterEmbeddedMod(string id, byte[] zip)
    {
        EmbeddedMods.Add(id, zip);
    }

    /// <summary>Whether the player turned a mod on.</summary>
    public static bool IsEnabled(string id) => EnabledIds.Contains(id);

    /// <summary>Whether a mod that's off will load anyway, because a mod that's on needs it.</summary>
    public static bool IsAutoEnabled(string id) => !IsEnabled(id) && ActiveIds().Contains(id);

    /// <summary>Turns a mod on or off, and remembers it for next time.</summary>
    public static void Enable(string id, bool enabled)
    {
        if (enabled ? EnabledIds.Add(id) : EnabledIds.Remove(id))
        {
            SaveSettings();
        }
    }

    /// <summary>Moves a mod to another place in the load order, and remembers it for next time.</summary>
    public static void SetIndex(string id, int index)
    {
        var mod = Find(id) ?? throw new KeyNotFoundException($"There is no mod {id}.");
        OpenedMods.Remove(mod);
        OpenedMods.Insert(Math.Clamp(index, 0, OpenedMods.Count), mod);
        SaveSettings();
    }

    /// <summary>Finds the mods again, like after the player adds or removes some. It can't be done once the game has started.</summary>
    /// <exception cref="InvalidOperationException">The game has already started.</exception>
    public static void Refresh()
    {
        if (Recomp.CurrentGame is not null)
        {
            throw new InvalidOperationException("Mods can't be found again once the game has started.");
        }

        Scan(_configPath);
    }

    internal static void Scan(string configPath)
    {
        string modsFolder = Directory.CreateDirectory(Path.Combine(configPath, "mods")).FullName;
        string configFolder = Directory.CreateDirectory(Path.Combine(configPath, "mod_config")).FullName;
        _configPath = configPath;
        Folder = modsFolder;
        _settingsPath = Path.Combine(configPath, "mods.json");
        OpenedMods.Clear();
        OpenErrors.Clear();
        EnabledIds.Clear();

        var sources = EmbeddedMods.Select(embedded => (Source: $"{embedded.Key} {EmbeddedSource}", Open: (Func<Mod>)(() => Mod.OpenEmbedded(embedded.Value))))
            .Concat(Directory.EnumerateFileSystemEntries(modsFolder).Order()
                .Where(path => Directory.Exists(path) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Select(path => (Source: Path.GetFileName(path), Open: (Func<Mod>)(() => Directory.Exists(path) ? Mod.OpenFolder(path) : Mod.OpenZip(path)))));

        var found = new List<Mod>();
        foreach (var (source, open) in sources)
        {
            try
            {
                var mod = open();
                if (found.Any(other => other.Manifest.Id == mod.Manifest.Id))
                {
                    throw new InvalidDataException($"Another mod has the id {mod.Manifest.Id}.");
                }

                mod.LoadConfig(Path.Combine(configFolder, $"{mod.Manifest.Id}.json"));
                found.Add(mod);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or FormatException)
            {
                OpenErrors.Add(new ModError(source, e.Message));
            }
        }

        var settings = BackedUpFile.TryRead(_settingsPath, json => JsonNode.Parse(json)!.AsObject(), out var json) ? json : [];
        var order = settings["mod_order"]?.AsArray().Select(node => (string?)node).ToList() ?? [];
        var enabled = settings["enabled_mods"]?.AsArray().Select(node => (string?)node).ToHashSet() ?? [];

        OpenedMods.AddRange(found.Where(mod => !order.Contains(mod.Manifest.Id)));
        OpenedMods.AddRange(order.Select(id => found.FirstOrDefault(mod => mod.Manifest.Id == id)).OfType<Mod>());

        foreach (var mod in OpenedMods)
        {
            bool isNew = !order.Contains(mod.Manifest.Id);
            if (enabled.Contains(mod.Manifest.Id) || (isNew && mod.Manifest.EnabledByDefault))
            {
                EnabledIds.Add(mod.Manifest.Id);
            }
        }
    }

    internal static IReadOnlyList<ModError> Load(GameEntry game)
    {
        LoadedMods.Clear();
        var active = ActiveIds();
        var mods = OpenedMods.Where(mod => mod.Manifest.GameId == game.ModGameId && active.Contains(mod.Manifest.Id)).ToList();

        var errors = mods.SelectMany(mod => Check(mod, mods).Select(message => new ModError(mod.Manifest.Id, message))).ToList();
        if (errors.Count > 0)
        {
            return errors;
        }

        foreach (var mod in mods)
        {
            if (!TryLoad(mod, mods, out string? message))
            {
                errors.Add(new ModError(mod.Manifest.Id, message));
            }
        }

        return errors;
    }

    private static Mod? Find(string id) => OpenedMods.FirstOrDefault(mod => mod.Manifest.Id == id);

    private static HashSet<string> ActiveIds()
    {
        var active = new HashSet<string>();
        var pending = new Stack<string>(EnabledIds);
        while (pending.TryPop(out string? id))
        {
            if (active.Add(id) && Find(id) is { } mod)
            {
                foreach (string dependency in mod.Manifest.Dependencies)
                {
                    pending.Push(ModManifest.ParseDependency(dependency).Id);
                }
            }
        }

        return active;
    }

    private static IEnumerable<string> Check(Mod mod, List<Mod> mods)
    {
        if (mod.Manifest.MinimumRecompVersion > Recomp.ProjectVersion)
        {
            yield return $"It needs version {mod.Manifest.MinimumRecompVersion} of the project or later.";
        }

        foreach (string dependency in mod.Manifest.Dependencies)
        {
            var (id, version) = ModManifest.ParseDependency(dependency);
            var found = mods.FirstOrDefault(other => other.Manifest.Id == id);
            if (found is null)
            {
                yield return $"It needs the mod {id}.";
            }
            else if (version is { } minimum && found.Manifest.Version < minimum)
            {
                yield return $"It needs version {minimum} of {id} or later, and {found.Manifest.Version} is there.";
            }
        }
    }

    private static bool TryLoad(Mod mod, List<Mod> mods, [NotNullWhen(false)] out string? message)
    {
        message = null;
        if (LoadedMods.Contains(mod))
        {
            return true;
        }

        LoadedMods.Add(mod);
        foreach (string dependency in mod.Manifest.Dependencies.Concat(mod.Manifest.OptionalDependencies))
        {
            string id = ModManifest.ParseDependency(dependency).Id;
            if (mods.FirstOrDefault(other => other.Manifest.Id == id) is { } other && !TryLoad(other, mods, out _))
            {
                message = $"It needs the mod {id}, which did not load.";
                return false;
            }
        }

        if (!mod.HasCode)
        {
            return true;
        }

        try
        {
            CreateEntryPoint(Assemblies.Load(mod), mod.Manifest).Load(mod);
            return true;
        }
        catch (Exception e)
        {
            LoadedMods.Remove(mod);
            message = (e as TargetInvocationException)?.InnerException?.Message ?? e.Message;
            return false;
        }
    }

    private static IMod CreateEntryPoint(Assembly assembly, ModManifest manifest)
    {
        var entryPoints = assembly.GetExportedTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IMod).IsAssignableFrom(type))
            .ToList();

        if (entryPoints.Count != 1 || entryPoints[0].GetConstructor(Type.EmptyTypes) is not { } constructor)
        {
            throw new InvalidDataException($"{manifest.Id}.dll needs one public class that implements IMod, with a constructor that takes nothing.");
        }

        return (IMod)constructor.Invoke(null);
    }

    private static void SaveSettings()
    {
        var settings = new JsonObject
        {
            ["enabled_mods"] = new JsonArray([.. EnabledIds.Order().Select(id => JsonValue.Create(id))]),
            ["mod_order"] = new JsonArray([.. OpenedMods.Select(mod => JsonValue.Create(mod.Manifest.Id))]),
        };

        BackedUpFile.Write(_settingsPath, JsonSerializer.SerializeToUtf8Bytes(settings, Indented));
    }

    private sealed class ModAssemblies() : AssemblyLoadContext("Mods")
    {
        public Assembly Load(Mod mod)
        {
            return LoadFromStream(new MemoryStream(mod.ReadFile(mod.AssemblyName)));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = $"{assemblyName.Name}.dll";
            var owner = LoadedMods.FirstOrDefault(mod => mod.HasFile(name));
            return owner is null ? null : LoadFromStream(new MemoryStream(owner.ReadFile(name)));
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string extension = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
            foreach (string folder in LoadedMods.Select(mod => mod.NativeLibraryFolder).OfType<string>().Distinct())
            {
                foreach (string name in (string[])[unmanagedDllName, unmanagedDllName + extension])
                {
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path))
                    {
                        return LoadUnmanagedDllFromPath(path);
                    }
                }
            }

            return 0;
        }
    }
}

/// <summary>Why a mod couldn't be opened or loaded.</summary>
/// <param name="Mod">The mod's id, or its file name if its manifest couldn't be read.</param>
/// <param name="Message">What went wrong.</param>
public sealed record ModError(string Mod, string Message);
