using AGBModern;

namespace LibRecomp;

/// <summary>A game your project can run. Register it with <see cref="Recomp.RegisterGame"/>.</summary>
/// <param name="GameId">The name you refer to the game by. Its stored ROM and its save are named after it too.</param>
/// <param name="ModGameId">The <c>game_id</c> that mods for this game put in their manifest. Different versions of a game can share one.</param>
/// <param name="InternalName">The title in the ROM header. This is how the player gets told they picked the wrong version of the game, rather than the wrong game.</param>
/// <param name="ROMHash">The SHA-1 of the ROM the game was recompiled from, in hex.</param>
/// <param name="SaveType">The cartridge's save chip.</param>
/// <param name="GPIO">What the cartridge has on its GPIO port.</param>
/// <param name="EntryPoint">The recompiled function the game starts in, which is wherever the branch at 0x08000000 goes. A soft reset starts here again too.</param>
public sealed record GameEntry(
    string GameId,
    string ModGameId,
    string InternalName,
    string ROMHash,
    SaveType SaveType,
    GPIODevices GPIO,
    RecompFunc EntryPoint);

/// <summary>What <see cref="Recomp.SelectROM"/> thought of the ROM the player picked.</summary>
public enum ROMValidationError
{
    /// <summary>It's the right ROM, and a copy of it is stored.</summary>
    Good,

    /// <summary>The file couldn't be read.</summary>
    FailedToOpen,

    /// <summary>The file isn't a GBA ROM at all.</summary>
    NotAROM,

    /// <summary>It's a ROM of some other game.</summary>
    IncorrectROM,

    /// <summary>It's the right game, but not the version your project was recompiled from.</summary>
    IncorrectVersion,

    /// <summary>The ROM was fine, but storing the copy failed.</summary>
    OtherError,
}
