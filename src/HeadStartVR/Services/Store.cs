using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeadStartVR.Models;

namespace HeadStartVR.Services;

/// <summary>Loads and saves games.json / settings.json in the data folder (next to the exe by default).</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DataDir { get; }
    private string GamesFile => Path.Combine(DataDir, "games.json");
    private string SettingsFile => Path.Combine(DataDir, "settings.json");

    public List<GameEntry> Games { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();

    public Store(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(DataDir);
        Games = Load<List<GameEntry>>(GamesFile) ?? new();
        Settings = Load<AppSettings>(SettingsFile) ?? new();
        FillDefaults(Settings, DataDir);
    }

    /// <summary>Settings left empty are filled from what's installed on this PC (re-checked on every start).</summary>
    public static void FillDefaults(AppSettings s, string dataDir)
    {
        if (string.IsNullOrWhiteSpace(s.OfxrTrayPath)) s.OfxrTrayPath = Detect.FindOfxrTray() ?? "";
        if (string.IsNullOrWhiteSpace(s.VdStreamerPath)) s.VdStreamerPath = Detect.FindVdStreamer() ?? "";
        if (string.IsNullOrWhiteSpace(s.BackupsRoot)) s.BackupsRoot = Path.Combine(dataDir, "profile-backups");
    }

    public void SaveGames() => Save(GamesFile, Games);
    public void SaveSettings() => Save(SettingsFile, Settings);

    private static T? Load<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for inspection rather than silently overwriting it.
            var broken = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, broken, overwrite: true);
            Log.Write($"Could not read {path} ({ex.Message}); copied to {broken}");
            return default;
        }
    }

    private static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Tiny append-only log in the data folder, useful when something goes wrong mid-launch.</summary>
public static class Log
{
    private static string? _file;
    private static readonly object Gate = new();

    public static void Init(string dataDir) => _file = Path.Combine(dataDir, "launcher.log");

    public static void Write(string message)
    {
        if (_file == null) return;
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > 2_000_000) File.Delete(_file);
                File.AppendAllText(_file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break a launch.
        }
    }
}
