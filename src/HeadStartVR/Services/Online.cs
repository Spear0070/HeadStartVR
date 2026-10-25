using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HeadStartVR.Models;

namespace HeadStartVR.Services;

/// <summary>
/// The launcher's only internet access, used solely when you open "Get UEVR builds".
/// No accounts, no tracking: plain HTTPS GETs to GitHub.
/// </summary>
public static class Net
{
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("HeadStartVR/1.1");
        return c;
    }

    /// <summary>Download to a file, reporting progress 0..1 when the size is known.</summary>
    public static async Task DownloadAsync(string url, string file, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress?.Report((double)done / total.Value);
        }
    }

    public static string Sha256(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}

// ---- UEVR builds from the authors' own GitHub releases -----------------------------------------------

public sealed record ReleaseAsset(string Name, long Size, string Url);

public sealed record Release(string Tag, string Title, DateTime Published, string PageUrl, List<ReleaseAsset> Assets)
{
    public string Display => $"{Title}  ·  {Published:d MMM yyyy}";
}

/// <summary>One installable file of a release (e.g. the nightly-based or the joeyhodge-based AFW zip).</summary>
public sealed record BuildChoice(BuildSource Source, Release Release, ReleaseAsset Asset, ReleaseAsset? Checksum)
{
    public string FolderName => Source.FolderName(Release, Asset);
    public string Display => Source.VariantLabel(Asset) + $"  ·  {Asset.Size / 1048576.0:N1} MB";
}

public sealed class BuildSource
{
    public required string Title { get; init; }
    public required string Repo { get; init; }
    public required string Description { get; init; }
    public required Func<ReleaseAsset, bool> IsBuildZip { get; init; }
    public required Func<Release, ReleaseAsset, string> FolderName { get; init; }
    public Func<ReleaseAsset, string> VariantLabel { get; init; } = a => a.Name;
    public string PageUrl => $"https://github.com/{Repo}/releases";

    /// <summary>
    /// The only places builds are downloaded from. UEVR is "All rights reserved" (praydog), so the launcher never
    /// bundles or re-hosts it; it fetches the authors' own release files, exactly as a browser download would.
    /// </summary>
    public static readonly IReadOnlyList<BuildSource> All = new[]
    {
        new BuildSource
        {
            Title = "UEVR Nightly (praydog)",
            Repo = "praydog/UEVR-nightly",
            Description = "Official nightly builds. What most current profiles are made for; the usual first choice.",
            IsBuildZip = a => a.Name.Equals("uevr.zip", StringComparison.OrdinalIgnoreCase),
            FolderName = (r, _) => "UEVR Nightly " + (Regex.Match(r.Tag, @"nightly-(\d+)") is { Success: true } m ? m.Groups[1].Value : r.Tag),
            VariantLabel = _ => "Full build",
        },
        new BuildSource
        {
            Title = "UEVR Stable (praydog)",
            Repo = "praydog/UEVR",
            Description = "Official stable release. Older than the nightlies; only if a profile asks for it.",
            IsBuildZip = a => a.Name.Equals("UEVR.zip", StringComparison.OrdinalIgnoreCase),
            FolderName = (r, _) => "UEVR Stable " + r.Tag,
            VariantLabel = _ => "Full build",
        },
        new BuildSource
        {
            Title = "UEVR JoeyHodge fork",
            Repo = "joeyhodge/UEVR",
            Description = "Community fork with frequent fixes and extra features. Some 6DOF profiles need it.",
            IsBuildZip = a => a.Name.Equals("uevr.zip", StringComparison.OrdinalIgnoreCase),
            FolderName = (r, _) => "UEVR JoeyHodge " + r.Tag,
            VariantLabel = _ => "Full build",
        },
        new BuildSource
        {
            Title = "UEVR AFW (PureDark)",
            Repo = "PureDark/UEVR",
            Description = "Adds PureDark's frame-warp (AFW) backend for smoother frame rates. Comes based on the nightly or on JoeyHodge.",
            IsBuildZip = a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("AFW", StringComparison.OrdinalIgnoreCase),
            FolderName = (r, a) => "UEVR AFW " + Regex.Replace(r.Tag, "^UEVR_AFW_", "") + (a.Name.Contains("joeyhodge", StringComparison.OrdinalIgnoreCase) ? " (JoeyHodge)" : " (Nightly)"),
            VariantLabel = a => a.Name.Contains("joeyhodge", StringComparison.OrdinalIgnoreCase) ? "Based on JoeyHodge" : "Based on the nightly",
        },
    };
}

public static class BuildDownloader
{
    private static readonly Dictionary<string, (DateTime at, List<Release> releases)> Cache = new();

