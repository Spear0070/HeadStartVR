using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using HeadStartVR.Models;

namespace HeadStartVR.Services;

public sealed record ImplicitLayer(string ManifestPath, bool Enabled, string Hive, string? Name, string? DisableEnvironment);

public static class OpenXrLayers
{
    private const string ImplicitKey = @"Software\Khronos\OpenXR\1\ApiLayers\Implicit";

    public static List<ImplicitLayer> GetImplicitLayers()
    {
        var list = new List<ImplicitLayer>();
        Read(Registry.CurrentUser, "HKCU", list);
        Read(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), "HKLM", list);
        return list;
    }

    private static void Read(RegistryKey root, string hive, List<ImplicitLayer> list)
    {
        try
        {
            using var key = root.OpenSubKey(ImplicitKey);
            if (key == null) return;
            foreach (var manifest in key.GetValueNames())
            {
                var enabled = key.GetValue(manifest) is int v && v == 0;
                string? name = null, disableEnv = null;
                try
                {
                    if (File.Exists(manifest))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                        if (doc.RootElement.TryGetProperty("api_layer", out var layer))
                        {
                            if (layer.TryGetProperty("name", out var n)) name = n.GetString();
                            if (layer.TryGetProperty("disable_environment", out var d)) disableEnv = d.GetString();
                        }
                    }
                }
                catch
                {
                    // Unreadable manifest: still list it.
                }
                list.Add(new ImplicitLayer(manifest, enabled, hive, name, disableEnv));
            }
        }
        catch
        {
            // Missing permissions or key: nothing to list.
        }
    }
}

public static class Ofxr
{
    public static bool IsTrayRunning(string trayPath) =>
        Process.GetProcessesByName(string.IsNullOrWhiteSpace(trayPath) ? "OFXRBridgeTray" : Path.GetFileNameWithoutExtension(trayPath)).Length > 0;

    public static bool IsInstalled(string trayPath) => !string.IsNullOrWhiteSpace(trayPath) && File.Exists(trayPath);

    /// <summary>OFXR's tray "Arm" registers an implicit OpenXR layer for the current user (HKCU); Disarm removes it.</summary>
    public static ImplicitLayer? GetArmedLayer() =>
        OpenXrLayers.GetImplicitLayers().FirstOrDefault(l =>
            l.Enabled && File.Exists(l.ManifestPath) &&
            (l.ManifestPath.Contains("OFXR", StringComparison.OrdinalIgnoreCase) ||
             l.ManifestPath.Contains("FrameBridge", StringComparison.OrdinalIgnoreCase) ||
             (l.Name?.Contains("FrameBridge", StringComparison.OrdinalIgnoreCase) ?? false)));

    public static bool IsArmed => GetArmedLayer() != null;
}

public static class VirtualDesktop
{
    public const string StreamerProcess = "VirtualDesktop.Streamer";

    public static bool IsStreamerRunning => Process.GetProcessesByName(StreamerProcess).Length > 0;

    public static string? GetActiveRuntime()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            return key?.GetValue("ActiveRuntime") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>XR_RUNTIME_JSON overrides the registry for every OpenXR app (a known cause of UEVR failures, praydog/UEVR#78).</summary>
    public static string? GetRuntimeOverride() =>
        Environment.GetEnvironmentVariable("XR_RUNTIME_JSON", EnvironmentVariableTarget.User) ??
        Environment.GetEnvironmentVariable("XR_RUNTIME_JSON", EnvironmentVariableTarget.Machine);

    public static string DescribeRuntime(string? runtimeJson)
    {
        if (string.IsNullOrEmpty(runtimeJson)) return "none";
        var p = runtimeJson.ToLowerInvariant();
        if (p.Contains("virtual desktop")) return "Virtual Desktop (VDXR)";
        if (p.Contains("steamvr")) return "SteamVR";
        if (p.Contains("oculus")) return "Meta / Oculus";
        return Path.GetFileName(runtimeJson);
    }

    public static bool IsVdRuntime(string? runtimeJson) =>
        runtimeJson?.Contains("Virtual Desktop", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>Virtual Desktop is optional: people on SteamVR, Air Link or Steam Link simply skip that step.</summary>
    public static bool IsInstalled(string streamerPath) =>
        (!string.IsNullOrWhiteSpace(streamerPath) && File.Exists(streamerPath)) || IsStreamerRunning;
}

/// <summary>Finds optional companion apps so nothing has to be configured by hand.</summary>
public static class Detect
{
    public static string? FindVdStreamer()
    {
        var running = Process.GetProcessesByName(VirtualDesktop.StreamerProcess).Select(PathOf).FirstOrDefault(p => p != null);
        if (running != null) return running;

        // The installer's uninstall entry knows where it went.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var uninstall = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var k = uninstall.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string display ||
                        !display.Contains("Virtual Desktop Streamer", StringComparison.OrdinalIgnoreCase)) continue;
                    if (k.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "VirtualDesktop.Streamer.exe")))
                        return Path.Combine(dir, "VirtualDesktop.Streamer.exe");
                }
            }
            catch { }
        }

        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer", "VirtualDesktop.Streamer.exe");
        return File.Exists(standard) ? standard : null;
    }

    /// <summary>OFXR Bridge is a portable zip, so look where people usually unzip tools.</summary>
    public static string? FindOfxrTray()
    {
        const string exe = "OFXRBridgeTray.exe";
        var running = Process.GetProcessesByName("OFXRBridgeTray").Select(PathOf).FirstOrDefault(p => p != null);
        if (running != null) return running;

        var roots = new List<string>
        {
            KnownFolders.Downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                foreach (var name in new[] { "OFXR", "VRTools", "VR", "VR Tools", "Tools", "UEVR" })
                    roots.Add(Path.Combine(d.RootDirectory.FullName, name));
        }
        catch { }

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var hit = Directory.EnumerateFiles(root, exe, options).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    private static string? PathOf(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }
}
