using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HeadStartVR.Services;

public static partial class SteamHelper
{
    public static string? SteamPath
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var p = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
                return p != null && Directory.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>For an exe inside ...\steamapps\common\&lt;installdir&gt;\..., find the Steam app id from appmanifest_*.acf.</summary>
    public static string? DetectAppId(string exePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exePath)!);
        while (dir?.Parent?.Parent != null)
        {
            if (dir.Parent.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                dir.Parent.Parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                var installDir = dir.Name;
                foreach (var acf in Directory.EnumerateFiles(dir.Parent.Parent.FullName, "appmanifest_*.acf"))
                {
                    try
                    {
                        var text = File.ReadAllText(acf);
                        var m = InstallDirRegex().Match(text);
                        if (m.Success && m.Groups[1].Value.Equals(installDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var id = AppIdRegex().Match(text);
                            if (id.Success) return id.Groups[1].Value;
                        }
                    }
                    catch { }
                }
                return null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Steam's cached library art for an app (wide header/hero first).</summary>
    public static string? FindCover(string appId)
    {
        var steam = SteamPath;
        if (steam == null) return null;
        var cache = Path.Combine(steam, "appcache", "librarycache");
        if (!Directory.Exists(cache)) return null;

        string[] preferred = { "library_hero.jpg", "header.jpg", "library_header.jpg", "capsule_616x353.jpg" };
        var appDir = Path.Combine(cache, appId);
        if (Directory.Exists(appDir))
        {
            var files = Directory.GetFiles(appDir, "*.jpg", SearchOption.AllDirectories);
            foreach (var name in preferred)
            {
                var hit = files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
        }
        // Older flat layout: <appid>_header.jpg
        foreach (var name in preferred)
        {
            var flat = Path.Combine(cache, $"{appId}_{name}");
            if (File.Exists(flat)) return flat;
        }
        return null;
    }

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    [GeneratedRegex("\"appid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();
}
