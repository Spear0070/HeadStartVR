using System.IO;
using System.Windows.Media;
using HeadStartVR.Models;
using HeadStartVR.Services;

namespace HeadStartVR.ViewModels;

public sealed class GameItem : ObservableObject
{
    private readonly ProfileManager _profiles;

    public GameEntry Game { get; }

    public GameItem(GameEntry game, ProfileManager profiles)
    {
        Game = game;
        _profiles = profiles;
        Refresh();
    }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; private set => Set(ref _icon, value); }

    private ImageSource? _cover;
    public ImageSource? Cover { get => _cover; private set => Set(ref _cover, value); }

    public string Name => Game.Name;
    public string Initials => string.Concat(Game.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string BuildName => string.IsNullOrEmpty(Game.UevrBuildPath) ? "no UEVR build" : Path.GetFileName(Game.UevrBuildPath.TrimEnd('\\'));
    public string LaunchLabel => Game.LaunchMethod switch
    {
        LaunchMethod.Steam => $"Steam · {Game.SteamAppId}",
        LaunchMethod.Uri => Game.Store != null ? $"{Game.Store} launcher" : "Launcher link",
        _ => "Direct exe",
    };
    public string OfxrLabel => Game.UseOfxr ? "OFXR on" : "OFXR off";
    public string DelayLabel => $"Inject after {Game.InjectDelaySeconds}s";
    public string LastPlayedLabel => Game.LastPlayed is { } d ? "Last played " + d.ToString("d MMM yyyy, HH:mm") : "Never launched from here";

    public string ProfileLabel
    {
        get
        {
            if (Game.AppliedProfileName != null) return $"{Game.AppliedProfileName} · applied {Game.AppliedProfileAt:d MMM yyyy}";
            if (string.IsNullOrEmpty(Game.ProfileZipPath)) return "No profile zip linked";
            if (!File.Exists(Game.ProfileZipPath)) return "Profile zip missing";
            return "Profile not applied yet";
        }
    }

    private PendingProfile? _pending;
    /// <summary>A new profile zip waiting to be applied (linked zip changed, or a new download).</summary>
    public PendingProfile? Pending { get => _pending; private set { Set(ref _pending, value); Raise(nameof(HasProfileUpdate)); Raise(nameof(PendingLabel)); } }

    public bool HasProfileUpdate => Pending != null;

    public string PendingLabel => Pending == null ? "" :
        Pending.FromWatchedFolder ? $"New download: {Path.GetFileName(Pending.ZipPath)}" : "New zip available";

    private string? _problem;
    /// <summary>Something that will stop a launch (missing exe/build), shown in red.</summary>
    public string? Problem { get => _problem; private set => Set(ref _problem, value); }

    public void Refresh()
    {
        Icon = IconHelper.ExtractIcon(Game.ExePath);
        Cover = IconHelper.LoadImage(Game.CoverImagePath)
                ?? (Game.SteamAppId != null ? IconHelper.LoadImage(SteamHelper.FindCover(Game.SteamAppId)) : null);

        Problem = !File.Exists(Game.ExePath) ? "Game exe not found"
            : !UevrBuilds.IsValidBuild(Game.UevrBuildPath) ? "UEVR build folder not found"
            : null;

        RefreshProfileStatus();

        foreach (var p in new[] { nameof(Name), nameof(Initials), nameof(BuildName), nameof(LaunchLabel), nameof(OfxrLabel),
                     nameof(DelayLabel), nameof(LastPlayedLabel), nameof(ProfileLabel) })
            Raise(p);
    }

    /// <summary>Cheap re-check of linked and downloaded profile zips (hashes are cached).</summary>
    public void RefreshProfileStatus()
    {
        try
        {
            Pending = _profiles.FindPendingUpdate(Game);
        }
        catch
        {
            Pending = null;
        }
        Raise(nameof(ProfileLabel));
    }
}
