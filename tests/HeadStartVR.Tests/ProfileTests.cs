using System.IO;
using HeadStartVR.Models;
using HeadStartVR.Services;
using Xunit;

namespace HeadStartVR.Tests;

public class ProfileTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly AppSettings _settings;
    private readonly ProfileManager _pm;
    private readonly GameEntry _game = new() { Name = "Test Game", ExePath = @"X:\nowhere\TestGame-Win64-Shipping.exe" };

    public ProfileTests()
    {
        _settings = new AppSettings
        {
            ProfilesRoot = _t.Sub("UnrealVRMod"),
            BackupsRoot = _t.Sub("backups"),
            ProfileWatchFolders = new() { _t.Sub("Downloads") },
        };
        _pm = new ProfileManager(_settings);
    }

    public void Dispose() => _t.Dispose();

    private string ProfileFile(string name) => Path.Combine(_pm.ProfileFolder(_game), name);

    [Fact]
    public void Apply_replaces_the_profile_and_backs_up_the_old_one()
    {
        _t.File(@"UnrealVRMod\TestGame-Win64-Shipping\old.txt");
        var zip = _t.Zip("new.zip", ("config.txt", "a=1"), (@"scripts/x.lua", ""));

        var r = _pm.Apply(_game, zip);

        Assert.True(File.Exists(ProfileFile("config.txt")));
        Assert.True(File.Exists(ProfileFile(@"scripts\x.lua")));
        Assert.False(File.Exists(ProfileFile("old.txt")));
        Assert.NotNull(r.BackupZip);
        Assert.True(File.Exists(r.BackupZip));
        Assert.Equal(zip, _game.ProfileZipPath);
        Assert.Null(_pm.FindPendingUpdate(_game));
    }

    [Theory]
    [InlineData("TestGame-Win64-Shipping/config.txt")]   // wrapped in a folder named after the exe
    [InlineData("Some Folder/Inner/config.txt")]           // nested deeper
    public void Apply_unwraps_folders(string entry)
    {
        var zip = _t.Zip("wrapped.zip", (entry, "a=1"));
        _pm.Apply(_game, zip);
        Assert.True(File.Exists(ProfileFile("config.txt")));
    }

    [Fact]
    public void Apply_unwraps_a_zip_inside_a_zip()
    {
        var inner = _t.Zip(@"tmp\inner.zip", ("config.txt", "a=1"));
        var outer = Path.Combine(_t.Path, "outer.zip");
        using (var z = System.IO.Compression.ZipFile.Open(outer, System.IO.Compression.ZipArchiveMode.Create))
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, inner, "inner.zip");
        _pm.Apply(_game, outer);
        Assert.True(File.Exists(ProfileFile("config.txt")));
    }

    [Fact]
    public void Restore_puts_a_backup_back_and_backs_up_first()
    {
        _t.File(@"UnrealVRMod\TestGame-Win64-Shipping\original.txt");
        _pm.Apply(_game, _t.Zip("v2.zip", ("config.txt", "v2")));
        var backup = _pm.ListBackups(_game).Single();

        var r = _pm.Restore(_game, backup.FullName);

        Assert.True(File.Exists(ProfileFile("original.txt")));
        Assert.False(File.Exists(ProfileFile("config.txt")));
        Assert.NotNull(r.BackupZip);
        Assert.Equal(2, _pm.ListBackups(_game).Count);
        Assert.Null(_pm.FindPendingUpdate(_game)); // the zip just rolled back from isn't offered again
    }

    [Fact]
    public void Downloads_named_after_the_exe_are_offered_but_older_ones_are_not()
    {
        _pm.Apply(_game, _t.Zip("v1.zip", ("config.txt", "v1")));

        var old = _t.Zip(@"Downloads\TestGame-Win64-Shipping (1).zip", ("config.txt", "old"));
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-3));
        Assert.Null(_pm.FindPendingUpdate(_game));

        var fresh = _t.Zip(@"Downloads\TestGame-Win64-Shipping (2).zip", ("config.txt", "new"));
        File.SetLastWriteTime(fresh, DateTime.Now.AddMinutes(1));
        var pending = _pm.FindPendingUpdate(_game);
        Assert.NotNull(pending);
        Assert.True(pending!.FromWatchedFolder);
        Assert.Equal(fresh, pending.ZipPath);
    }

    [Fact]
    public void Downloads_with_the_exe_folder_inside_are_offered_whatever_their_name()
    {
        var zip = _t.Zip(@"Downloads\some-vr-mod-v3.zip", ("TestGame-Win64-Shipping/config.txt", "x"));
        Assert.Equal(zip, _pm.FindPendingUpdate(_game)?.ZipPath);
    }

    [Theory]
    [InlineData("SHProto-Win64-Shipping.zip", true)]
    [InlineData("SHProto-Win64-Shipping (1).zip", true)]
    [InlineData("SHProto Win64 Shipping 421 3.3 2026-09-23T15-50Z alf5wbRh4.zip", true)] // real Flat2VR file name
    [InlineData("shproto_win64_shipping_v2.zip", true)]
    [InlineData("SHProto.zip", false)]
    [InlineData("Other-Win64-Shipping.zip", false)]
    public void Download_names_with_spaces_or_underscores_still_match(string fileName, bool expected)
    {
        Assert.Equal(expected, ProfileManager.NameMatches(fileName, "SHProto-Win64-Shipping"));
    }

    [Fact]
    public void Unrelated_downloads_are_ignored()
    {
        _t.Zip(@"Downloads\OtherGame-Win64-Shipping.zip", ("config.txt", "x"));
        Assert.Null(_pm.FindPendingUpdate(_game));
    }

    [Fact]
    public void Ignored_zips_stay_ignored()
    {
        _t.Zip(@"Downloads\TestGame-Win64-Shipping.zip", ("config.txt", "x"));
        var pending = _pm.FindPendingUpdate(_game)!;
        _game.IgnoredProfileHash = pending.Hash;
        Assert.Null(_pm.FindPendingUpdate(_game));
    }

    [Fact]
    public void Plugin_dlls_are_detected()
    {
        Assert.True(ProfileManager.ZipContainsDll(_t.Zip("p.zip", ("config.txt", ""), ("plugins/x.dll", ""))));
        Assert.False(ProfileManager.ZipContainsDll(_t.Zip("q.zip", ("config.txt", ""))));
    }
}
