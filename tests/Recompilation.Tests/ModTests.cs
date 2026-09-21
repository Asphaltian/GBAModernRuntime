using System.IO.Compression;
using System.Text;
using AGBModern;
using LibRecomp;
using LibRecomp.Mods;

namespace Recompilation.Tests;

public static class TestFuncs
{
    public static class Original
    {
        public static void Replaced(RecompContext ctx) => ctx.R0 = 1;

        public static void Contested(RecompContext ctx) => ctx.R0 = 1;

        public static void ProjectPatched(RecompContext ctx) => ctx.R0 = 1;
    }

    public static class Patches
    {
        public static RecompFunc Replaced = Original.Replaced;

        public static RecompFunc Contested = Original.Contested;

        public static RecompFunc ProjectPatched = ctx => Original.ProjectPatched(ctx);
    }
}

public sealed class ModTests : IDisposable
{
    private static readonly GameEntry Game = new("testgame", "testgame", "TEST", "", SaveType.None, GPIODevices.None, _ => { });

    private readonly string _configPath = Directory.CreateTempSubdirectory("config").FullName;

    public ModTests()
    {
        Recomp.RegisterConfigPath(_configPath);
        AppContext.SetData("loaded", "");
    }

    private string ModsFolder => Directory.CreateDirectory(Path.Combine(_configPath, "mods")).FullName;

    private static string LoadOrder => (string)AppContext.GetData("loaded")!;

    public void Dispose() => Directory.Delete(_configPath, recursive: true);

    private static string Manifest(string id, string extra = "") => $$"""
        {
            "id": "{{id}}", "game_id": "testgame", "display_name": "{{id}}", "version": "1.0.0",
            "authors": ["Tester"], "minimum_recomp_version": "1.0.0" {{extra}}
        }
        """;

    private static byte[] ModAssembly(string id, string loadBody = "", params byte[][] references) => RecompiledCode.Emit(
        id,
        [$$"""
        public sealed class {{id}}Mod : LibRecomp.Mods.IMod
        {
            public static string Greeting => "hello from {{id}}";

            public void Load(LibRecomp.Mods.Mod mod)
            {
                System.AppContext.SetData("loaded", (string?)System.AppContext.GetData("loaded") + mod.Manifest.Id + ";");
                {{loadBody}}
            }
        }
        """],
        [File.ReadAllBytes(typeof(TestFuncs).Assembly.Location), .. references]);

    private void AddFolder(string id, string manifest, byte[]? assembly = null)
    {
        string folder = Directory.CreateDirectory(Path.Combine(ModsFolder, id)).FullName;
        File.WriteAllText(Path.Combine(folder, "mod.json"), manifest);
        if (assembly is not null)
        {
            File.WriteAllBytes(Path.Combine(folder, $"{id}.dll"), assembly);
        }
    }

