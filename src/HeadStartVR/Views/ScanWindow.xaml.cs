using System.IO;
using System.Windows;
using System.Windows.Media;
using HeadStartVR.Models;
using HeadStartVR.Services;
using HeadStartVR.ViewModels;

namespace HeadStartVR.Views;

public partial class ScanWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HashSet<string> _known;
    private List<Row> _rows = new();

    public List<GameEntry> Added { get; } = new();

    /// <summary>Placeholder entries so the dropdown can show "No build yet" and "Other folder…".</summary>
    private static readonly UevrBuild NoBuild = new("", "No build yet", null);
    private static readonly UevrBuild OtherFolder = new("", "Other folder…", null);

    /// <summary>Shared by every row's dropdown, so a build added for one game is offered to all.</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<UevrBuild> _choices = new();
    private List<UevrBuild> _builds = new();

    public sealed class Row : ObservableObject
    {
        public required FoundGame Found { get; init; }
        public required IEnumerable<UevrBuild> Builds { get; init; }
        public string Name => Found.Name;
        public string Store => Found.Store;
        public string ExePath => Found.ExePath;
        public ImageSource? Icon { get; init; }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { Set(ref _isSelected, value); SelectionChanged?.Invoke(); } }

        private UevrBuild? _build;
        public UevrBuild? Build
        {
            get => _build;
            set
            {
                if (value == OtherFolder)
                {
                    PickOther?.Invoke(this); // keeps the current build until a folder is picked
                    return;
                }
                Set(ref _build, value);
            }
        }

        public void RefreshBuild() => Raise(nameof(Build));

        public Action? SelectionChanged { get; set; }
        public Action<Row>? PickOther { get; set; }
    }

    private readonly Action _saveSettings;

    public ScanWindow(AppSettings settings, IEnumerable<GameEntry> existing, Action saveSettings)
    {
        InitializeComponent();
        _settings = settings;
        _saveSettings = saveSettings;
        _known = existing.Select(g => g.ExePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Loaded += async (_, _) => await ScanAsync();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder where you keep games" };
        if (dlg.ShowDialog(this) != true) return;
        if (!_settings.GameScanFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            _settings.GameScanFolders.Add(dlg.FolderName);
            _saveSettings();
        }
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        AddFolderButton.IsEnabled = false;
        SubText.Text = "Searching Steam, Epic and GOG libraries and your game folders…";
        var folders = _settings.GameScanFolders.ToList();
        var searched = GameScanner.FoldersToScan(folders);
        var found = await Task.Run(() => GameScanner.Scan(folders));
        AddFolderButton.IsEnabled = true;
        var where = "Searched Steam, Epic, GOG" + (searched.Count > 0 ? " and " + string.Join(", ", searched) : "") + ".";
        // Keep ticks and build choices when rescanning after "Add folder…".
        var previous = _rows.ToDictionary(r => r.ExePath, StringComparer.OrdinalIgnoreCase);
        LoadBuilds();

        _rows = found.Where(f => !_known.Contains(f.ExePath)).Select(f => new Row
        {
            Found = f,
            Builds = _choices,
            Icon = IconHelper.ExtractIcon(f.ExePath, 48),
        }).ToList();
        foreach (var r in _rows)
        {
            if (previous.TryGetValue(r.ExePath, out var old))
            {
                r.Build = FindChoice(old.Build?.Folder) ?? NoBuild;
                r.IsSelected = old.IsSelected;
            }
            else
            {
                r.Build = GuessBuild(r.Found, _builds) ?? NoBuild;
            }
            r.SelectionChanged = UpdateAddButton;
            r.PickOther = row => Dispatcher.BeginInvoke(() => PickOtherBuild(row));
        }

        List.ItemsSource = _rows;
        UpdateAddButton();
        var already = found.Count - _rows.Count;
        SubText.Text = (_rows.Count == 0
            ? (found.Count == 0
                ? "No Unreal Engine games found. Use Add folder… to search where you keep your games."
                : $"All {found.Count} Unreal Engine games found are already in your library.")
            : $"Found {_rows.Count} Unreal Engine game{(_rows.Count == 1 ? "" : "s")}" +
              (already > 0 ? $" ({already} already in your library)" : "") +
              ". Pick the ones to add and the UEVR build each should use.") + "\n" + where;
    }

    private void LoadBuilds()
    {
        _builds = UevrBuilds.Discover(_settings, GameEditorWindow.LibraryBuildFolders());
        _choices.Clear();
        _choices.Add(NoBuild);
        foreach (var b in _builds) _choices.Add(b);
        _choices.Add(OtherFolder);
    }

    private UevrBuild? FindChoice(string? folder) =>
        string.IsNullOrEmpty(folder) ? null : _builds.FirstOrDefault(b => b.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reload the build list (after Other folder… or a download) and keep every row's choice.</summary>
    private void RefreshBuilds()
    {
        var keep = _rows.ToDictionary(r => r, r => r.Build?.Folder);
        LoadBuilds();
        foreach (var (row, folder) in keep) row.Build = FindChoice(folder) ?? NoBuild;
    }

    private void PickOtherBuild(Row row)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Pick the UEVR build folder for {row.Name} (contains UEVRInjector.exe)" };
        if (dlg.ShowDialog(this) == true)
        {
            if (!UevrBuilds.IsValidBuild(dlg.FolderName))
            {
                Dialog.Show(this, "Not a UEVR build", "That folder has no UEVRInjector.exe. Pick the folder of one UEVR build.", ("OK", "ok", true));
            }
            else
            {
                if (!_settings.KnownBuilds.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.KnownBuilds.Add(dlg.FolderName);
                    _saveSettings();
                }
                RefreshBuilds();
                row.Build = FindChoice(dlg.FolderName) ?? NoBuild;
                return;
            }
        }
        row.RefreshBuild(); // dialog cancelled: show the previous choice again
    }

    private void GetBuilds_Click(object sender, RoutedEventArgs e)
    {
        var w = new BuildsWindow(_settings, GameEditorWindow.LibraryBuildFolders()) { Owner = this };
        w.ShowDialog();
        foreach (var dir in w.Installed)
            if (!_settings.KnownBuilds.Contains(dir, StringComparer.OrdinalIgnoreCase)) _settings.KnownBuilds.Add(dir);
        _saveSettings();
        RefreshBuilds();
        // Give the new build to ticked games that don't have one yet.
        if (w.Installed.LastOrDefault() is { } fresh && FindChoice(fresh) is { } b)
            foreach (var r in _rows.Where(r => r.IsSelected && r.Build == NoBuild)) r.Build = b;
    }

    /// <summary>A build folder that holds "&lt;GameExe&gt;.zip" or mentions the game's name is probably the right one.</summary>
    private static UevrBuild? GuessBuild(FoundGame f, List<UevrBuild> builds)
    {
        var stem = Path.GetFileNameWithoutExtension(f.ExePath);
        var byZip = builds.FirstOrDefault(b => File.Exists(Path.Combine(b.Folder, stem + ".zip")));
        if (byZip != null) return byZip;
        var words = f.Name.Split(new[] { ' ', '-', ':', '_' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3);
        return builds.FirstOrDefault(b => words.Any(w => b.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateAddButton()
    {
        var n = _rows.Count(r => r.IsSelected);
        AddButton.IsEnabled = n > 0;
        AddButton.Content = n == 0 ? "Add selected" : $"Add {n} game{(n == 1 ? "" : "s")}";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var useOfxr = File.Exists(_settings.OfxrTrayPath);
        foreach (var r in _rows.Where(r => r.IsSelected))
        {
            var f = r.Found;
            Added.Add(new GameEntry
            {
                Name = f.Name,
                ExePath = f.ExePath,
                UevrBuildPath = r.Build == NoBuild ? "" : r.Build?.Folder ?? "",
                Store = f.Store == "Folder" ? null : f.Store,
                SteamAppId = f.SteamAppId,
                // Epic games usually need their launcher to sign in, so start them through it.
                LaunchMethod = f.LaunchUri != null ? LaunchMethod.Uri : LaunchMethod.Direct,
                LaunchUri = f.LaunchUri,
                UseOfxr = useOfxr,
            });
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
