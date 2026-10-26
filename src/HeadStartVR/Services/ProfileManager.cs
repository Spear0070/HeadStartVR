using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using HeadStartVR.Models;

namespace HeadStartVR.Services;

public sealed record ProfileApplyResult(string ProfileFolder, string? BackupZip, int FilesWritten);

public sealed record PendingProfile(string ZipPath, string Hash, bool FromWatchedFolder);

/// <summary>Replaces a game's UEVR profile (%APPDATA%\UnrealVRMod\&lt;GameExe&gt;) with a downloaded zip, backing up the old one first.</summary>
public sealed class ProfileManager
{
    private readonly AppSettings _settings;

    public ProfileManager(AppSettings settings) => _settings = settings;

    public string ProfileFolder(GameEntry game) => Path.Combine(_settings.ProfilesRoot, game.ExeStem);

    public bool ProfileExists(GameEntry game)
    {
        var dir = ProfileFolder(game);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static bool ZipContainsDll(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Any(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A profile zip that differs from what was last applied (and wasn't dismissed): either the game's linked
    /// zip, or a newer zip named after the game's exe in one of the watched folders (e.g. Downloads).
    /// </summary>
    public PendingProfile? FindPendingUpdate(GameEntry game)
    {
        if (!string.IsNullOrEmpty(game.ProfileZipPath) && File.Exists(game.ProfileZipPath))
        {
            var hash = CachedHash(game.ProfileZipPath);
            if (IsNew(game, hash)) return new PendingProfile(game.ProfileZipPath, hash, FromWatchedFolder: false);
        }

        foreach (var zip in FindDownloadedZips(game))
        {
            var hash = CachedHash(zip.FullName);
            if (IsNew(game, hash) && hash != CachedHashOrNull(game.ProfileZipPath))
                return new PendingProfile(zip.FullName, hash, FromWatchedFolder: true);
        }
        return null;
    }

    private static bool IsNew(GameEntry game, string hash) => hash != game.AppliedProfileHash && hash != game.IgnoredProfileHash;

    /// <summary>Zips in the watched folders that look like this game's profile, newest first.</summary>
    public IEnumerable<FileInfo> FindDownloadedZips(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.ExePath)) yield break;
        var stem = game.ExeStem;
        // Only zips saved after the last applied profile count as new; older downloads are history.
        var cutoff = DateTime.Now.AddDays(-180);
        if (game.AppliedProfileAt is { } applied && applied > cutoff) cutoff = applied;
        var candidates = new List<FileInfo>();
        foreach (var folder in _settings.ProfileWatchFolders.Where(Directory.Exists))
        {
            try
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*.zip"))
                {
                    if (f.LastWriteTime < cutoff || f.Length > 300_000_000) continue;
                    if (NameMatches(f.Name, stem) || ZipHasTopFolder(f.FullName, stem))
                        candidates.Add(f);
                }
            }
            catch
            {
                // Folder unreadable right now; try again on the next refresh.
            }
        }
        foreach (var f in candidates.OrderByDescending(f => f.LastWriteTime)) yield return f;
    }

    /// <summary>
    /// "SHProto-Win64-Shipping (1).zip" and "SHProto Win64 Shipping 421 3.3.zip" both belong to SHProto-Win64-Shipping:
    /// shared profiles often swap dashes for spaces or underscores, so compare letters and digits only.
    /// </summary>
    public static bool NameMatches(string fileName, string exeStem)
    {
        static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var stem = Squash(exeStem);
        return stem.Length > 0 && Squash(Path.GetFileNameWithoutExtension(fileName)).StartsWith(stem, StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, (long len, DateTime mtime, string hash)> HashCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long len, DateTime mtime, bool hit)> FolderPeekCache = new(StringComparer.OrdinalIgnoreCase);

    private static string? CachedHashOrNull(string? path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) ? CachedHash(path) : null;

    private static string CachedHash(string path)
    {
        var fi = new FileInfo(path);
        lock (HashCache)
        {
            if (HashCache.TryGetValue(path, out var c) && c.len == fi.Length && c.mtime == fi.LastWriteTimeUtc) return c.hash;
        }
        var hash = HashFile(path);
        lock (HashCache) HashCache[path] = (fi.Length, fi.LastWriteTimeUtc, hash);
        return hash;
    }

    /// <summary>Profile zips are often wrapped in a folder named after the exe (e.g. "SHProto-Win64-Shipping/config.txt").</summary>
    private static bool ZipHasTopFolder(string zipPath, string stem)
    {
        var fi = new FileInfo(zipPath);
        lock (FolderPeekCache)
        {
            if (FolderPeekCache.TryGetValue(zipPath, out var c) && c.len == fi.Length && c.mtime == fi.LastWriteTimeUtc) return c.hit;
        }
        var hit = false;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            hit = zip.Entries.Any(e => e.FullName.StartsWith(stem + "/", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        lock (FolderPeekCache) FolderPeekCache[zipPath] = (fi.Length, fi.LastWriteTimeUtc, hit);
        return hit;
    }

    // ---- Backups ----------------------------------------------------------------------------------

    public string BackupFolder(GameEntry game) => Path.Combine(_settings.BackupsRoot, SafeName(game.Name));

    public List<FileInfo> ListBackups(GameEntry game)
    {
        var dir = BackupFolder(game);
        if (!Directory.Exists(dir)) return new();
        return new DirectoryInfo(dir).EnumerateFiles("*.zip").OrderByDescending(f => f.LastWriteTime).ToList();
    }

    /// <summary>Put a backup back in place. The current profile is backed up first, so a restore can be undone too.</summary>
    public ProfileApplyResult Restore(GameEntry game, string backupZip)
    {
        var r = Replace(game, backupZip, "before-restore");
        game.AppliedProfileHash = HashFile(backupZip);
        game.AppliedProfileName = "Backup " + Path.GetFileName(backupZip);
        game.AppliedProfileAt = DateTime.Now;
        // Don't nag about the zip the user just rolled back from.
        game.IgnoredProfileHash = CachedHashOrNull(game.ProfileZipPath);
        Log.Write($"Restored backup {backupZip} for {game.Name}");
        return r;
    }

    public static bool IsGameRunning(GameEntry game) => Process.GetProcessesByName(game.ExeStem).Length > 0;

    public string? Backup(GameEntry game, string reason)
    {
        var dir = ProfileFolder(game);
        if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any()) return null;

        var backupDir = Path.Combine(_settings.BackupsRoot, SafeName(game.Name));
        Directory.CreateDirectory(backupDir);
        var stamp = $"profile-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var zipPath = Path.Combine(backupDir, stamp + ".zip");
        for (var n = 2; File.Exists(zipPath); n++) zipPath = Path.Combine(backupDir, $"{stamp}-{n}.zip");
        ZipFile.CreateFromDirectory(dir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }

    public ProfileApplyResult Apply(GameEntry game, string zipPath)
    {
        var r = Replace(game, zipPath, "before-update");
        // The zip just applied becomes the game's linked zip (a download replaces the old link).
        game.ProfileZipPath = zipPath;
        game.AppliedProfileHash = HashFile(zipPath);
        game.AppliedProfileName = Path.GetFileName(zipPath);
        game.AppliedProfileAt = DateTime.Now;
        game.IgnoredProfileHash = null;
        Log.Write($"Applied profile {zipPath} to {r.ProfileFolder} ({r.FilesWritten} files); backup: {r.BackupZip ?? "none"}");
        return r;
    }

    private ProfileApplyResult Replace(GameEntry game, string zipPath, string backupReason)
    {
        if (IsGameRunning(game))
            throw new InvalidOperationException($"{game.Name} is running. Close it first: UEVR rewrites its profile when the game exits.");

        var dest = ProfileFolder(game);
        var temp = Path.Combine(Path.GetTempPath(), "HeadStartVR-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Extract and validate before touching the current profile.
            ZipFile.ExtractToDirectory(zipPath, temp, overwriteFiles: true);
            var root = FindContentRoot(temp);

            var backup = Backup(game, backupReason);

            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(dest);
            var count = CopyTree(root, dest);
            return new ProfileApplyResult(dest, backup, count);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>Mark the current zip as already applied without touching the profile folder.</summary>
    public static void MarkApplied(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.ProfileZipPath) || !File.Exists(game.ProfileZipPath)) return;
        game.AppliedProfileHash = HashFile(game.ProfileZipPath);
        game.AppliedProfileName = Path.GetFileName(game.ProfileZipPath);
        game.AppliedProfileAt = DateTime.Now;
        game.IgnoredProfileHash = null;
    }

    /// <summary>
    /// Profile zips come in several shapes: files at the root, wrapped in a "&lt;GameExe&gt;" folder, or wrapped in
    /// another zip. Find the folder that holds config.txt (or the single wrapped folder).
    /// </summary>
    public static string FindContentRoot(string dir)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            if (File.Exists(Path.Combine(dir, "config.txt"))) return dir;

            var entries = Directory.GetFileSystemEntries(dir)
                .Where(e => !Path.GetFileName(e).Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length != 1) break;

            var single = entries[0];
            if (Directory.Exists(single))
            {
                dir = single;
                continue;
            }
            if (single.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var nested = Path.Combine(dir, "_nested");
                ZipFile.ExtractToDirectory(single, nested, overwriteFiles: true);
                File.Delete(single);
                dir = nested;
                continue;
            }
            break;
        }

        var configs = Directory.GetFiles(dir, "config.txt", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 });
        return configs.Length == 1 ? Path.GetDirectoryName(configs[0])! : dir;
    }

    private static int CopyTree(string source, string dest)
    {
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(dir).Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (rel.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "game" : clean;
    }
}
