using System.IO;
using HeadStartVR.Models;

namespace HeadStartVR.Services;

/// <summary>
/// Keeps one launcher running and lets desktop shortcuts (HeadStartVR.exe --launch &lt;game&gt;) hand their
/// request to it. Requests are small files in data\requests: that works even though the running launcher is
/// elevated and the shortcut starts unelevated (window messages and pipes would be blocked between the two).
/// </summary>
public static class InstanceBridge
{
    private const string MutexName = @"Local\HeadStartVR.Instance";
    private static Mutex? _mutex;

    public static bool IsAnotherInstanceRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var m))
            {
                m.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The running (elevated) instance owns it; an unelevated process may not open it, but it exists.
            return true;
        }
    }

    public static void Claim() => _mutex = new Mutex(true, MutexName, out _);

    private static string RequestsDir(string dataDir) => Path.Combine(dataDir, "requests");

    public static bool TrySend(string dataDir, string command)
    {
        try
        {
            var dir = RequestsDir(dataDir);
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, command);
            File.Move(tmp, Path.ChangeExtension(tmp, ".req"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static FileSystemWatcher Listen(string dataDir, Action<string> onRequest)
    {
        var dir = RequestsDir(dataDir);
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.EnumerateFiles(dir))
            try { File.Delete(stale); } catch { }

        var watcher = new FileSystemWatcher(dir, "*.req") { NotifyFilter = NotifyFilters.FileName };
        watcher.Renamed += (_, e) => Handle(e.FullPath, onRequest);
        watcher.Created += (_, e) => Handle(e.FullPath, onRequest);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static void Handle(string path, Action<string> onRequest)
    {
        if (!path.EndsWith(".req", StringComparison.OrdinalIgnoreCase)) return;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                var text = File.ReadAllText(path).Trim();
                File.Delete(path);
                onRequest(text);
                return;
            }
            catch (FileNotFoundException)
            {
                return; // already handled (Created + Renamed both fired)
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

public static class Shortcuts
{
    /// <summary>Creates "&lt;Game&gt; (VR).lnk" on the desktop that runs this launcher with --launch &lt;id&gt;.</summary>
    public static string CreateDesktopShortcut(GameEntry game)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnk = Path.Combine(desktop, ProfileManager.SafeName(game.Name) + " (VR).lnk");
        var exe = Environment.ProcessPath!;

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = exe;
        sc.Arguments = LaunchArguments(game);
        sc.WorkingDirectory = Path.GetDirectoryName(exe);
        sc.IconLocation = (File.Exists(game.ExePath) ? game.ExePath : exe) + ",0";
        sc.Description = $"Launch {game.Name} in VR with HeadStart VR";
        sc.Save();
        return lnk;
    }

    public static string LaunchArguments(GameEntry game) => $"--launch {game.Id}";

    public static string LaunchCommand(GameEntry game) => $"\"{Environment.ProcessPath}\" {LaunchArguments(game)}";
}
