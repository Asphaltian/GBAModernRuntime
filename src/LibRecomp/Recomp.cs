using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using AGBModern;
using LibRecomp.Mods;

namespace LibRecomp;

/// <summary>A recompiled function, or a patch that replaces one.</summary>
public delegate void RecompFunc(RecompContext ctx);

/// <summary>
/// This is where your project sets its game up and starts it. Register everything first, then call
/// <see cref="Start"/>, and once <see cref="IsROMValid"/> says the player's ROM is stored, call
/// <see cref="StartGame"/>.
/// </summary>
/// <example>
/// <code>
/// Recomp.RegisterConfigPath(dataFolder);
/// Recomp.RegisterGame(game);
/// Recomp.RegisterFunctions(Funcs.Table);
/// Recomp.RegisterRAMFunctions(Funcs.RAMFunctions);
/// Recomp.Start(new SemanticVersion(1, 0, 0), ShowError);
///
/// // The first time around, check the player's ROM and keep a copy of it
/// if (!Recomp.IsROMValid("mygame"))
/// {
///     Recomp.SelectROM(romPath, "mygame");
/// }
///
/// Recomp.StartGame("mygame");
/// </code>
/// </example>
public static class Recomp
{
    private const int GameStackSize = 64 * 1024 * 1024;
    private const int HeaderSize = 0xC0;
    private const int FixedValueOffset = 0xB2;
    private const byte FixedValue = 0x96;
    private const int TitleOffset = 0xA0;
    private const int TitleLength = 12;

    private static readonly Dictionary<uint, RecompFunc> Functions = [];
    private static readonly Dictionary<string, GameEntry> Games = [];
    private static (uint Address, uint Size, RecompFunc Function)[] _ramFunctions = [];
    private static (string GameId, byte[] ROM)? _storedROM;

    private static string? _configPath;
    private static Action<string> _showError = Console.Error.WriteLine;
    private static SaveFile? _saveFile;
    private static Thread? _gameThread;
    private static volatile bool _isQuitting;
    private static uint _unwindTarget;

    /// <summary>Your project's version. Mods can ask for a minimum one.</summary>
    public static SemanticVersion ProjectVersion { get; private set; }

    /// <summary>The folder where stored ROMs, saves, mods and their settings go.</summary>
    public static string ConfigPath => _configPath ?? throw new InvalidOperationException("Call RegisterConfigPath first.");

    /// <summary>The game <see cref="StartGame"/> started, if there is one.</summary>
    public static GameEntry? CurrentGame { get; private set; }

    /// <summary>This is read by recompiled code, so you won't need it yourself.</summary>
    public static bool IsCopyInEWRAM { get; private set; }

    /// <inheritdoc cref="IsCopyInEWRAM"/>
    public static uint CopyOffset { get; private set; }

    /// <inheritdoc cref="IsCopyInEWRAM"/>
    public static bool IsUnwinding { get; private set; }

    /// <summary>Sets <see cref="ConfigPath"/>. Call this before anything else.</summary>
    public static void RegisterConfigPath(string path)
    {
        _configPath = path;
    }

    /// <summary>Adds a game your project can run.</summary>
    public static void RegisterGame(GameEntry game)
    {
        if (!Games.TryAdd(game.GameId, game))
        {
            throw new ArgumentException($"A game with the id {game.GameId} is already registered.", nameof(game));
        }
    }

    /// <summary>Registers <c>Funcs.Table</c> from the generated code.</summary>
    public static void RegisterFunctions(ReadOnlySpan<(uint Address, RecompFunc Function)> table)
    {
        Functions.Clear();
        foreach (var (address, function) in table)
        {
            Functions[address] = function;
        }
    }

    /// <summary>Registers <c>Funcs.RAMFunctions</c> from the generated code.</summary>
    public static void RegisterRAMFunctions(ReadOnlySpan<(uint Address, uint Size, RecompFunc Function)> table)
    {
        _ramFunctions = table.ToArray();
    }

    /// <summary>
    /// Finds the mods. The runtime calls <paramref name="showError"/> whenever it needs to tell the
    /// player something went wrong. Keep in mind that this can happen on any thread.
    /// </summary>
    public static void Start(SemanticVersion projectVersion, Action<string> showError)
    {
        ProjectVersion = projectVersion;
        _showError = showError;
        ModManager.Scan(ConfigPath);
    }

