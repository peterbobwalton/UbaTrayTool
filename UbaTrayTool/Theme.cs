using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace UbaTrayTool;

/// <summary>Colours for the popup, following the Windows light/dark setting and accent colour.</summary>
internal sealed class Theme
{
    public bool Light { get; private init; }
    public Color Background { get; private init; }
    public Color Surface { get; private init; }        // cards
    public Color SurfaceHover { get; private init; }
    public Color Border { get; private init; }
    public Color Text { get; private init; }
    public Color DimText { get; private init; }
    public Color FaintText { get; private init; }
    public Color Track { get; private init; }
    public Color Accent { get; private init; }
    public Color Success { get; private init; }
    public Color Danger { get; private init; }
    public Color DangerSurface { get; private init; }
    public Color Warning { get; private init; }

    // Lane colours by kind of action (compile / link / pch / shader & misc / other).
    public Color Compile { get; private init; }
    public Color Link { get; private init; }
    public Color Pch { get; private init; }
    public Color Misc { get; private init; }

    public static Theme Current() => Create(IsSystemLight(), AccentColor());

    public static Theme Create(bool light, Color accent) => light
        ? new Theme
        {
            Light = true,
            Background = Color.FromArgb(247, 248, 251),
            Surface = Color.FromArgb(255, 255, 255),
            SurfaceHover = Color.FromArgb(240, 242, 247),
            Border = Color.FromArgb(214, 217, 225),
            Text = Color.FromArgb(24, 26, 32),
            DimText = Color.FromArgb(88, 93, 106),
            FaintText = Color.FromArgb(140, 145, 158),
            Track = Color.FromArgb(226, 229, 236),
            Accent = Darken(accent, 0.1),
            Success = Color.FromArgb(22, 150, 72),
            Danger = Color.FromArgb(200, 40, 40),
            DangerSurface = Color.FromArgb(253, 238, 237),
            Warning = Color.FromArgb(185, 120, 0),
            Compile = Color.FromArgb(40, 120, 230),
            Link = Color.FromArgb(140, 80, 220),
            Pch = Color.FromArgb(0, 150, 150),
            Misc = Color.FromArgb(210, 130, 20),
        }
        : new Theme
        {
            Light = false,
            Background = Color.FromArgb(28, 29, 34),
            Surface = Color.FromArgb(39, 41, 48),
            SurfaceHover = Color.FromArgb(46, 48, 56),
            Border = Color.FromArgb(62, 65, 75),
            Text = Color.FromArgb(240, 241, 245),
            DimText = Color.FromArgb(170, 174, 186),
            FaintText = Color.FromArgb(120, 124, 136),
            Track = Color.FromArgb(54, 57, 66),
            Accent = Lighten(accent, 0.15),
            Success = Color.FromArgb(70, 205, 120),
            Danger = Color.FromArgb(255, 95, 85),
            DangerSurface = Color.FromArgb(58, 36, 38),
            Warning = Color.FromArgb(250, 190, 60),
            Compile = Color.FromArgb(90, 170, 255),
            Link = Color.FromArgb(185, 140, 255),
            Pch = Color.FromArgb(70, 210, 200),
            Misc = Color.FromArgb(255, 185, 80),
        };

    /// <summary>UBT descriptions look like "Foo.cpp (Compile [x64])" or "UnrealEditor-Foo.dll (Link [x64])".</summary>
    public Color LaneColor(string description)
    {
        string d = description.ToLowerInvariant();
        if (d.Contains("sharedpch") || d.Contains(".pch") || d.Contains("(precompile"))
            return Pch;
        if (d.Contains("(link") || d.Contains("(lib") || d.EndsWith(".dll") || d.EndsWith(".exe") || d.EndsWith(".lib"))
            return Link;
        if (d.Contains("(compile") || d.Contains(".cpp") || d.EndsWith(".c") || d.EndsWith(".obj"))
            return Compile;
        return Misc;
    }

    public static bool IsSystemLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("SystemUsesLightTheme") ?? key?.GetValue("AppsUseLightTheme");
            return value is int i && i != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The Windows accent colour (HKCU\...\DWM\AccentColor is ABGR), or a default blue.</summary>
    public static Color AccentColor()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                Color c = Color.FromArgb(abgr & 0xff, (abgr >> 8) & 0xff, (abgr >> 16) & 0xff);
                // Very dark or grey accents read badly as a progress colour.
                float sat = c.GetSaturation();
                float bri = c.GetBrightness();
                if (sat > 0.25f && bri > 0.2f)
                    return c;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return Color.FromArgb(64, 156, 255);
    }

    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.A + (b.A - a.A) * t),
        (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t),
        (int)Math.Round(a.B + (b.B - a.B) * t));

    public static Color Darken(Color c, double t) => Lerp(c, Color.Black, t);
    public static Color Lighten(Color c, double t) => Lerp(c, Color.White, t);
    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // ---- DWM window chrome ---------------------------------------------------------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11 rounded corners and a border that matches the theme.</summary>
    public void ApplyWindowChrome(IntPtr hwnd)
    {
        try
        {
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int dark = Light ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int border = Border.R | (Border.G << 8) | (Border.B << 16);   // COLORREF
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }
}
