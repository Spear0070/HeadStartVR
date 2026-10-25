using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using HeadStartVR.Models;

namespace HeadStartVR.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly System.Windows.Threading.DispatcherTimer _buildsDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        OfxrBox.Text = settings.OfxrTrayPath;
        VdBox.Text = settings.VdStreamerPath;
        BuildsBox.Text = string.Join(Environment.NewLine, settings.BuildLocations);
        InstallBox.Text = settings.BuildInstallFolder ?? "";
        _buildsDebounce.Tick += (_, _) =>
        {
            _buildsDebounce.Stop();
            ShowBuildsFound();
        };
        Loaded += (_, _) => ShowBuildsFound();
        ProfilesBox.Text = settings.ProfilesRoot;
        BackupsBox.Text = settings.BackupsRoot;
        VoiceCheck.IsChecked = settings.VoicePrompts;
        ComboCheck.IsChecked = settings.GamepadInjectCombo;
        PollBox.Text = settings.HeadsetPollSeconds.ToString();
        WatchBox.Text = string.Join(Environment.NewLine, settings.ProfileWatchFolders);
        ScanBox.Text = string.Join(Environment.NewLine, settings.GameScanFolders);
    }

    private void BuildsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _buildsDebounce.Stop();
        _buildsDebounce.Start();
    }

    /// <summary>Show right away what the folders in the box lead to, so a wrong pick is obvious.</summary>
    private void ShowBuildsFound()
    {
        var preview = new AppSettings
        {
            BuildLocations = Lines(BuildsBox),
            KnownBuilds = _settings.KnownBuilds,
            BuildInstallFolder = InstallBox.Text.Trim().Length > 0 ? InstallBox.Text.Trim() : null,
        };
        var builds = Services.UevrBuilds.Discover(preview, GameEditorWindow.LibraryBuildFolders());
        BuildsFound.Text = builds.Count == 0
            ? "No UEVR builds found yet. Add the folder where you unzipped them, or use Get builds…"
            : $"Found {builds.Count} UEVR build{(builds.Count == 1 ? "" : "s")}: " +
              string.Join(", ", builds.Select(b => $"{b.Name} ({Path.GetDirectoryName(b.Folder.TrimEnd('\\'))})"));
    }

    private void GetBuilds_Click(object sender, RoutedEventArgs e)
    {
        var w = new BuildsWindow(_settings, GameEditorWindow.LibraryBuildFolders()) { Owner = this };
        w.ShowDialog();
        foreach (var dir in w.Installed)
            if (!_settings.KnownBuilds.Contains(dir, StringComparer.OrdinalIgnoreCase)) _settings.KnownBuilds.Add(dir);
        InstallBox.Text = _settings.BuildInstallFolder ?? InstallBox.Text;
        ShowBuildsFound();
    }

    private static List<string> Lines(TextBox box) =>
        box.Text.Split('\n').Select(l => l.Trim().Trim('"')).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void AddFolderLine_Click(object sender, RoutedEventArgs e)
    {
        var box = BoxFor(sender);
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) != true) return;
        var lines = Lines(box);
        if (!lines.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) lines.Add(dlg.FolderName);
        box.Text = string.Join(Environment.NewLine, lines);
        if (box == BuildsBox) ShowBuildsFound();
    }

    private TextBox BoxFor(object sender) => (TextBox)FindName((string)((Button)sender).Tag);

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var box = BoxFor(sender);
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
        if (File.Exists(box.Text)) dlg.InitialDirectory = Path.GetDirectoryName(box.Text);
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FileName;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var box = BoxFor(sender);
        var dlg = new OpenFolderDialog();
        if (Directory.Exists(box.Text)) dlg.InitialDirectory = box.Text;
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollBox.Text.Trim(), out var poll) || poll < 1 || poll > 10)
        {
            ErrorText.Text = "Headset check interval must be 1–10 seconds.";
            return;
        }
        _settings.OfxrTrayPath = OfxrBox.Text.Trim().Trim('"');
        _settings.VdStreamerPath = VdBox.Text.Trim().Trim('"');
        _settings.BuildLocations = Lines(BuildsBox);
        _settings.BuildInstallFolder = InstallBox.Text.Trim().Trim('"') is { Length: > 0 } install ? install : null;
        _settings.ProfilesRoot = ProfilesBox.Text.Trim().Trim('"');
        _settings.BackupsRoot = BackupsBox.Text.Trim().Trim('"');
        _settings.VoicePrompts = VoiceCheck.IsChecked == true;
        _settings.GamepadInjectCombo = ComboCheck.IsChecked == true;
        _settings.HeadsetPollSeconds = poll;
        _settings.ProfileWatchFolders = Lines(WatchBox);
        _settings.GameScanFolders = Lines(ScanBox);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
