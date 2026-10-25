using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using HeadStartVR.Models;
using HeadStartVR.Services;

namespace HeadStartVR.Views;

public partial class GameEditorWindow : Window
{
    private readonly GameEntry? _existing;
    private readonly AppSettings _settings;
    private List<UevrBuild> _builds;
    private bool _nameEdited;

    public GameEntry? Result { get; private set; }

    /// <summary>Build folders already used by games in the library (always offered).</summary>
    internal static IEnumerable<string> LibraryBuildFolders() =>
        App.Store?.Games.Select(g => g.UevrBuildPath).Where(p => !string.IsNullOrEmpty(p)) ?? Enumerable.Empty<string>();

    public GameEditorWindow(GameEntry? existing, AppSettings settings)
    {
        InitializeComponent();
        _existing = existing;
        _settings = settings;
        _builds = UevrBuilds.Discover(settings, LibraryBuildFolders());
        BuildCombo.ItemsSource = _builds;

        if (existing != null)
        {
            HeaderText.Text = "Edit " + existing.Name;
            SaveButton.Content = "Save";
            ExeBox.Text = existing.ExePath;
            NameBox.Text = existing.Name;
            _nameEdited = true;
            SelectBuild(existing.UevrBuildPath);
            ZipBox.Text = existing.ProfileZipPath ?? "";
            SteamIdBox.Text = existing.SteamAppId ?? "";
            UriBox.Text = existing.LaunchUri ?? "";
            (existing.LaunchMethod switch { LaunchMethod.Steam => SteamRadio, LaunchMethod.Uri => UriRadio, _ => DirectRadio }).IsChecked = true;
            ArgsBox.Text = existing.LaunchArgs;
            DelayBox.Text = existing.InjectDelaySeconds.ToString();
            OfxrCheck.IsChecked = existing.UseOfxr;
            CoverBox.Text = existing.CoverImagePath ?? "";
        }
        else
        {
            DelayBox.Text = "25";
            OfxrCheck.IsChecked = File.Exists(settings.OfxrTrayPath);
        }

        NameBox.TextChanged += (_, _) => { if (NameBox.IsKeyboardFocused) _nameEdited = true; };
        UpdateRuntimeStatus();
    }

