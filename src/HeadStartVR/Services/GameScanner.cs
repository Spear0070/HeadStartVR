using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HeadStartVR.Services;

public sealed record FoundGame(string Name, string ExePath, string Store, string? SteamAppId, string? LaunchUri);

/// <summary>
/// Finds installed Unreal Engine games (anything with a Binaries\Win64\*-Shipping.exe) in Steam, Epic and GOG
/// libraries and in extra folders. Read-only: it only looks at library manifests and the file system.
/// </summary>
public static partial class GameScanner
{
    /// <summary>
    /// Common places people keep non-store games, checked without any setup: &lt;drive&gt;:\Games on every fixed drive,
    /// and &lt;drive&gt;:\XboxGames (Xbox app / Game Pass installs).
    /// </summary>
    public static List<string> DefaultFolders()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                foreach (var name in new[] { "Games", "XboxGames" })
                {
                    var p = Path.Combine(d.RootDirectory.FullName, name);
                    if (Directory.Exists(p)) list.Add(p);
                }
        }
        catch { }
        return list;
    }

    /// <summary>Every folder a scan looks through besides the store libraries (defaults + the user's own).</summary>
    public static List<string> FoldersToScan(IEnumerable<string> extraFolders) =>
        DefaultFolders().Concat(extraFolders.Where(Directory.Exists))
            .Select(p => p.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static List<FoundGame> Scan(IEnumerable<string> extraFolders)
    {
        var found = new List<FoundGame>();
        Try(() => ScanSteam(found));
        Try(() => ScanEpic(found));
        Try(() => ScanGog(found));
        foreach (var folder in FoldersToScan(extraFolders))
            Try(() => ScanFolder(folder, found));

        return found
            .GroupBy(f => f.ExePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Write("Game scan: " + ex.Message); }
    }

    // ---- Steam: libraryfolders.vdf → appmanifest_*.acf ----------------------------------------------
    private static void ScanSteam(List<FoundGame> found)
    {
        var steam = SteamHelper.SteamPath;
        if (steam == null) return;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPathRegex().Matches(File.ReadAllText(vdf)))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                var id = AcfValue(text, "appid");
                var name = AcfValue(text, "name");
                var installDir = AcfValue(text, "installdir");
                if (id == null || installDir == null) continue;
                var dir = Path.Combine(apps, "common", installDir);
                foreach (var exe in FindShippingExes(dir))
                    found.Add(new FoundGame(name ?? installDir, exe, "Steam", id, null));
            }
        }
    }

    private static string? AcfValue(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---- Epic: launcher manifests (*.item JSON) ------------------------------------------------------
    private static void ScanEpic(List<FoundGame> found)
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return;
        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(item));
            var r = doc.RootElement;
            string? S(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var dir = S("InstallLocation");
            if (dir == null) continue;
            var ns = S("CatalogNamespace");
            var catalogItem = S("CatalogItemId");
            var app = S("AppName");
            var uri = ns != null && catalogItem != null && app != null
                ? $"com.epicgames.launcher://apps/{ns}%3A{catalogItem}%3A{app}?action=launch&silent=true"
                : null;
            foreach (var exe in FindShippingExes(dir))
                found.Add(new FoundGame(S("DisplayName") ?? Path.GetFileName(dir), exe, "Epic", null, uri));
        }
    }

    // ---- GOG: registry ------------------------------------------------------------------------------
    private static void ScanGog(List<FoundGame> found)
    {
        using var games = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (games == null) return;
        foreach (var id in games.GetSubKeyNames())
        {
            using var g = games.OpenSubKey(id);
            if (g?.GetValue("path") is not string dir) continue;
            var name = g.GetValue("gameName") as string ?? Path.GetFileName(dir);
            foreach (var exe in FindShippingExes(dir))
                found.Add(new FoundGame(name, exe, "GOG", null, null));
        }
    }

    // ---- Extra folders: each subfolder is treated as one install ------------------------------------
    private static void ScanFolder(string root, List<FoundGame> found)
    {
        foreach (var exe in FindShippingExes(root, depth: 5))
        {
            // <root>\<Game>\<Project>\Binaries\Win64\X.exe → name = <Game>
            var rel = Path.GetRelativePath(root, exe).Split(Path.DirectorySeparatorChar);
            var name = rel.Length > 4 ? rel[0] : Path.GetFileNameWithoutExtension(exe).Replace("-Win64-Shipping", "");
            var store = Path.GetFileName(root).Equals("XboxGames", StringComparison.OrdinalIgnoreCase) ? "Xbox" : "Folder";
            found.Add(new FoundGame(name, exe, store, SteamHelper.DetectAppId(exe), null));
        }
    }

    private static readonly string[] HelperExeWords = { "CrashReport", "Server", "EpicWebHelper", "CEFSubProcess", "Launcher", "Setup", "Redist", "Uninstall", "BugSplat", "Prereq" };

    /// <summary>
    /// UE packaged games keep the real game process at &lt;Root&gt;\&lt;Project&gt;\Binaries\Win64\&lt;Project&gt;-Win64-Shipping.exe,
    /// next to a &lt;Root&gt;\Engine folder. Some games rename that exe (e.g. Dawnwalker.exe); those are found through the
    /// Engine folder next to the project.
    /// </summary>
    public static IEnumerable<string> FindShippingExes(string dir, int depth = 4)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = Math.Max(0, depth - 1), IgnoreInaccessible = true };
        var result = new List<string>();
        foreach (var bin in Directory.EnumerateDirectories(dir, "*", options))
        {
            var leaf = Path.GetFileName(bin);
            if (!leaf.Equals("Win64", StringComparison.OrdinalIgnoreCase) && !leaf.Equals("WinGDK", StringComparison.OrdinalIgnoreCase)) continue;
            var binaries = Path.GetDirectoryName(bin)!;
            if (!Path.GetFileName(binaries).Equals("Binaries", StringComparison.OrdinalIgnoreCase)) continue;
            var project = Path.GetDirectoryName(binaries)!;
            if (Path.GetFileName(project).Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;

            var exes = Directory.EnumerateFiles(bin, "*.exe")
                .Where(f => !HelperExeWords.Any(w => Path.GetFileName(f).Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var shipping = exes.Where(f => f.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (shipping.Count > 0)
                result.AddRange(shipping);
            else if (exes.Count == 1 && Directory.Exists(Path.Combine(Path.GetDirectoryName(project)!, "Engine")))
                result.Add(exes[0]);
        }
        return result;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();
}
