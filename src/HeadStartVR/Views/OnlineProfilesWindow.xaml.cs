using System.Windows;
using HeadStartVR.Models;
using HeadStartVR.Services;

namespace HeadStartVR.Views;

/// <summary>Points to where UEVR profiles are posted; the Downloads watcher takes it from there.</summary>
public partial class OnlineProfilesWindow : Window
{
    private readonly GameEntry _game;

    public OnlineProfilesWindow(GameEntry game, AppSettings settings)
    {
        InitializeComponent();
        _game = game;
        HeaderText.Text = $"{game.Name} · find a profile online";
        ExeText.Text = $"UEVR profiles are matched by the game's exe name: {game.ExeStem}.";
        var watched = settings.ProfileWatchFolders.Count > 0 ? string.Join(", ", settings.ProfileWatchFolders) : "your Downloads folder";
        WatchText.Text = $"Just download the profile zip into {watched}. A zip named {game.ExeStem}…zip (or containing a {game.ExeStem} folder) " +
                         "is picked up automatically and offered for this game, with your current profile backed up first. " +
                         "Saved it somewhere else? Use From file… on the game's page.";
    }

    private void Flat2Vr_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_game.Name + " UEVR"); } catch { }
        ProcessLauncher.OpenUriAsUser(ProfileLinks.Flat2VrDiscord);
        Flat2VrText.Text = $"Copied “{_game.Name} UEVR”. Paste it into Discord's search (Ctrl+V) once the Flat2VR server is open.";
    }

    private void Nexus_Click(object sender, RoutedEventArgs e) => ProcessLauncher.OpenUriAsUser(ProfileLinks.NexusSearch(_game.Name));

    private void Web_Click(object sender, RoutedEventArgs e) => ProcessLauncher.OpenUriAsUser(ProfileLinks.WebSearch(_game.Name));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