    private void SelectBuild(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        var match = _builds.FirstOrDefault(b => string.Equals(b.Folder.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (match == null && UevrBuilds.IsValidBuild(folder))
        {
            match = UevrBuilds.FromFolder(folder);
            _builds.Add(match);
            BuildCombo.ItemsSource = null;
            BuildCombo.ItemsSource = _builds;
        }
        BuildCombo.SelectedItem = match;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick the game's -Win64-Shipping.exe",
            Filter = "Game executable (*.exe)|*.exe",
        };
        if (File.Exists(ExeBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(ExeBox.Text);
        if (dlg.ShowDialog(this) != true) return;
        ExeBox.Text = dlg.FileName;
        OnExeChosen();
    }

    private void ExeBox_LostFocus(object sender, RoutedEventArgs e) => OnExeChosen();

    /// <summary>Fill in what we can work out from the exe path: name, Steam app id, matching build and profile zip.</summary>
    private void OnExeChosen()
    {
        var exe = ExeBox.Text.Trim().Trim('"');
        if (!File.Exists(exe)) return;
        var stem = Path.GetFileNameWithoutExtension(exe);

        var inBinaries = Path.GetFileName(Path.GetDirectoryName(exe))?.ToUpperInvariant() is "WIN64" or "WINGDK";
        ExeHint.Text = inBinaries
            ? $"UEVR profile folder: {Path.Combine(_settings.ProfilesRoot, stem)}"
            : "This isn't in a Binaries\\Win64 folder, so it may be a launcher. UEVR needs the real game process (usually …-Win64-Shipping.exe in <Project>\\Binaries\\Win64).";

        if (!_nameEdited || string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = GuessName(exe);

        var appId = SteamHelper.DetectAppId(exe);
        if (appId != null && string.IsNullOrWhiteSpace(SteamIdBox.Text)) SteamIdBox.Text = appId;

        // A build folder that already contains "<GameExe>.zip" (or mentions the game) is probably the right one.
        if (BuildCombo.SelectedItem == null)
        {
            var byZip = _builds.FirstOrDefault(b => File.Exists(Path.Combine(b.Folder, stem + ".zip")));
            var gameWord = NameBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var byName = _builds.FirstOrDefault(b => gameWord.Length > 2 && b.Name.Contains(gameWord, StringComparison.OrdinalIgnoreCase));
            if ((byZip ?? byName) is { } guess) BuildCombo.SelectedItem = guess;
        }
        SuggestZip(stem);
    }

    private void SuggestZip(string stem)
    {
        if (!string.IsNullOrWhiteSpace(ZipBox.Text)) return;
        if (BuildCombo.SelectedItem is UevrBuild b)
        {
            var candidate = Path.Combine(b.Folder, stem + ".zip");
            if (File.Exists(candidate)) ZipBox.Text = candidate;
        }
    }

    /// <summary>&lt;Root&gt;\&lt;Project&gt;\Binaries\Win64\X.exe → folder name of &lt;Root&gt;, made readable.</summary>
    private static string GuessName(string exe)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        if (dir.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) || dir.Name.Equals("WinGDK", StringComparison.OrdinalIgnoreCase))
        {
            var root = dir.Parent?.Parent?.Parent; // Win64 → Binaries → Project → Root
            if (root != null && root.Parent != null) return root.Name;
        }
        var stem = Path.GetFileNameWithoutExtension(exe);
        foreach (var suffix in new[] { "-Win64-Shipping", "-WinGDK-Shipping", "-Shipping" })
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return stem[..^suffix.Length];
        return stem;
    }

    private void BrowseBuild_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick the UEVR build folder (contains UEVRInjector.exe)" };
        if (BuildCombo.SelectedItem is UevrBuild cur) dlg.InitialDirectory = Path.GetDirectoryName(cur.Folder.TrimEnd('\\'));
        if (dlg.ShowDialog(this) != true) return;
        if (!UevrBuilds.IsValidBuild(dlg.FolderName))
        {
            ErrorText.Text = "That folder has no UEVRInjector.exe. Pick the folder of one UEVR build.";
            return;
        }
        // Remember it so every game (and the Scan window) can use it too.
        if (!_settings.KnownBuilds.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) _settings.KnownBuilds.Add(dlg.FolderName);
        SelectBuild(dlg.FolderName);
    }

    private void GetBuilds_Click(object sender, RoutedEventArgs e)
    {
        var w = new BuildsWindow(_settings, LibraryBuildFolders()) { Owner = this };
        w.ShowDialog();
        foreach (var dir in w.Installed)
            if (!_settings.KnownBuilds.Contains(dir, StringComparer.OrdinalIgnoreCase)) _settings.KnownBuilds.Add(dir);
        var selected = (BuildCombo.SelectedItem as UevrBuild)?.Folder;
        _builds = UevrBuilds.Discover(_settings, LibraryBuildFolders());
        BuildCombo.ItemsSource = _builds;
        SelectBuild(w.Installed.LastOrDefault() ?? selected ?? "");
    }

    private void BuildCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRuntimeStatus();
        if (File.Exists(ExeBox.Text)) SuggestZip(Path.GetFileNameWithoutExtension(ExeBox.Text));
    }

