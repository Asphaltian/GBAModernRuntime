namespace GBARenderer;

/// <summary>
/// Extra columns of tiles on each side of the screen, for when the window is wider than the GBA's.
/// Set <see cref="Width"/>, then fill in the columns with <see cref="Supply"/> every frame. Keep in
/// mind that a background you don't supply columns for stops at the edge of the screen.
/// </summary>
/// <example>
/// <code>
/// // Every frame, carry BG1 on past both edges of the screen
/// for (int i = 0; i &lt; margins.ColumnsShown; i++)
/// {
///     CopyMapColumn(margins.Supply(1, -i), firstColumn - i);
///     CopyMapColumn(margins.Supply(1, 30 + i), firstColumn + 30 + i);
/// }
/// </code>
/// </example>
public sealed class FrameMargins
{
    /// <summary>The most columns you can supply on each side.</summary>
    public const int ColumnsPerSide = 12;

    /// <summary>How many tiles each column has, counting down from the top of the screen.</summary>
    public const int Rows = 32;

    /// <summary>The largest <see cref="Width"/> you can set, in pixels.</summary>
    public const int MaxWidth = ((ColumnsPerSide - 2) * 8) - 8;

    internal const uint NoWindow = 0x80008000;

    private const int ColumnsPerBackground = 2 * ColumnsPerSide;

    internal readonly ushort[] Tiles = new ushort[FrameMotion.BackgroundCount * ColumnsPerBackground * Rows];

    internal readonly uint[] SuppliedColumns = new uint[FrameMotion.BackgroundCount];

    internal readonly uint[] WindowEdges = [.. Enumerable.Repeat(NoWindow, FrameMotion.WindowCount * FrameRenderer.Height)];

    private int _width;

    /// <summary>How many pixels to show past each side of the screen, up to <see cref="MaxWidth"/>.</summary>
    public int Width
    {
        get => _width;
        set => _width = Math.Clamp(value, 0, MaxWidth);
    }

    /// <summary>How many columns you need to supply on each side to cover <see cref="Width"/>.</summary>
    public int ColumnsShown => _width == 0 ? 0 : ((_width + 7) / 8) + 2;

    /// <summary>
    /// Gives you one column of a background to fill with tilemap entries, starting from the top of the
    /// screen. Column 0 holds the screen's top left pixel, so the left side goes 0, -1, -2 and so on,
    /// and the right side goes 30, 31, 32 and so on.
    /// </summary>
    public Span<ushort> Supply(int bg, int column)
    {
        int index = column <= 0 ? -column : column - 30 + ColumnsPerSide;
        SuppliedColumns[bg] |= 1u << index;
        return Tiles.AsSpan(((bg * ColumnsPerBackground) + index) * Rows, Rows);
    }

    /// <summary>
    /// Sets where window 0 or 1 reaches into the margins on one line. Just like the columns, you need
    /// to do this every frame. <paramref name="x1"/> and <paramref name="x2"/> are X1 and X2 of WIN0H
    /// or WIN1H, carried on past 0 and 240. On a line you don't supply, the window carries on the way
    /// it is at the edge of the screen.
    /// </summary>
    /// <example>
    /// <code>
    /// // On line 40, window 1 covers everything left of -20 and everything from 260 on
    /// margins.SupplyWindow(1, 40, x1: 260, x2: -20);
    /// </code>
    /// </example>
    public void SupplyWindow(int window, int line, int x1, int x2)
    {
        WindowEdges[(window * FrameRenderer.Height) + line] = (ushort)x1 | ((uint)(ushort)x2 << 16);
    }

    /// <summary>Drops every column and window you've supplied.</summary>
    public void Clear()
    {
        Array.Clear(SuppliedColumns);
        Array.Fill(WindowEdges, NoWindow);
    }
}