    public static async Task<List<Release>> GetReleasesAsync(BuildSource source, CancellationToken ct)
    {
        lock (Cache)
            if (Cache.TryGetValue(source.Repo, out var c) && DateTime.Now - c.at < TimeSpan.FromMinutes(15)) return c.releases;

        var json = await Net.Http.GetStringAsync($"https://api.github.com/repos/{source.Repo}/releases?per_page=12", ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<Release>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            var assets = r.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(
                a.GetProperty("name").GetString()!,
                a.GetProperty("size").GetInt64(),
                a.GetProperty("browser_download_url").GetString()!)).ToList();
            var tag = r.GetProperty("tag_name").GetString()!;
            var title = r.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : tag;
            list.Add(new Release(tag, title, r.GetProperty("published_at").GetDateTime().ToLocalTime(), r.GetProperty("html_url").GetString()!, assets));
        }
        lock (Cache) Cache[source.Repo] = (DateTime.Now, list);
        return list;
    }

    public static List<BuildChoice> Choices(BuildSource source, Release release) =>
        release.Assets.Where(source.IsBuildZip)
            .Select(a => new BuildChoice(source, release, a,
                release.Assets.FirstOrDefault(x => x.Name.Equals(a.Name + ".sha256", StringComparison.OrdinalIgnoreCase))))
            .ToList();

    /// <summary>Download, verify (when a checksum is published), unpack and check it's a complete build.</summary>
    public static async Task<string> InstallAsync(BuildChoice choice, string installFolder, IProgress<(double, string)> progress, CancellationToken ct)
    {
        var dest = Path.Combine(installFolder, choice.FolderName);
        if (Directory.Exists(dest)) throw new IOException($"{choice.FolderName} is already installed in {installFolder}.");
        if (!choice.Asset.Url.StartsWith($"https://github.com/{choice.Source.Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected download address; refusing to download.");

        // Stage inside the install folder so the final move never crosses drives.
        Directory.CreateDirectory(installFolder);
        var work = Path.Combine(installFolder, ".headstartvr-download-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, choice.Asset.Name);
            progress.Report((0, $"Downloading {choice.Asset.Name}…"));
            await Net.DownloadAsync(choice.Asset.Url, zip, new Progress<double>(p => progress.Report((p, $"Downloading {choice.Asset.Name}… {p:P0}"))), ct);

            if (choice.Checksum != null)
            {
                progress.Report((1, "Checking the download…"));
                var expected = Regex.Match(await Net.Http.GetStringAsync(choice.Checksum.Url, ct), "[0-9a-fA-F]{64}").Value;
                if (!string.Equals(expected, Net.Sha256(zip), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download doesn't match its published checksum. Nothing was installed; try again.");
            }

            progress.Report((1, "Unpacking…"));
            var unpacked = Path.Combine(work, "unpacked");
            ZipFile.ExtractToDirectory(zip, unpacked);
            var root = unpacked;
            while (!File.Exists(Path.Combine(root, "UEVRInjector.exe")) &&
                   Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is { Length: 1 } only)
                root = only[0];
            if (!File.Exists(Path.Combine(root, "UEVRInjector.exe")) || !File.Exists(Path.Combine(root, "UEVRBackend.dll")))
                throw new InvalidDataException($"{choice.Asset.Name} isn't a complete UEVR build (no UEVRInjector.exe + UEVRBackend.dll).");

            Directory.Move(root, dest);
            File.WriteAllText(Path.Combine(dest, "headstartvr-source.txt"),
                $"{choice.Source.Title}\n{choice.Release.Tag}\n{choice.Asset.Name}\n{choice.Release.PageUrl}\nInstalled {DateTime.Now:yyyy-MM-dd HH:mm}\n");

            // UEVR with Virtual Desktop must use OpenXR; save the injector that choice right away.
            try { UevrBuilds.SetOpenXR(Path.Combine(dest, "UEVRInjector.exe")); } catch { }

            Log.Write($"Installed UEVR build {choice.FolderName} from {choice.Asset.Url}" + (choice.Checksum != null ? " (checksum OK)" : " (no checksum published)"));
            return dest;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }
}

// ---- Where to look for profiles ----------------------------------------------------------------------

/// <summary>
/// Profiles aren't downloaded by the launcher: these open the places authors post them, and the Downloads
/// watcher picks up whatever you save.
/// </summary>
public static class ProfileLinks
{
    public static string NexusSearch(string gameName) => "https://www.nexusmods.com/search?keyword=" + Uri.EscapeDataString(gameName + " UEVR");
    public static string WebSearch(string gameName) => "https://www.google.com/search?q=" + Uri.EscapeDataString($"\"{gameName}\" UEVR profile");

    /// <summary>flat2vr.com forwards to the Flat2VR Discord invite.</summary>
    public const string Flat2VrDiscord = "https://flat2vr.com/";
}
