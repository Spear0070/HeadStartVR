using System.IO;
using HeadStartVR.Models;
using HeadStartVR.Services;
using Xunit;

namespace HeadStartVR.Tests;

public class DiscoveryTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    // ---- Games ------------------------------------------------------------------------------------

    [Fact]
    public void Finds_the_shipping_exe_not_the_launcher_stub()
    {
        var root = _t.Sub("Games", "Cool Game");
        _t.File(@"Games\Cool Game\CoolGame.exe");                                   // root launcher stub
        var shipping = _t.File(@"Games\Cool Game\CoolGame\Binaries\Win64\CoolGame-Win64-Shipping.exe");
        _t.File(@"Games\Cool Game\Engine\Binaries\Win64\CrashReportClient.exe");

        var found = GameScanner.FindShippingExes(root);

        Assert.Equal(new[] { shipping }, found);
    }

    [Fact]
    public void Finds_a_renamed_game_exe_next_to_an_Engine_folder()
    {
        var root = _t.Sub("Games", "Renamed");
        _t.Sub("Games", "Renamed", "Engine");
        var exe = _t.File(@"Games\Renamed\Proj\Binaries\Win64\Proj.exe");
        Assert.Equal(new[] { exe }, GameScanner.FindShippingExes(root));
    }

    [Fact]
    public void Ignores_non_unreal_games()
    {
        var root = _t.Sub("Games", "NotUnreal");
        _t.File(@"Games\NotUnreal\bin\win64\game.exe"); // no Engine folder, no Binaries\Win64 layout
        _t.File(@"Games\NotUnreal\game.exe");
        Assert.Empty(GameScanner.FindShippingExes(root));
    }

    [Fact]
    public void Steam_app_id_comes_from_the_library_manifest()
    {
        _t.File(@"lib\steamapps\appmanifest_123450.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"123450\"\n\t\"installdir\"\t\t\"Cool Game\"\n}");
        var exe = _t.File(@"lib\steamapps\common\Cool Game\CoolGame\Binaries\Win64\CoolGame-Win64-Shipping.exe");
        Assert.Equal("123450", SteamHelper.DetectAppId(exe));
        Assert.Null(SteamHelper.DetectAppId(_t.File(@"elsewhere\Game\Binaries\Win64\X-Win64-Shipping.exe")));
    }

    // ---- UEVR builds -------------------------------------------------------------------------------

    [Fact]
    public void Picking_one_build_folder_also_finds_its_siblings()
    {
        var a = _t.Build("builds", "Game A UEVR");
        _t.Build("builds", "Game B UEVR");
        var found = UevrBuilds.Discover(new AppSettings { BuildLocations = new() { a } });
        Assert.Contains(found, b => b.Name == "Game B UEVR");
    }

    [Fact]
    public void Picking_the_parent_folder_finds_builds_two_levels_down()
    {
        _t.Build("builds", "group", "Deep UEVR");
        var found = UevrBuilds.Discover(new AppSettings { BuildLocations = new() { _t.Sub("builds") } });
        Assert.Contains(found, b => b.Name == "Deep UEVR");
    }

    [Fact]
    public void Same_named_builds_are_told_apart()
    {
        _t.Build("builds", "one", "uevr");
        _t.Build("builds", "two", "uevr");
        var dupes = UevrBuilds.Discover(new AppSettings { BuildLocations = new() { _t.Sub("builds") } })
            .Where(b => b.Name == "uevr").ToList();
        Assert.Equal(2, dupes.Count);
        Assert.NotEqual(dupes[0].Display, dupes[1].Display);
    }

    [Fact]
    public void Remembered_and_in_use_builds_are_always_offered()
    {
        var known = _t.Build("anywhere", "My UEVR");
        var used = _t.Build("elsewhere", "Used UEVR");
        var found = UevrBuilds.Discover(new AppSettings { KnownBuilds = new() { known } }, new[] { used });
        Assert.Contains(found, b => b.Folder == known);
        Assert.Contains(found, b => b.Folder == used);
    }

    [Fact]
    public void Downloads_install_next_to_existing_builds()
    {
        var a = _t.Build("builds", "A");
        var b = _t.Build("builds", "B");
        var folder = UevrBuilds.InstallFolder(new AppSettings(), new[] { UevrBuilds.FromFolder(a), UevrBuilds.FromFolder(b) });
        Assert.Equal(_t.Sub("builds"), folder);
    }

    [Fact]
    public void Injector_settings_folder_hash_matches_dotnet()
    {
        // Verified against the folder .NET itself created for this injector path.
        Assert.Equal("wuguqid55tg2riyofqrc2earnuhs1cw4", UevrBuilds.PathHash(@"C:\VRTools\UEVR Hellraiser\UEVRInjector.exe"));
        // Case doesn't matter (.NET upper-cases the path first).
        Assert.Equal(UevrBuilds.PathHash(@"c:\vrtools\uevr hellraiser\uevrinjector.exe"),
                     UevrBuilds.PathHash(@"C:\VRTools\UEVR Hellraiser\UEVRInjector.exe"));
    }

    [Fact]
    public void A_never_opened_injector_is_switched_to_OpenXR()
    {
        var injector = Path.Combine(_t.Build("fresh build"), "UEVRInjector.exe");
        try
        {
            Assert.Equal(InjectorRuntime.NotSet, UevrBuilds.GetRuntime(injector));
            UevrBuilds.SetOpenXR(injector);
            Assert.Equal(InjectorRuntime.OpenXR, UevrBuilds.GetRuntime(injector));
        }
        finally
        {
            if (UevrBuilds.FindSettingsFile(injector) is { } cfg)
                Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(cfg))!, true);
        }
    }

    // ---- Build sources (no network) -----------------------------------------------------------------

    [Fact]
    public void Build_sources_name_installs_clearly()
    {
        var nightly = BuildSource.All.Single(s => s.Repo == "praydog/UEVR-nightly");
        var rel = new Release("nightly-01143-4ee5c6b", "x", DateTime.Now, "https://github.com/praydog/UEVR-nightly/releases/tag/x",
            new() { new ReleaseAsset("uevr.zip", 1, "u"), new ReleaseAsset("uevr.zip.sha256", 1, "s") });
        var choice = BuildDownloader.Choices(nightly, rel).Single();
        Assert.Equal("UEVR Nightly 01143", choice.FolderName);
        Assert.NotNull(choice.Checksum);

        var afw = BuildSource.All.Single(s => s.Repo == "PureDark/UEVR");
        var afwRel = new Release("UEVR_AFW_v1.0-beta.6", "x", DateTime.Now, "p", new()
        {
            new ReleaseAsset("UEVR-joeyhodge_AFW_v1.0-beta.6.zip", 1, "a"),
            new ReleaseAsset("UEVR-nightly_AFW_v1.0-beta.6.zip", 1, "b"),
        });
        Assert.Equal(new[] { "UEVR AFW v1.0-beta.6 (JoeyHodge)", "UEVR AFW v1.0-beta.6 (Nightly)" },
            BuildDownloader.Choices(afw, afwRel).Select(c => c.FolderName));
    }
}
