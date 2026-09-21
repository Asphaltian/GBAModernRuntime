using AGBModern;

namespace LibRecomp;

/// <summary><c>GameEntry</c> is a game that your project can run. Register it with <see cref="Recomp.RegisterGame"/>.</summary>
/// <param name="GameId">The name you use for the game. Its stored ROM and save get named after it too.</param>
/// <param name="ModGameId">The <c>game_id</c> that mods for this game put in their manifest. Different versions of a game can share one.</param>
/// <param name="InternalName">
/// The title in the ROM header. If the player picks another version of the same game, this is how they get told
/// it's the wrong version instead of the wrong game.
/// </param>
/// <param name="ROMHash">The SHA-1 of the ROM that the game was recompiled from, in hex.</param>
/// <param name="SaveType">The save chip in the game's cartridge.</param>
/// <param name="GPIO">What the cartridge has on its GPIO port.</param>
/// <param name="EntryPoint">
/// The recompiled function that the game starts in, which is wherever the branch at 0x08000000 goes. The game starts
/// here again after a soft reset too.
/// </param>
public sealed record GameEntry(
    string GameId,
    string ModGameId,
    string InternalName,
    string ROMHash,
    SaveType SaveType,
    GPIODevices GPIO,
    RecompFunc EntryPoint);

/// <summary>What <see cref="Recomp.SelectROM"/> found out about the ROM the player picked.</summary>
public enum ROMValidationError
{
    /// <summary>It's the right ROM, and a copy of it is stored now.</summary>
    Good,

    /// <summary>The file couldn't be read.</summary>
    FailedToOpen,

    /// <summary>The file isn't a GBA ROM at all.</summary>
    NotAROM,

    /// <summary>It's a ROM of some other game.</summary>
    IncorrectROM,

    /// <summary>It's the right game, but not the version that your project was recompiled from.</summary>
    IncorrectVersion,

    /// <summary>The ROM itself was fine, but the copy couldn't be stored.</summary>
    OtherError,
}
