using System.Numerics;

namespace GBARenderer;

/// <summary>
/// How far something is drawn from where the game put it, in pixels. <see cref="From"/> is where it
/// shows when the frame first appears, and <see cref="To"/> is where it should be by the time the
/// next frame is due.
/// </summary>
public struct Displacement
{
    public Vector2 From;
    public Vector2 To;
}

/// <summary>
/// How things move between frames, so the renderer can draw that movement smoothly on displays
/// faster than the GBA's 60 Hz. Fill it in every frame. Anything you leave at zero is drawn exactly
/// where the game put it.
/// </summary>
public sealed class FrameMotion
{
    public const int BackgroundCount = 4;
    public const int ObjectCount = 128;
    public const int WindowCount = 2;

    /// <summary>One for each background, BG0 to BG3.</summary>
    public readonly Displacement[] Backgrounds = new Displacement[BackgroundCount];

    /// <summary>One for each OAM entry.</summary>
    public readonly Displacement[] Objects = new Displacement[ObjectCount];

    /// <summary>One for window 0 and one for window 1, so a window can move along with a background.</summary>
    public readonly Displacement[] Windows = new Displacement[WindowCount];

    internal void Settle()
    {
        Settle(Backgrounds);
        Settle(Objects);
        Settle(Windows);
    }

    private static void Settle(Displacement[] displacements)
    {
        for (int i = 0; i < displacements.Length; i++)
        {
            displacements[i].From = displacements[i].To;
        }
    }
}
