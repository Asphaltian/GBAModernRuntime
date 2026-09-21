using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace LibRecomp.Mods;

/// <summary>
/// A mod is a folder or a zip file with a <c>mod.json</c> in it. Your code goes in <c>id.dll</c>
/// next to it. A mod without a dll is just files, which the project or other mods can read.
/// </summary>
public sealed class Mod
{
    private const string ManifestName = "mod.json";
    private const string ThumbnailName = "thumb.png";

    private readonly string? _folder;
    private readonly Func<ZipArchive>? _openZip;

    private Mod(string? folder, Func<ZipArchive>? openZip, string? nativeLibraryFolder, string? fileName)
    {
        _folder = folder;
        _openZip = openZip;
        NativeLibraryFolder = nativeLibraryFolder;
        FileName = fileName;
        Manifest = ModManifest.Parse(TryReadFile(ManifestName) ?? throw new InvalidDataException($"There is no {ManifestName}."));
    }

    /// <summary>The name of the file or folder in the mods folder the mod came from, or null for a mod built into the project.</summary>
    public string? FileName { get; }

    /// <summary>What the mod's <c>mod.json</c> says.</summary>
    public ModManifest Manifest { get; }

    /// <summary>The mod's settings, the way the player has them set.</summary>
    public ModConfig Config { get; private set; } = null!;

    /// <summary>The mod's <c>thumb.png</c>, if it has one.</summary>
    public byte[]? Thumbnail => TryReadFile(ThumbnailName);

    internal bool HasCode => HasFile(AssemblyName);

    internal string? NativeLibraryFolder { get; }

    internal string AssemblyName => $"{Manifest.Id}.dll";

    /// <summary>Whether the mod has a file. The path starts at the mod's root and uses forward slashes.</summary>
    public bool HasFile(string name)
    {
        if (_folder is not null)
        {
            return File.Exists(Path.Combine(_folder, name));
        }

        using var zip = _openZip!();
        return zip.GetEntry(name) is not null;
    }

    /// <summary>Reads one of the mod's files, like <c>mod.ReadFile("maps/town.bin")</c>.</summary>
    public byte[] ReadFile(string name)
    {
        return TryReadFile(name) ?? throw new FileNotFoundException($"{Manifest.Id} has no file {name}.");
    }

    /// <summary>
    /// Swaps a function for your own. Keep in mind that only one mod can replace any given function,
    /// and you can't replace one the project already patches. Hooks still run before and after yours.
    /// </summary>
    /// <example>
    /// <code>
    /// mod.Replace(ref Funcs.Patches.sub_8000400, ctx =>
    /// {
    ///     // Run the game's version, then double what it returns
    ///     Funcs.Original.sub_8000400(ctx);
    ///     ctx.R0 *= 2;
    /// });
    /// </code>
    /// </example>
    public void Replace(ref RecompFunc function, RecompFunc replacement, [CallerArgumentExpression(nameof(function))] string name = "")
    {
        PatchedFunction.For(ref function, name).Replace(this, replacement);
    }

    /// <summary>Runs your code every time a function is called, right before the function itself.</summary>
    /// <example>
    /// <code>
    /// mod.Hook(ref Funcs.Patches.sub_8000400, ctx => Console.WriteLine($"Called with {ctx.R0}"));
    /// </code>
    /// </example>
    public void Hook(ref RecompFunc function, RecompFunc hook, [CallerArgumentExpression(nameof(function))] string name = "")
    {
        PatchedFunction.For(ref function, name).EntryHooks.Add(hook);
    }

    /// <summary>Runs your code every time a function returns. Whatever it returned is still in <c>ctx.R0</c>.</summary>
    public void HookReturn(ref RecompFunc function, RecompFunc hook, [CallerArgumentExpression(nameof(function))] string name = "")
    {
        PatchedFunction.For(ref function, name).ReturnHooks.Add(hook);
    }

    internal static Mod OpenFolder(string path) => new(path, null, path, Path.GetFileName(path));

    internal static Mod OpenZip(string path) => new(null, () => ZipFile.OpenRead(path), Path.GetDirectoryName(path), Path.GetFileName(path));

    internal static Mod OpenEmbedded(byte[] zip) => new(null, () => new ZipArchive(new MemoryStream(zip)), null, null);

    internal void LoadConfig(string path)
    {
        Config = new ModConfig(Manifest, path);
    }

    internal byte[]? TryReadFile(string name)
    {
        if (_folder is not null)
        {
            string file = Path.Combine(_folder, name);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }

        using var zip = _openZip!();
        if (zip.GetEntry(name) is not { } entry)
        {
            return null;
        }

        using var stream = entry.Open();
        using var contents = new MemoryStream();
        stream.CopyTo(contents);
        return contents.ToArray();
    }
}
