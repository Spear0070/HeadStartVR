using System.IO;
using System.Windows;
using System.Windows.Controls;
using HeadStartVR.Models;
using HeadStartVR.Services;

namespace HeadStartVR.Views;

public partial class BuildsWindow : Window
{
    private readonly AppSettings _settings;
    private string _installFolder;
    private CancellationTokenSource? _cts;

    /// <summary>Build folders installed while the window was open (the caller remembers them and saves settings).</summary>
    public List<string> Installed { get; } = new();

    public BuildsWindow(AppSettings settings, IEnumerable<string>? gameBuildFolders = null)
    {
        InitializeComponent();
        _settings = settings;
        _installFolder = UevrBuilds.InstallFolder(settings, UevrBuilds.Discover(settings, gameBuildFolders));
        InstallBox.Text = _installFolder;
        Sources.ItemsSource = BuildSource.All;
        Loaded += (_, _) => Sources.SelectedIndex = 0;
        Closing += (_, e) =>
        {
            if (_cts != null)
            {
                e.Cancel = Dialog.Show(this, "Download in progress", "Stop the download?", ("Stop", "stop", true), ("Keep going", "keep", false)) != "stop";
                if (!e.Cancel) _cts.Cancel();
            }
        };
    }

    private BuildSource? Source => Sources.SelectedItem as BuildSource;
    private BuildChoice? Choice => Variants.SelectedItem as BuildChoice;

    private async void Sources_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Source is not { } src) return;
        SourceTitle.Text = src.Title;
        SourceRepo.Text = "github.com/" + src.Repo;
        Versions.ItemsSource = null;
        Variants.ItemsSource = null;
        InstallButton.IsEnabled = false;
        StatusText.Text = "Loading releases from GitHub…";
        try
        {
            var releases = (await BuildDownloader.GetReleasesAsync(src, CancellationToken.None))
                .Where(r => BuildDownloader.Choices(src, r).Count > 0).ToList();
            if (Source != src) return; // user moved on
            Versions.ItemsSource = releases;
            Versions.SelectedIndex = releases.Count > 0 ? 0 : -1;
            StatusText.Text = releases.Count > 0 ? "" : "No downloadable builds found in this source's recent releases.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't reach GitHub: " + ex.Message + (ex.Message.Contains("403") ? " (GitHub limits how often it can be asked; try again in a while)" : "");
        }
    }

    private void Versions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Source is not { } src || Versions.SelectedItem is not Release r) return;
        var choices = BuildDownloader.Choices(src, r);
        Variants.ItemsSource = choices;
        Variants.SelectedIndex = 0;
        VariantPanel.Visibility = choices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Variants_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTarget();

    private void UpdateTarget()
    {
        if (Choice is not { } c)
        {
            TargetText.Text = "";
            InstallButton.IsEnabled = false;
            return;
        }
        var dest = Path.Combine(_installFolder, c.FolderName);
        var installed = Directory.Exists(dest);
        TargetText.Text = (installed ? "Already installed as " : "Will be installed as ") + dest +
                          (c.Checksum != null ? "\nThe download is checked against the checksum the author publishes." : "\nThis source doesn't publish a checksum.");
        InstallLabel.Text = installed ? "Already installed" : $"Download & install ({c.Asset.Size / 1048576.0:N0} MB)";
        InstallButton.IsEnabled = !installed && _cts == null;
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should downloaded UEVR builds go?" };
        if (Directory.Exists(_installFolder)) dlg.InitialDirectory = _installFolder;
        if (dlg.ShowDialog(this) != true) return;
        _installFolder = dlg.FolderName;
        _settings.BuildInstallFolder = dlg.FolderName;
        InstallBox.Text = _installFolder;
        UpdateTarget();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Choice is not { } c) return;
        _cts = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        Sources.IsEnabled = Versions.IsEnabled = Variants.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<(double p, string text)>(x =>
        {
            ProgressScale.ScaleX = x.p;
            StatusText.Text = x.text;
        });
        try
        {
            var dest = await BuildDownloader.InstallAsync(c, _installFolder, progress, _cts.Token);
            Installed.Add(dest);
            StatusText.Text = $"Installed {Path.GetFileName(dest)}. Pick it in any game's UEVR build list.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Download stopped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Not installed: " + ex.Message;
            Log.Write("Build install failed: " + ex);
        }
        finally
        {
            _cts = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            Sources.IsEnabled = Versions.IsEnabled = Variants.IsEnabled = true;
            UpdateTarget();
        }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        var url = (Versions.SelectedItem as Release)?.PageUrl ?? Source?.PageUrl;
        if (url != null) ProcessLauncher.OpenUriAsUser(url);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
