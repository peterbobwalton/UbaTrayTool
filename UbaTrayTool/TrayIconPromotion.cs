using Microsoft.Win32;

namespace UbaTrayTool;

/// <summary>
/// Windows 11 puts tray icons it hasn't seen before in the overflow flyout (behind the ^ button),
/// and an updated exe counts as new. After each install or update, promote this app's icon to the
/// taskbar once; if the user later hides it in Settings, that choice is left alone until the next update.
/// </summary>
internal static class TrayIconPromotion
{
    private const string SettingsKeyPath = @"Control Panel\NotifyIconSettings";
    private const string MarkerKeyPath = @"Software\UbaTrayTool\IconPromotion";

    public static void PromoteOnce()
    {
        string? path = Environment.ProcessPath;
        if (path is null)
            return;

        string name = Path.GetFileName(path);
        string stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path).Ticks.ToString();
            using RegistryKey? marker = Registry.CurrentUser.OpenSubKey(MarkerKeyPath);
            if (marker?.GetValue(name) as string == stamp)
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        // Explorer creates the icon's settings entry shortly after the icon first appears.
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (TryPromote(name))
                {
                    TryWriteMarker(name, stamp);
                    return;
                }
            }
        });
    }

    private static bool TryPromote(string exeName)
    {
        try
        {
            using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            if (settings is null)
                return false;   // not Windows 11

            bool found = false;
            foreach (string subKeyName in settings.GetSubKeyNames())
            {
                using RegistryKey? entry = settings.OpenSubKey(subKeyName, writable: true);
                // Stored with a known-folder GUID in place of e.g. C:\Program Files, so match on the file name.
                if (entry?.GetValue("ExecutablePath") is not string stored ||
                    !stored.EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase))
                    continue;

                entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                found = true;
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void TryWriteMarker(string exeName, string stamp)
    {
        try
        {
            using RegistryKey marker = Registry.CurrentUser.CreateSubKey(MarkerKeyPath);
            marker.SetValue(exeName, stamp, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
