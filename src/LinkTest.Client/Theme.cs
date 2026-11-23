namespace LinkTest.WinUi;

/// <summary>
/// Light / dark palette for the WinForms programs' own colors (standard controls follow
/// <see cref="Application.SetColorMode"/>). <see cref="Apply"/> runs once, before the first window: switching needs a
/// restart. Programs that never call it (the server GUI) stay light.
/// </summary>
static class Theme
{
    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    public static readonly string[] Modes = { "system", "light", "dark" };

    public static bool Dark { get; private set; }

#pragma warning disable WFO5001   // Application.SetColorMode is marked experimental
    public static void Apply(string? mode)
    {
        Application.SetColorMode(mode switch { "light" => SystemColorMode.Classic, "dark" => SystemColorMode.Dark, _ => SystemColorMode.System });
        Dark = Application.IsDarkModeEnabled;
    }
#pragma warning restore WFO5001

    static Color C(int r, int g, int b, int dr, int dg, int db) => Dark ? Color.FromArgb(dr, dg, db) : Color.FromArgb(r, g, b);

    public static Color Window => C(245, 246, 248, 32, 32, 32);
    /// <summary>Panels, tiles, charts and text boxes (white in light mode).</summary>
    public static Color Card => C(255, 255, 255, 43, 43, 43);
    public static Color Bar => C(235, 238, 242, 38, 38, 38);
    public static Color Text => C(0, 0, 0, 232, 234, 237);
    public static Color Muted => C(90, 90, 90, 170, 176, 184);
    public static Color Grid => C(230, 230, 230, 60, 64, 70);
    public static Color Axis => C(110, 110, 110, 160, 166, 176);
    public static Color Frame => C(192, 192, 192, 80, 84, 90);
    public static Color Accent => C(0, 95, 184, 96, 168, 240);
    public static Color Heading => C(0, 90, 160, 110, 175, 240);
    public static Color Button => Color.FromArgb(0, 120, 212);
    public static Color ButtonDisabled => C(200, 205, 212, 70, 72, 76);

    public static Color Good => C(34, 139, 34, 91, 196, 107);
    public static Color Fair => C(255, 140, 0, 232, 161, 90);
    public static Color Poor => C(178, 34, 34, 240, 106, 106);
    public static Color GoodBg => C(223, 246, 221, 23, 51, 34);
    public static Color FairBg => C(255, 244, 206, 58, 44, 22);
    public static Color PoorBg => C(253, 231, 233, 61, 28, 31);
    public static Color NeutralBg => C(240, 240, 240, 50, 50, 50);

    // Chart series.
    public static Color Up => C(0, 120, 212, 79, 168, 240);
    public static Color Down => C(16, 137, 62, 76, 194, 122);
    public static Color Sent => C(170, 170, 170, 107, 114, 128);
    public static Color Rtt => C(202, 80, 16, 240, 138, 75);
    public static Color RttMax => C(240, 180, 140, 122, 74, 46);
    public static Color RttTcp => C(135, 100, 184, 170, 140, 220);
    public static Color LossDown => C(196, 43, 28, 240, 106, 106);
}
