using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HeadStartVR.Models;
using HeadStartVR.Services;
using HeadStartVR.ViewModels;

namespace HeadStartVR.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly Store _store = App.Store;
    private readonly ProfileManager _profiles;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _downloadsDebounce = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<FileSystemWatcher> _watchers = new();
    private FileSystemWatcher? _requests;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _profiles = new ProfileManager(_store.Settings);

        foreach (var g in _store.Games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name))
            _vm.Games.Add(new GameItem(g, _profiles));
        _vm.Selected = _vm.Games.FirstOrDefault();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsAdmin) or nameof(MainViewModel.RuntimeOk) or nameof(MainViewModel.VdRunning)
                or nameof(MainViewModel.VdInstalled) or nameof(MainViewModel.OfxrArmed))
                UpdateStatusBar();
        };
        _vm.VdStreamerPath = _store.Settings.VdStreamerPath;
        _vm.RefreshStatus();
        UpdateStatusBar();
        _statusTimer.Tick += (_, _) => _vm.RefreshStatus();
        _statusTimer.Start();

        _downloadsDebounce.Tick += (_, _) =>
        {
            _downloadsDebounce.Stop();
            CheckDownloads(announce: true);
        };
        WatchDownloadFolders();

        StateChanged += (_, _) =>
        {
            // A maximized WindowChrome window overhangs the screen by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        Loaded += (_, _) =>
        {
            if (App.ElevationDeclined)
                Dialog.Show(this, "Running without admin",
                    "You declined the administrator prompt. The launcher still works, but Windows will ask for permission when it injects UEVR — which may be while you're wearing the headset.",
                    ("OK", "ok", true));

            if (!string.IsNullOrEmpty(App.DataDir))
                _requests = InstanceBridge.Listen(App.DataDir, cmd => Dispatcher.BeginInvoke(() => HandleRequest(cmd)));
            if (App.LaunchRequest != null) HandleRequest("launch " + App.LaunchRequest);
            CheckDownloads(announce: true, startup: true);
        };
    }

    private void UpdateStatusBar()
    {
        Brush ok = (Brush)FindResource("SuccessBrush"), warn = (Brush)FindResource("WarningBrush"), off = (Brush)FindResource("MutedBrush");
        AdminDot.Fill = _vm.IsAdmin ? ok : warn;
        AdminText.Text = _vm.IsAdmin ? "Administrator" : "Not elevated";
        RuntimeDot.Fill = _vm.RuntimeOk ? ok : warn;
        VdDot.Fill = _vm.VdRunning ? ok : off;
        VdText.Text = !_vm.VdInstalled ? "Virtual Desktop not installed" : _vm.VdRunning ? "VD Streamer running" : "VD Streamer not running";
        OfxrDot.Fill = _vm.OfxrArmed ? ok : off;
        OfxrText.Text = _vm.OfxrArmed ? "OFXR armed"
            : Ofxr.IsInstalled(_store.Settings.OfxrTrayPath) ? "OFXR not armed" : "OFXR not installed";
    }

    private GameItem? Current => _vm.Selected;

    // ---- Requests from desktop shortcuts ---------------------------------------------------------

    private void HandleRequest(string command)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!command.StartsWith("launch ", StringComparison.OrdinalIgnoreCase)) return;

        var key = command[7..].Trim().Trim('"');
        var item = _vm.Games.FirstOrDefault(g => g.Game.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
                   ?? _vm.Games.FirstOrDefault(g => g.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            Dialog.Show(this, "Game not found", $"No game “{key}” in the library. The shortcut may belong to a game you removed.", ("OK", "ok", true));
            return;
        }
        if (_vm.Session?.IsRunning == true)
        {
            Dialog.Show(this, "Launch in progress", $"{_vm.Session.GameName} is still launching or running from here.", ("OK", "ok", true));
            return;
        }
        _vm.Session = null;
        _vm.Selected = item;
        StartLaunch(item);
    }

    // ---- Downloads watcher -----------------------------------------------------------------------

    private void WatchDownloadFolders()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in _store.Settings.ProfileWatchFolders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(folder, "*.zip") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { _downloadsDebounce.Stop(); _downloadsDebounce.Start(); });
                w.Created += changed;
                w.Changed += changed;
                w.Renamed += (s, e) => changed(s, e); // browsers rename .crdownload/.part to .zip when done
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Write($"Can't watch {folder}: {ex.Message}");
            }
        }
    }

    /// <summary>Re-check every game for new profile zips; show a banner for newly found downloads.</summary>
    private void CheckDownloads(bool announce, bool startup = false)
    {
        GameItem? newlyFound = null;
        foreach (var item in _vm.Games)
        {
            var before = startup ? null : item.Pending?.ZipPath;
            item.RefreshProfileStatus();
            if (item.Pending is { FromWatchedFolder: true } p && !string.Equals(before, p.ZipPath, StringComparison.OrdinalIgnoreCase))
                newlyFound ??= item;
        }
        if (announce && newlyFound != null && _vm.Session == null)
        {
            _vm.BannerGame = newlyFound;
            _vm.BannerText = $"New UEVR profile for {newlyFound.Name}: {Path.GetFileName(newlyFound.Pending!.ZipPath)}";
        }
    }

    private void BannerReview_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.BannerGame != null) _vm.Selected = _vm.BannerGame;
        _vm.BannerText = null;
    }

    private void BannerDismiss_Click(object sender, RoutedEventArgs e) => _vm.BannerText = null;

    // ---- Library ---------------------------------------------------------------------------------

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var editor = new GameEditorWindow(null, _store.Settings) { Owner = this };
        if (editor.ShowDialog() != true) return;

        var game = editor.Result!;
        _store.Games.Add(game);
        AfterEditProfileCheck(game, isNew: true);
        _store.SaveGames();
        _store.SaveSettings(); // builds picked with Other folder… / Get builds…
        var item = new GameItem(game, _profiles);
        _vm.Games.Insert(0, item);
        _vm.Selected = item;
    }

    private void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        var w = new ScanWindow(_store.Settings, _store.Games, () => _store.SaveSettings()) { Owner = this };
        if (w.ShowDialog() != true || w.Added.Count == 0) return;
        GameItem? first = null;
        foreach (var g in w.Added)
        {
            _store.Games.Add(g);
            var item = new GameItem(g, _profiles);
            _vm.Games.Add(item);
            first ??= item;
        }
        _store.SaveGames();
        _vm.Selected = first;
    }

    private void EditGame_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var oldZip = Current.Game.ProfileZipPath;
        var editor = new GameEditorWindow(Current.Game, _store.Settings) { Owner = this };
        if (editor.ShowDialog() != true) return;
        if (!string.Equals(oldZip, Current.Game.ProfileZipPath, StringComparison.OrdinalIgnoreCase))
            AfterEditProfileCheck(Current.Game, isNew: false);
        _store.SaveGames();
        _store.SaveSettings(); // builds picked with Other folder… / Get builds…
        Current.Refresh();
    }

    /// <summary>When a zip gets linked to a game whose profile already exists, ask whether it's already applied.</summary>
    private void AfterEditProfileCheck(GameEntry game, bool isNew)
    {
        if (string.IsNullOrEmpty(game.ProfileZipPath) || !File.Exists(game.ProfileZipPath)) return;
        if (!_profiles.ProfileExists(game))
        {
            var a = Dialog.Show(this, "Install profile?",
                $"There's no UEVR profile for {game.ExeStem} yet. Install {Path.GetFileName(game.ProfileZipPath)} now?",
                ("Install now", "apply", true), ("Later", "later", false));
            if (a == "apply") ApplyProfile(game, game.ProfileZipPath);
            return;
        }

        var answer = Dialog.Show(this, "Existing profile found",
            $"{game.ExeStem} already has a UEVR profile in\n{_profiles.ProfileFolder(game)}\n\n" +
            $"Is {Path.GetFileName(game.ProfileZipPath)} the profile that's already installed there?",
            ("Yes, it's already installed", "mark", true),
            ("No, install it now", "apply", false),
            ("Decide at next launch", "later", false));
        if (answer == "mark") ProfileManager.MarkApplied(game);
        else if (answer == "apply") ApplyProfile(game, game.ProfileZipPath);
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var a = Dialog.Show(this, "Remove game",
            $"Remove {Current.Name} from the launcher?\n\nThis only removes the library entry. The game, its UEVR build and its profile stay where they are.",
            ("Remove", "remove", true), ("Cancel", "cancel", false));
        if (a != "remove") return;
        _store.Games.Remove(Current.Game);
        _store.SaveGames();
        var idx = _vm.Games.IndexOf(Current);
        _vm.Games.Remove(Current);
        _vm.Selected = _vm.Games.Count == 0 ? null : _vm.Games[Math.Min(idx, _vm.Games.Count - 1)];
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        try
        {
            var lnk = Shortcuts.CreateDesktopShortcut(Current.Game);
            var a = Dialog.Show(this, "Desktop shortcut created",
                $"{Path.GetFileName(lnk)} is on your desktop. It opens the launcher (or uses the one already open) and starts the VR launch right away.\n\n" +
                $"To start it from Playnite, a Steam shortcut or a script, use:\n{Shortcuts.LaunchCommand(Current.Game)}",
                ("OK", "ok", true), ("Copy command", "copy", false));
            if (a == "copy") Clipboard.SetText(Shortcuts.LaunchCommand(Current.Game));
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "Couldn't create the shortcut", ex.Message, ("OK", "ok", true));
        }
    }

    // ---- Profiles --------------------------------------------------------------------------------

    private void UpdateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var dlg = new OpenFileDialog
        {
            Title = $"Pick the new UEVR profile zip for {Current.Name}",
            Filter = "UEVR profile (*.zip)|*.zip",
            InitialDirectory = _store.Settings.ProfileWatchFolders.FirstOrDefault(Directory.Exists)
                               ?? (Directory.Exists(Current.Game.UevrBuildPath) ? Current.Game.UevrBuildPath : ""),
        };
        if (dlg.ShowDialog(this) != true) return;
        ApplyProfile(Current.Game, dlg.FileName);
        _store.SaveGames();
        Current.Refresh();
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Pending is not { } pending) return;
        ApplyProfile(Current.Game, pending.ZipPath);
        _store.SaveGames();
        Current.Refresh();
        if (_vm.BannerGame == Current) _vm.BannerText = null;
    }

    private void IgnoreProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Pending is not { } pending) return;
        Current.Game.IgnoredProfileHash = pending.Hash;
        _store.SaveGames();
        Current.RefreshProfileStatus();
        if (_vm.BannerGame == Current) _vm.BannerText = null;
    }

    private void FindOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        new OnlineProfilesWindow(Current.Game, _store.Settings) { Owner = this }.ShowDialog();
    }

    private void Backups_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var w = new BackupsWindow(Current.Game, _profiles) { Owner = this };
        w.ShowDialog();
        if (!w.Restored) return;
        _store.SaveGames();
        Current.Refresh();
    }

    private bool ApplyProfile(GameEntry game, string zip)
    {
        try
        {
            if (ProfileManager.ZipContainsDll(zip))
            {
                var a = Dialog.Show(this, "Profile contains plugins",
                    "This profile includes DLL plugins, which run code inside the game. Only apply it if it comes from a trusted author.",
                    ("Apply anyway", "go", true), ("Cancel", "cancel", false));
                if (a != "go") return false;
            }
            var r = _profiles.Apply(game, zip);
            Dialog.Show(this, "Profile updated",
                $"Installed {Path.GetFileName(zip)} ({r.FilesWritten} files) into\n{r.ProfileFolder}" +
                (r.BackupZip != null ? $"\n\nThe previous profile was backed up to\n{r.BackupZip}" : ""),
                ("OK", "ok", true));
            return true;
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "Couldn't update the profile", ex.Message, ("OK", "ok", true));
            return false;
        }
    }

    private void OpenProfileFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var dir = _profiles.ProfileFolder(Current.Game);
        if (!Directory.Exists(dir))
        {
            Dialog.Show(this, "No profile yet", $"{dir} doesn't exist yet. UEVR creates it on the first injection, or install a profile zip.", ("OK", "ok", true));
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
    }

    // ---- Launch ----------------------------------------------------------------------------------

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Current != null) StartLaunch(Current);
    }

    private async void StartLaunch(GameItem item)
    {
        if (_vm.Session != null) return;
        _vm.BannerText = null;
        var session = new LaunchSession(item.Game, _store.Settings, _profiles, () => _store.SaveGames());
        session.Steps.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() => StepsScroller.ScrollToEnd(), DispatcherPriority.Background);
        _vm.Session = session;
        await session.RunAsync();
        item.Refresh();
        _vm.RefreshStatus();
    }

    private void BackToLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Session?.IsRunning == true) return;
        _vm.Session = null;
    }

    // ---- Settings & chrome -----------------------------------------------------------------------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_store.Settings) { Owner = this };
        if (w.ShowDialog() != true) return;
        Store.FillDefaults(_store.Settings, _store.DataDir); // cleared boxes go back to what's detected
        _store.SaveSettings();
        Voice.Enabled = _store.Settings.VoicePrompts;
        WatchDownloadFolders();
        _vm.VdStreamerPath = _store.Settings.VdStreamerPath;
        _vm.RefreshStatus();
        UpdateStatusBar();
        foreach (var g in _vm.Games) g.Refresh();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.Session?.IsRunning == true)
        {
            var a = Dialog.Show(this, "Launch in progress",
                "A launch is still in progress. Close the launcher anyway? The game keeps running.",
                ("Close", "close", true), ("Stay", "stay", false));
            if (a != "close")
            {
                e.Cancel = true;
                return;
            }
            _vm.Session.Cancel();
        }
        foreach (var w in _watchers) w.Dispose();
        _requests?.Dispose();
        base.OnClosing(e);
    }
}