    /// <summary>Checks the ROM the player picked for a game, and keeps a copy of it if it's the right one.</summary>
    public static ROMValidationError SelectROM(string romPath, string gameId)
    {
        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(romPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ROMValidationError.FailedToOpen;
        }

        var error = Validate(rom, Games[gameId]);
        if (error != ROMValidationError.Good)
        {
            return error;
        }

        try
        {
            BackedUpFile.Write(StoredROMPath(gameId), rom);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ROMValidationError.OtherError;
        }

        _storedROM = (gameId, rom);
        return ROMValidationError.Good;
    }

    /// <summary>Whether the right ROM for a game is stored.</summary>
    public static bool IsROMValid(string gameId)
    {
        return LoadStoredROM(gameId) is not null;
    }

    /// <summary>
    /// Loads a game's stored ROM, save and mods, and runs it on a thread of its own. If something stops
    /// it, the player is told why and this returns false.
    /// </summary>
    public static bool StartGame(string gameId)
    {
        var game = Games[gameId];
        if (LoadStoredROM(gameId) is not { } rom)
        {
            _showError("The stored ROM could not be read. Select the ROM again.");
            return false;
        }

        Memory.LoadROM(rom);
        _storedROM = null;
        SaveMemory.Initialize(game.SaveType);
        GPIO.Connect(game.GPIO);

        var errors = ModManager.Load(game);
        if (errors.Count > 0)
        {
            _showError("These mods could not be loaded. Fix or disable them, then restart:\n\n"
                + string.Join("\n", errors.Select(error => $"{error.Mod}: {error.Message}")));
            return false;
        }

        CurrentGame = game;
        var saveFile = new SaveFile(Path.Combine(ConfigPath, "saves", $"{gameId}.sav"));
        _saveFile = saveFile;
        Video.FrameFinished += OnFrameFinished;
        Power.WhileStopped = StopIfQuitting;
        Video.Start(paced: true);
        APU.Start();

        _gameThread = new Thread(Run, GameStackSize) { Name = "Game", IsBackground = true };
        _gameThread.Start();
        return true;

        void Run()
        {
            var start = game.EntryPoint;
            try
            {
                while (true)
                {
                    try
                    {
                        IsUnwinding = false;
                        BIOS.Boot();
                        start(new RecompContext());
                        ThrowIfUnwinding();
                        return;
                    }
                    catch (BIOS.GameReset reset)
                    {
                        start = reset.Start == BIOS.ROMStart ? game.EntryPoint : LookupFunc(reset.Start);
                    }
                }
            }
            catch (GameQuit)
            {
            }
            catch (Exception e)
            {
                saveFile.Flush();
                _showError(e.ToString());
                Environment.Exit(1);
            }
        }
    }

    /// <summary>Stops the game at the end of its current frame and writes its save. Make sure to call this before your program exits.</summary>
    public static void Quit()
    {
        _isQuitting = true;
        if (_gameThread is { IsAlive: true } && _gameThread.Join(TimeSpan.FromSeconds(1)))
        {
            return;
        }

        _saveFile?.Flush();
    }

    /// <summary>Finds the function at an address, in ROM or copied into RAM. Throws if there isn't one.</summary>
    public static RecompFunc LookupFunc(uint address)
    {
        return TryLookupFunc(address, out var function)
            ? function
            : throw new InvalidOperationException($"No recompiled function at 0x{address & ~1u:X8}.{RAMFunctionHint(address)}");
    }

    /// <summary>This is called by recompiled code, so you won't need it yourself.</summary>
    public static void IndirectJump(RecompContext ctx, uint target)
    {
        if (TryLookupFunc(target, out var function))
        {
            function(ctx);
        }
        else
        {
            (IsUnwinding, _unwindTarget) = (true, target & ~1u);
        }
    }

    /// <inheritdoc cref="IndirectJump"/>
    public static bool TryResumeAt(uint address)
    {
        if (_unwindTarget != address)
        {
            return false;
        }

        IsUnwinding = false;
        return true;
    }

    internal static void ThrowIfUnwinding()
    {
        if (IsUnwinding)
        {
            throw new InvalidOperationException($"The game jumped to 0x{_unwindTarget:X8}, which is not a recompiled function or anywhere a call returns to.{RAMFunctionHint(_unwindTarget)}");
        }
    }

    /// <inheritdoc cref="IndirectJump"/>
    public static Exception SwitchError(string function, uint address, uint target)
    {
        return new InvalidOperationException(
            $"The jump table dispatch at 0x{address:X8} in {function} went to 0x{target:X8}, which is not one of its cases.");
    }

