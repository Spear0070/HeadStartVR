using System.IO;
using HeadStartVR.Models;
using HeadStartVR.Services;
using Xunit;

namespace HeadStartVR.Tests;

public class MiscTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void Shortcut_requests_reach_the_running_launcher()
    {
        var data = _t.Sub("data");
        string? received = null;
        using (InstanceBridge.Listen(data, c => received = c))
        {
            Assert.True(InstanceBridge.TrySend(data, "launch abc123"));
            for (var i = 0; i < 60 && received == null; i++) Thread.Sleep(50);
        }
        Assert.Equal("launch abc123", received);
    }

    [Fact]
    public void Empty_settings_get_sensible_defaults()
    {
        var s = new AppSettings();
        Store.FillDefaults(s, _t.Path);
        Assert.Equal(Path.Combine(_t.Path, "profile-backups"), s.BackupsRoot);
        Assert.NotNull(s.OfxrTrayPath);   // found or empty: OFXR is optional
        Assert.NotNull(s.VdStreamerPath); // found or empty: Virtual Desktop is optional
    }

    [Fact]
    public void Settings_and_library_round_trip()
    {
        var store = new Store(_t.Path);
        store.Games.Add(new GameEntry { Name = "A", ExePath = @"X:\a\A-Win64-Shipping.exe", LaunchMethod = LaunchMethod.Uri, LaunchUri = "com.epicgames.launcher://apps/x" });
        store.Settings.KnownBuilds.Add(@"X:\builds\one");
        store.SaveGames();
        store.SaveSettings();

        var again = new Store(_t.Path);
        Assert.Equal("A", again.Games.Single().Name);
        Assert.Equal(LaunchMethod.Uri, again.Games.Single().LaunchMethod);
        Assert.Contains(@"X:\builds\one", again.Settings.KnownBuilds);
    }

    [Fact]
    public void Profile_search_links_are_well_formed()
    {
        Assert.Equal("https://www.nexusmods.com/search?keyword=Cool%20Game%20UEVR", ProfileLinks.NexusSearch("Cool Game"));
        Assert.StartsWith("https://www.google.com/search?q=", ProfileLinks.WebSearch("Cool Game"));
    }
}
