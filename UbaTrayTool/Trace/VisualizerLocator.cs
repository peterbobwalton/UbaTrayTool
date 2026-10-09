using Microsoft.Win32;

namespace UbaTrayTool.Trace;

/// <summary>Finds UbaVisualizer.exe for the "open in UbaVisualizer" command.</summary>
internal static class VisualizerLocator
{
    private const string RelativePath = @"Engine\Binaries\Win64\UnrealBuildAccelerator\x64\UbaVisualizer.exe";

    /// <summary>Walks up from the trace host's exe (UnrealBuildTool.exe, in Engine\Binaries\DotNET) to the engine root.</summary>
    public static string? FromHost(string? hostImagePath)
    {
        if (string.IsNullOrEmpty(hostImagePath))
            return null;
        string? dir = Path.GetDirectoryName(hostImagePath);
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, RelativePath);
            if (File.Exists(candidate))
                return candidate;
            // UbaHost.dll's own folder (hosts other than UBT).
            candidate = Path.Combine(dir, "UbaVisualizer.exe");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Newest launcher install, for when no build has told us which engine is in use.</summary>
    public static string? FromInstalledEngines()
    {
        var roots = new List<(Version Version, string Dir)>();
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");
            if (key is not null)
            {
                foreach (string name in key.GetSubKeyNames())
                {
                    using RegistryKey? sub = key.OpenSubKey(name);
                    if (sub?.GetValue("InstalledDirectory") is string dir && Version.TryParse(name, out Version? v))
                        roots.Add((v, dir));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        string epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        if (Directory.Exists(epic))
        {
            foreach (string dir in Directory.GetDirectories(epic, "UE_*"))
                if (Version.TryParse(Path.GetFileName(dir)[3..], out Version? v))
                    roots.Add((v, dir));
        }

        foreach ((_, string dir) in roots.OrderByDescending(r => r.Version))
        {
            string candidate = Path.Combine(dir, RelativePath);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