    /// <inheritdoc cref="IndirectJump"/>
    public static Exception JumpError(string function, uint address, uint target)
    {
        return new InvalidOperationException(
            $"The jump at 0x{address:X8} in {function} went to 0x{target:X8}, inside {function}, and the recompiled code can't follow it there.");
    }

    /// <inheritdoc cref="IndirectJump"/>
    public static void HandleEvents(RecompContext ctx)
    {
        Scheduler.RunDueEvents();

        if (ctx.AreIRQsEnabled && Interrupts.IsPending)
        {
            BIOS.EnterIRQ(ctx);
        }
    }

    /// <inheritdoc cref="IndirectJump"/>
    public static void SWI(RecompContext ctx, int function)
    {
        BIOS.Call(ctx, function);
    }

    internal static void StopIfQuitting()
    {
        if (_isQuitting)
        {
            _saveFile?.Flush();
            throw new GameQuit();
        }
    }

    private static void OnFrameFinished(VideoFrame frame)
    {
        _saveFile!.Update();
        StopIfQuitting();
    }

    private static string RAMFunctionHint(uint address) => address >> 24 is 0x2 or 0x3
        ? " If the game copies a function there, add it to input.ram_funcs in the GBARecomp config."
        : "";

    private static string StoredROMPath(string gameId) => Path.Combine(ConfigPath, $"{gameId}.gba");

    private static byte[]? LoadStoredROM(string gameId)
    {
        if (_storedROM is { } stored && stored.GameId == gameId)
        {
            return stored.ROM;
        }

        if (!BackedUpFile.TryRead(StoredROMPath(gameId), contents => contents, out var rom) || Validate(rom, Games[gameId]) != ROMValidationError.Good)
        {
            return null;
        }

        _storedROM = (gameId, rom);
        return rom;
    }

    private static ROMValidationError Validate(byte[] rom, GameEntry game)
    {
        if (rom.Length < HeaderSize || rom[FixedValueOffset] != FixedValue)
        {
            return ROMValidationError.NotAROM;
        }

        if (Convert.ToHexString(SHA1.HashData(rom)).Equals(game.ROMHash, StringComparison.OrdinalIgnoreCase))
        {
            return ROMValidationError.Good;
        }

        string title = Encoding.ASCII.GetString(rom, TitleOffset, TitleLength).TrimEnd('\0');
        return title == game.InternalName ? ROMValidationError.IncorrectVersion : ROMValidationError.IncorrectROM;
    }

    private static bool TryLookupFunc(uint address, [MaybeNullWhen(false)] out RecompFunc function)
    {
        address &= ~1u;
        if (Functions.TryGetValue(address, out function))
        {
            return true;
        }

        (uint Address, uint Size, RecompFunc Function)? copy = null;
        foreach (var candidate in _ramFunctions)
        {
            bool isBetterMatch = IsCopyAt(address, candidate.Address, candidate.Size)
                && (copy is not { } found || MatchingLength(address, candidate.Address) > MatchingLength(address, found.Address));
            if (isBetterMatch)
            {
                copy = candidate;
            }
        }

        if (copy is not { } original)
        {
            return false;
        }

        function = ctx => RunCopy(original.Function, address, original.Address, ctx);
        return true;
    }

    private static void RunCopy(RecompFunc function, uint address, uint romAddress, RecompContext ctx)
    {
        (bool wasInEWRAM, uint previousOffset) = (IsCopyInEWRAM, CopyOffset);
        (IsCopyInEWRAM, CopyOffset) = (address >> 24 == 0x2, address - romAddress);
        try
        {
            function(ctx);
        }
        finally
        {
            (IsCopyInEWRAM, CopyOffset) = (wasInEWRAM, previousOffset);
        }
    }

    private static bool IsCopyAt(uint address, uint romAddress, uint size)
    {
        return Memory.TryGetSpan(address, (int)size, out var copy)
            && Memory.TryGetSpan(romAddress, (int)size, out var original)
            && copy.SequenceEqual(original);
    }

    private static int MatchingLength(uint address, uint romAddress)
    {
        const int Chunk = 64;
        int length = 0;
        while (Memory.TryGetSpan(address + (uint)length, Chunk, out var copy)
            && Memory.TryGetSpan(romAddress + (uint)length, Chunk, out var original))
        {
            int common = copy.CommonPrefixLength(original);
            length += common;
            if (common < Chunk)
            {
                break;
            }
        }

        return length;
    }

    private sealed class GameQuit : Exception;
}
