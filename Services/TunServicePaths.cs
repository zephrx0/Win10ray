using System;
using System.IO;

namespace XrayMetro.Services;

public static class TunServicePaths
{
    // Keep SYSTEM executables outside the application's installer-owned directory tree.
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Win10ray.TunService.Experimental");
    public static string LegacyDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Win10ray", "TunService");
    public static bool IsAllowedImage(string image, bool probeOnly) =>
        image.Equals(Path.Combine(InstallDirectory, "Win10ray.TunService.exe"), StringComparison.OrdinalIgnoreCase) ||
        probeOnly && image.Equals(Path.Combine(LegacyDirectory, "Win10ray.TunService.exe"), StringComparison.OrdinalIgnoreCase);
}
