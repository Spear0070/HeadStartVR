using System.IO;
using System.Text.Json.Serialization;

namespace HeadStartVR.Models;

public enum LaunchMethod
{
    Direct,
    Steam,
    /// <summary>A launcher link such as com.epicgames.launcher://apps/...?action=launch.</summary>
    Uri,
}

public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Full path to the game's Binaries\Win64\*-Win64-Shipping.exe.</summary>
    public string ExePath { get; set; } = "";

    /// <summary>Folder holding the UEVRInjector.exe build this game uses.</summary>
    public string UevrBuildPath { get; set; } = "";

    /// <summary>Optional UEVR profile zip; replacing this file triggers a profile update.</summary>
    public string? ProfileZipPath { get; set; }

    public LaunchMethod LaunchMethod { get; set; } = LaunchMethod.Direct;
    public string? SteamAppId { get; set; }
    public string? LaunchUri { get; set; }
    public string? Store { get; set; }
    public string LaunchArgs { get; set; } = "";
    public int InjectDelaySeconds { get; set; } = 25;
    public bool UseOfxr { get; set; } = true;
    public string? CoverImagePath { get; set; }

    // Profile bookkeeping
    public string? AppliedProfileHash { get; set; }
    public string? AppliedProfileName { get; set; }
    public DateTime? AppliedProfileAt { get; set; }
    public string? IgnoredProfileHash { get; set; }

    public DateTime? LastPlayed { get; set; }

    [JsonIgnore]
    public string ExeStem => Path.GetFileNameWithoutExtension(ExePath);

    [JsonIgnore]
    public string ExeFileName => Path.GetFileName(ExePath);
}

public sealed class AppSettings
{
    /// <summary>OFXR Bridge tray app (optional). Empty = look for it automatically.</summary>
    public string OfxrTrayPath { get; set; } = "";

    /// <summary>Virtual Desktop Streamer (optional: SteamVR/Link users don't need it). Empty = find it automatically.</summary>
    public string VdStreamerPath { get; set; } = "";


    /// <summary>
    /// Folders searched for UEVR builds (each up to two levels deep). Picking a single build's folder also works:
    /// its parent is searched for sibling builds. Common places are searched automatically on top of these.
    /// </summary>
    public List<string> BuildLocations { get; set; } = new();

    /// <summary>Individual build folders picked with "Other folder…", remembered for every game.</summary>
    public List<string> KnownBuilds { get; set; } = new();

    /// <summary>Where "Get UEVR builds" installs downloads; empty = next to your existing builds.</summary>
    public string? BuildInstallFolder { get; set; }

    public string ProfilesRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod");
    /// <summary>Where profile backups go. Empty = "profile-backups" in the launcher's data folder.</summary>
    public string BackupsRoot { get; set; } = "";
    public bool VoicePrompts { get; set; } = true;
    public int HeadsetPollSeconds { get; set; } = 2;
    public bool GamepadInjectCombo { get; set; } = true;

    /// <summary>Folders checked for newly downloaded profile zips (named after the game's exe).</summary>
    public List<string> ProfileWatchFolders { get; set; } = new() { KnownFolders.Downloads };

    /// <summary>Extra folders (besides Steam/Epic/GOG) searched by "Scan for games".</summary>
    public List<string> GameScanFolders { get; set; } = new();
}

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p)!;
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
                    return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
        uint flags, IntPtr token, out IntPtr path);
}
