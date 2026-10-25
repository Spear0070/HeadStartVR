using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HeadStartVR.Models;
using HeadStartVR.Services;

namespace HeadStartVR.Views;

public partial class BackupsWindow : Window
{
    private readonly GameEntry _game;
    private readonly ProfileManager _profiles;

    /// <summary>True when a backup was restored (the caller saves games.json and refreshes).</summary>
    public bool Restored { get; private set; }

    public sealed record Row(string Path, string When, string Detail);

    public BackupsWindow(GameEntry game, ProfileManager profiles)
    {
        InitializeComponent();
        _game = game;
        _profiles = profiles;
        HeaderText.Text = $"{game.Name} · profile backups";
        Load();
    }

    private void Load()
    {
        var rows = _profiles.ListBackups(_game).Select(f => new Row(
            f.FullName,
            Describe(f.Name) + " · " + f.LastWriteTime.ToString("d MMM yyyy, HH:mm"),
            $"{f.Name} · {(f.Length < 1024 ? "under 1 KB" : $"{f.Length / 1024.0:N0} KB")}")).ToList();
        List.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Describe(string fileName) =>
        fileName.StartsWith("profile-before-update", StringComparison.OrdinalIgnoreCase) ? "Before a profile update"
        : fileName.StartsWith("profile-before-restore", StringComparison.OrdinalIgnoreCase) ? "Before a restore"
        : "Saved backup";

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var path = (string)((Button)sender).Tag;
        var a = Dialog.Show(this, "Restore backup",
            $"Replace the current {_game.ExeStem} profile with\n{Path.GetFileName(path)}?\n\nThe current profile is backed up first.",
            ("Restore", "restore", true), ("Cancel", "cancel", false));
        if (a != "restore") return;
        try
        {
            var r = _profiles.Restore(_game, path);
            Restored = true;
            Dialog.Show(this, "Backup restored",
                $"Restored {r.FilesWritten} files." + (r.BackupZip != null ? $"\n\nThe profile you had before was saved as\n{Path.GetFileName(r.BackupZip)}" : ""),
                ("OK", "ok", true));
            Load();
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "Couldn't restore", ex.Message, ("OK", "ok", true));
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _profiles.BackupFolder(_game);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