    private static byte[] Zip(params (string Name, byte[] Contents)[] files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create))
        {
            foreach (var (name, contents) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(contents);
            }
        }

        return bytes.ToArray();
    }

    private static void Scan() => Recomp.Start(new SemanticVersion(1, 2, 0), _ => { });

    private static IReadOnlyList<ModError> ScanAndLoad()
    {
        Scan();
        return ModManager.Load(Game);
    }

    [Fact]
    public void AModInAFolderIsEnabledAndLoaded()
    {
        AddFolder("FolderMod", Manifest("FolderMod"), ModAssembly("FolderMod"));

        Assert.Empty(ScanAndLoad());

        Assert.Equal("FolderMod;", LoadOrder);
        Assert.True(ModManager.IsEnabled("FolderMod"));
    }

    [Fact]
    public void AModInAZipFileIsLoadedAndCanReadItsFiles()
    {
        File.WriteAllBytes(Path.Combine(ModsFolder, "zipped.zip"), Zip(
            ("mod.json", Encoding.UTF8.GetBytes(Manifest("ZipMod"))),
            ("ZipMod.dll", ModAssembly("ZipMod", """System.AppContext.SetData("asset", mod.ReadFile("data/asset.bin")[0]);""")),
            ("data/asset.bin", [42])));

        Assert.Empty(ScanAndLoad());

        Assert.Equal("ZipMod;", LoadOrder);
        Assert.Equal((byte)42, AppContext.GetData("asset"));
    }

    [Fact]
    public void ABuiltInModIsLoaded()
    {
        ModManager.RegisterEmbeddedMod("BuiltIn", Zip(
            ("mod.json", Encoding.UTF8.GetBytes(Manifest("BuiltIn").Replace("testgame", "builtingame"))),
            ("BuiltIn.dll", ModAssembly("BuiltIn"))));

        Scan();
        Assert.Empty(ModManager.Load(Game with { ModGameId = "builtingame" }));

        Assert.Equal("BuiltIn;", LoadOrder);
    }

    [Fact]
    public void AModWithoutAnAssemblyIsData()
    {
        AddFolder("Textures", Manifest("Textures"));

        Assert.Empty(ScanAndLoad());

        Assert.Contains(ModManager.Loaded, mod => mod.Manifest.Id == "Textures");
    }

    [Fact]
    public void ADependencyLoadsFirstAndItsAssemblyCanBeUsed()
    {
        byte[] library = ModAssembly("Library");
        AddFolder("Library", Manifest("Library"), library);
        AddFolder("AUser", Manifest("AUser", """, "dependencies": ["Library:1.0.0"]"""), ModAssembly("AUser", """System.AppContext.SetData("greeting", LibraryMod.Greeting);""", library));

        Assert.Empty(ScanAndLoad());

        Assert.Equal("Library;AUser;", LoadOrder);
        Assert.Equal("hello from Library", AppContext.GetData("greeting"));
    }

    [Fact]
    public void ADisabledDependencyIsLoadedForTheModThatNeedsIt()
    {
        AddFolder("Needed", Manifest("Needed"), ModAssembly("Needed"));
        AddFolder("Needing", Manifest("Needing", """, "dependencies": ["Needed"]"""), ModAssembly("Needing"));
        Scan();
        ModManager.Enable("Needed", false);

        Assert.True(ModManager.IsAutoEnabled("Needed"));
        Assert.Empty(ModManager.Load(Game));
        Assert.Equal("Needed;Needing;", LoadOrder);
    }

    [Fact]
    public void NothingLoadsWhenAModCannotBe()
    {
        AddFolder("Fine", Manifest("Fine"), ModAssembly("Fine"));
        AddFolder("Needy", Manifest("Needy", """, "dependencies": ["Absent"]"""), ModAssembly("Needy"));
        AddFolder("Newer", Manifest("Newer").Replace("\"minimum_recomp_version\": \"1.0.0\"", "\"minimum_recomp_version\": \"2.0.0\""), ModAssembly("Newer"));
        AddFolder("Picky", Manifest("Picky", """, "dependencies": ["Fine:1.1.0"]"""), ModAssembly("Picky"));

        var errors = ScanAndLoad();

        Assert.Equal("", LoadOrder);
        Assert.Contains(errors, error => error.Mod == "Needy" && error.Message.Contains("Absent"));
        Assert.Contains(errors, error => error.Mod == "Newer" && error.Message.Contains("2.0.0"));
        Assert.Contains(errors, error => error.Mod == "Picky" && error.Message.Contains("1.1.0"));
    }

    [Fact]
    public void ModsForAnotherGameOrThatAreDisabledAreLeftOut()
    {
        AddFolder("Other", Manifest("Other").Replace("testgame", "anothergame"), ModAssembly("Other"));
        AddFolder("Off", Manifest("Off", """, "enabled_by_default": false"""), ModAssembly("Off"));
        AddFolder("Loose", Manifest("Loose", """, "optional_dependencies": ["Off:9.0.0"]"""), ModAssembly("Loose"));

        Assert.Empty(ScanAndLoad());

        Assert.Equal("Loose;", LoadOrder);
    }

    [Fact]
    public void WhatIsEnabledAndTheOrderAreRemembered()
    {
        AddFolder("First", Manifest("First"), ModAssembly("First"));
        AddFolder("Second", Manifest("Second"), ModAssembly("Second"));
        Scan();
        ModManager.Enable("First", false);
        ModManager.SetIndex("Second", 0);

        Scan();

        Assert.False(ModManager.IsEnabled("First"));
        Assert.Equal(["Second", "First"], ModManager.Mods.Select(mod => mod.Manifest.Id));
    }

    [Fact]
    public void SettingsStartAtTheirDefaultsAndAreRemembered()
    {
        AddFolder("Configured", Manifest("Configured", """
            , "config_schema": { "options": [
                { "id": "mode", "name": "Mode", "type": "Enum", "options": ["Off", "On"], "default": "On" },
                { "id": "speed", "name": "Speed", "type": "Number", "min": 0, "max": 2, "default": 1.5 },
                { "id": "title", "name": "Title", "type": "String", "default": "hi" }
            ] }
            """));
        Scan();
        var config = ModManager.Mods.Single().Config;
        Assert.Equal((1u, 1.5, "hi"), (config.GetEnum("mode"), config.GetNumber("speed"), config.GetString("title")));

        config.SetEnum("mode", 0);
        config.SetNumber("speed", 0.25);
        Scan();

        config = ModManager.Mods.Single().Config;
        Assert.Equal((0u, 0.25, "hi"), (config.GetEnum("mode"), config.GetNumber("speed"), config.GetString("title")));
        Assert.Contains("\"mode\": \"Off\"", File.ReadAllText(Path.Combine(_configPath, "mod_config", "Configured.json")));
    }

    [Fact]
    public void HooksRunAroundAReplacement()
    {
        AddFolder("Replacer", Manifest("Replacer"), ModAssembly("Replacer", """
            mod.Replace(ref Recompilation.Tests.TestFuncs.Patches.Replaced, ctx => ctx.R0 = ctx.R0 * 10 + 2);
            """));
        AddFolder("Hooker", Manifest("Hooker"), ModAssembly("Hooker", """
            mod.Hook(ref Recompilation.Tests.TestFuncs.Patches.Replaced, ctx => ctx.R0 = 1);
            mod.HookReturn(ref Recompilation.Tests.TestFuncs.Patches.Replaced, ctx => ctx.R0 = ctx.R0 * 10 + 3);
            """));

        Assert.Empty(ScanAndLoad());

        var ctx = new RecompContext();
        TestFuncs.Patches.Replaced(ctx);
        Assert.Equal(123u, ctx.R0);
    }

    [Fact]
    public void TwoModsCannotReplaceOneFunction()
    {
        AddFolder("A", Manifest("A"), ModAssembly("A", "mod.Replace(ref Recompilation.Tests.TestFuncs.Patches.Contested, ctx => { });"));
        AddFolder("B", Manifest("B"), ModAssembly("B", "mod.Replace(ref Recompilation.Tests.TestFuncs.Patches.Contested, ctx => { });"));

        var errors = ScanAndLoad();

        Assert.Contains(errors, error => error.Mod == "B" && error.Message.Contains("A replaces"));
    }

    [Fact]
    public void AModCannotReplaceWhatTheProjectPatches()
    {
        AddFolder("Clobber", Manifest("Clobber"), ModAssembly("Clobber", "mod.Replace(ref Recompilation.Tests.TestFuncs.Patches.ProjectPatched, ctx => { });"));

        var errors = ScanAndLoad();

        Assert.Contains(errors, error => error.Mod == "Clobber" && error.Message.Contains("project patches"));
    }

    [Fact]
    public void AnUnreadableModIsReported()
    {
        AddFolder("Broken", "{ \"id\": \"Broken\" }");

        Scan();

        Assert.Contains(ModManager.Errors, error => error.Mod == "Broken");
    }

    [Fact]
    public void RefreshingFindsAddedModsAndForgetsRemovedOnes()
    {
        AddFolder("Kept", Manifest("Kept"));
        AddFolder("Removed", Manifest("Removed"));
        Scan();

        Directory.Delete(Path.Combine(ModsFolder, "Removed"), recursive: true);
        File.WriteAllBytes(Path.Combine(ModsFolder, "added.zip"), Zip(("mod.json", Encoding.UTF8.GetBytes(Manifest("Added")))));
        ModManager.Refresh();

        Assert.Equal(ModsFolder, ModManager.Folder);
        var names = ModManager.Mods.Where(mod => mod.FileName is not null).ToDictionary(mod => mod.Manifest.Id, mod => mod.FileName);
        Assert.Equal(new Dictionary<string, string?> { ["Kept"] = "Kept", ["Added"] = "added.zip" }, names);
    }
}
