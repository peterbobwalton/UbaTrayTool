using Microsoft.Win32;

namespace UbaTrayTool;

/// <summary>Per-user settings in HKCU\Software\UbaTrayTool.</summary>
internal static class Settings
{
    private const string KeyPath = @"Software\UbaTrayTool";

    public static bool NotifyOnFinish
    {
        get => GetInt(nameof(NotifyOnFinish), 1) != 0;
        set => SetInt(nameof(NotifyOnFinish), value ? 1 : 0);
    }

    /// <summary>Pop the window up when a build starts (it tucks away again a few seconds after it ends).</summary>
    public static bool AutoShow
    {
        get => GetInt(nameof(AutoShow), 1) != 0;
        set => SetInt(nameof(AutoShow), value ? 1 : 0);
    }

    /// <summary>Hold the channel's listener mutex so UBA doesn't launch UbaVisualizer itself.</summary>
    public static bool SuppressVisualizer
    {
        get => GetInt(nameof(SuppressVisualizer), 0) != 0;
        set => SetInt(nameof(SuppressVisualizer), value ? 1 : 0);
    }

    public static string? VisualizerPath
    {
        get => GetString(nameof(VisualizerPath));
        set => SetString(nameof(VisualizerPath), value);
    }

    private static int GetInt(string name, int fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int i ? i : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }

    private static void SetInt(string name, int value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static string? GetString(string name)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static void SetString(string name, string? value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value is null)
                key.DeleteValue(name, throwOnMissingValue: false);
            else
                key.SetValue(name, value, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }
}
