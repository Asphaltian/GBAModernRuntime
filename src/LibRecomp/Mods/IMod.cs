namespace LibRecomp.Mods;

/// <summary>
/// Your mod's entry point. Your mod's assembly needs exactly one public class that implements this,
/// with a constructor that takes no arguments.
/// </summary>
/// <example>
/// <code>
/// public class MyMod : IMod
/// {
///     public void Load(Mod mod)
///     {
///         mod.Hook(ref Funcs.Patches.sub_8000400, ctx => Console.WriteLine("Here we go"));
///     }
/// }
/// </code>
/// </example>
public interface IMod
{
    /// <summary>
    /// Called once before the game starts, after every mod yours depends on has loaded. This is where
    /// you change the game's functions with <see cref="Mod.Replace"/>, <see cref="Mod.Hook"/> and
    /// <see cref="Mod.HookReturn"/>.
    /// </summary>
    void Load(Mod mod);
}