    private void UpdateRuntimeStatus()
    {
        if (BuildCombo.SelectedItem is not UevrBuild b)
        {
            RuntimeDot.Fill = (Brush)FindResource("MutedBrush");
            RuntimeText.Text = _builds.Count == 0
                ? "No UEVR builds found yet. Use Get builds… to download one, or Other folder… if you already have one."
                : $"{_builds.Count} UEVR build{(_builds.Count == 1 ? "" : "s")} found. Missing one? Use Other folder… or add its folder in Settings.";
            return;
        }
        var rt = UevrBuilds.GetRuntime(b.InjectorPath);
        RuntimeDot.Fill = (Brush)FindResource(rt == InjectorRuntime.OpenXR ? "SuccessBrush" : "WarningBrush");
        RuntimeText.Text = rt switch
        {
            InjectorRuntime.OpenXR => "This injector is set to OpenXR.",
            InjectorRuntime.OpenVR => "This injector is set to OpenVR. The launcher will offer to switch it to OpenXR.",
            _ => "This injector has never been opened from this folder (it would default to OpenVR). The launcher will offer to set OpenXR.",
        };
    }

    private void BrowseZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Pick the UEVR profile zip", Filter = "UEVR profile (*.zip)|*.zip" };
        if (BuildCombo.SelectedItem is UevrBuild b) dlg.InitialDirectory = b.Folder;
        if (dlg.ShowDialog(this) == true) ZipBox.Text = dlg.FileName;
    }

    private void ClearZip_Click(object sender, RoutedEventArgs e) => ZipBox.Text = "";

    private void BrowseCover_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Pick a cover image", Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp" };
        if (dlg.ShowDialog(this) == true) CoverBox.Text = dlg.FileName;
    }

    private void LaunchMethod_Checked(object sender, RoutedEventArgs e)
    {
        if (SteamRow != null) SteamRow.Visibility = SteamRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (UriRow != null) UriRow.Visibility = UriRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var exe = ExeBox.Text.Trim().Trim('"');
        if (!File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = "Pick the game's .exe file.";
            return;
        }
        if (BuildCombo.SelectedItem is not UevrBuild build)
        {
            ErrorText.Text = "Choose the UEVR build this game uses.";
            return;
        }
        var zip = ZipBox.Text.Trim().Trim('"');
        if (zip.Length > 0 && !File.Exists(zip))
        {
            ErrorText.Text = "The profile zip doesn't exist.";
            return;
        }
        if (!int.TryParse(DelayBox.Text.Trim(), out var delay) || delay < 0 || delay > 600)
        {
            ErrorText.Text = "Inject delay must be 0–600 seconds.";
            return;
        }
        var steam = SteamRadio.IsChecked == true;
        var steamId = SteamIdBox.Text.Trim();
        if (steam && !steamId.All(char.IsDigit) || steam && steamId.Length == 0)
        {
            ErrorText.Text = "Enter the Steam app id (numbers only), or start the game directly.";
            return;
        }
        var useUri = UriRadio.IsChecked == true;
        var uri = UriBox.Text.Trim().Trim('"');
        if (useUri && !System.Text.RegularExpressions.Regex.IsMatch(uri, @"^[a-z][a-z0-9+.\-]*://", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            ErrorText.Text = "Enter a launcher link like com.epicgames.launcher://…, or start the game directly.";
            return;
        }
        var cover = CoverBox.Text.Trim().Trim('"');
        if (cover.Length > 0 && !File.Exists(cover))
        {
            ErrorText.Text = "The cover image doesn't exist.";
            return;
        }

        var g = _existing ?? new GameEntry();
        g.ExePath = exe;
        g.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? GuessName(exe) : NameBox.Text.Trim();
        g.UevrBuildPath = build.Folder;
        g.ProfileZipPath = zip.Length > 0 ? zip : null;
        g.LaunchMethod = steam ? LaunchMethod.Steam : useUri ? LaunchMethod.Uri : LaunchMethod.Direct;
        g.SteamAppId = steamId.Length > 0 ? steamId : null;
        g.LaunchUri = uri.Length > 0 ? uri : null;
        g.LaunchArgs = ArgsBox.Text.Trim();
        g.InjectDelaySeconds = delay;
        g.UseOfxr = OfxrCheck.IsChecked == true;
        g.CoverImagePath = cover.Length > 0 ? cover : null;
        Result = g;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
