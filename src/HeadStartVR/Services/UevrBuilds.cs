using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HeadStartVR.Models;

namespace HeadStartVR.Services;

public sealed record UevrBuild(string Folder, string Name, string? Revision)
{
    public string InjectorPath => Path.Combine(Folder, "UEVRInjector.exe");
    public string LoaderPath => Path.Combine(Folder, "openxr_loader.dll");

    /// <summary>Parent folder shown when two builds share a name (e.g. two folders called "uevr").</summary>
    public string? Hint { get; init; }

    public string Display => Name + (Revision == null ? "" : $"  ·  {Revision}") + (Hint == null ? "" : $"  ·  in {Hint}");
}

public enum InjectorRuntime
{
    OpenXR,
    OpenVR,
    NotSet,
}

public static class UevrBuilds
{
    /// <summary>
    /// Every UEVR build the launcher can offer: the user's build locations (a picked build folder also brings in its
    /// siblings), remembered "Other folder…" builds, builds already used by games, and common places
    /// (Downloads, Desktop, Documents, C:\VRTools, &lt;drive&gt;:\UEVR, …) found without any setup.
    /// </summary>
    public static List<UevrBuild> Discover(AppSettings settings, IEnumerable<string>? gameBuildFolders = null)
    {
        var found = new Dictionary<string, UevrBuild>(StringComparer.OrdinalIgnoreCase);
        void Add(UevrBuild b) => found.TryAdd(Path.GetFullPath(b.Folder).TrimEnd('\\'), b);
        void AddRoot(string root, int depth = 2)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var b in Scan(root, depth)) Add(b);
            }
            catch { }
        }

        foreach (var loc in settings.BuildLocations)
        {
            AddRoot(loc);
            // Someone picked one game's build instead of the folder holding all of them: look next to it too.
            if (IsValidBuild(loc) && Path.GetDirectoryName(loc.TrimEnd('\\')) is { } parent && !IsDriveRoot(parent))
                AddRoot(parent, depth: 1);
        }
        foreach (var folder in settings.KnownBuilds.Concat(gameBuildFolders ?? Array.Empty<string>()))
            if (IsValidBuild(folder)) Add(FromFolder(folder));
        foreach (var root in AutoDetectRoots(settings)) AddRoot(root);

        var list = found.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // Same folder name in different places: show where each one lives.
        var dupes = list.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        return list.Select(b => dupes.Contains(b) ? b with { Hint = Path.GetFileName(Path.GetDirectoryName(b.Folder.TrimEnd('\\'))) } : b).ToList();
    }

    /// <summary>Places people commonly unzip UEVR builds to.</summary>
    public static List<string> AutoDetectRoots(AppSettings settings)
    {
        var roots = new List<string>
        {
            KnownFolders.Downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            DefaultInstallFolder,
        };
        if (!string.IsNullOrEmpty(settings.BuildInstallFolder)) roots.Add(settings.BuildInstallFolder);
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                foreach (var name in new[] { "UEVR", "VRTools", "VR", "VR Tools", "Tools" })
                    roots.Add(Path.Combine(d.RootDirectory.FullName, name));
        }
        catch { }
        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Fallback install folder for downloaded builds: "UEVR builds" next to the launcher.</summary>
    public static string DefaultInstallFolder => Path.Combine(AppContext.BaseDirectory, "UEVR builds");

    /// <summary>Where downloads go: the user's choice, else the folder already holding most builds, else the default.</summary>
    public static string InstallFolder(AppSettings settings, IEnumerable<UevrBuild> builds)
    {
        if (!string.IsNullOrEmpty(settings.BuildInstallFolder)) return settings.BuildInstallFolder;
        var downloads = KnownFolders.Downloads;
        var common = builds
            .Select(b => Path.GetDirectoryName(b.Folder.TrimEnd('\\')))
            .Where(p => p != null && !IsDriveRoot(p) && !p.Equals(downloads, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;
        return common ?? DefaultInstallFolder;
    }

    private static bool IsDriveRoot(string path) => Path.GetPathRoot(path)?.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ?? false;

    public static List<UevrBuild> Scan(string root, int depth = 2)
    {
        var builds = new List<UevrBuild>();
        if (!Directory.Exists(root)) return builds;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true };
        foreach (var injector in Directory.EnumerateFiles(root, "UEVRInjector.exe", options))
        {
            if (injector.Contains(@"\.headstartvr-download-", StringComparison.OrdinalIgnoreCase)) continue; // half-installed download
            builds.Add(FromFolder(Path.GetDirectoryName(injector)!));
        }
        return builds.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static UevrBuild FromFolder(string folder)
    {
        string? revision = null;
        var revFile = Path.Combine(folder, "revision.txt");
        try
        {
            if (File.Exists(revFile))
            {
                var rev = File.ReadAllText(revFile).Trim();
                if (rev.Length > 0) revision = rev.Length > 7 ? rev[..7] : rev;
            }
        }
        catch { }
        return new UevrBuild(folder, Path.GetFileName(folder.TrimEnd('\\')), revision);
    }

    public static bool IsValidBuild(string folder) => File.Exists(Path.Combine(folder, "UEVRInjector.exe"));

    // ---- The injector's saved runtime choice ------------------------------------------------------------
    // UEVRInjector stores its UI choices (OpenXR/OpenVR radio, etc.) as .NET user settings in
    // %LOCALAPPDATA%\<Company>\UEVRInjector_Path_<hash>\<version>\user.config, where <hash> is derived from the
    // injector's full path. A build that was never opened at its current path therefore has no saved choice and
    // falls back to OpenVR, which then gets used by --attach.

    public static string PathHash(string injectorPath)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            bw.Write(Path.GetFullPath(injectorPath).ToUpperInvariant());
        return ToBase32StringSuitableForDirName(SHA1.HashData(ms.ToArray()));
    }

    public static string? FindSettingsFile(string injectorPath)
    {
        var hash = PathHash(injectorPath);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        IEnumerable<string> companies;
        try
        {
            // praydog's builds first; forks may use another company name, so fall back to every folder.
            companies = new[] { Path.Combine(local, "praydog") }.Concat(Directory.EnumerateDirectories(local)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return null;
        }
        foreach (var company in companies)
        {
            try
            {
                if (!Directory.Exists(company)) continue;
                foreach (var dir in Directory.EnumerateDirectories(company, "*_Path_" + hash))
                {
                    var newest = Directory.EnumerateFiles(dir, "user.config", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (newest != null) return newest;
                }
            }
            catch
            {
                // Some folders in LocalAppData deny listing; skip them.
            }
        }
        return null;
    }

    public static InjectorRuntime GetRuntime(string injectorPath)
    {
        var file = FindSettingsFile(injectorPath);
        if (file == null) return InjectorRuntime.NotSet;
        try
        {
            var doc = XDocument.Load(file);
            bool? Get(string name) => doc.Descendants("setting").FirstOrDefault(s => (string?)s.Attribute("name") == name)
                ?.Element("value")?.Value is { } v ? string.Equals(v, "True", StringComparison.OrdinalIgnoreCase) : null;
            if (Get("OpenXRRadio") == true) return InjectorRuntime.OpenXR;
            return InjectorRuntime.OpenVR;
        }
        catch
        {
            return InjectorRuntime.NotSet;
        }
    }

    /// <summary>Select OpenXR in the injector's saved settings (what clicking the OpenXR radio button and closing it does).</summary>
    public static void SetOpenXR(string injectorPath)
    {
        var file = FindSettingsFile(injectorPath);
        if (file == null)
        {
            var version = FileVersionInfo.GetVersionInfo(injectorPath).FileVersion;
            if (string.IsNullOrWhiteSpace(version)) version = "1.0.0.0";
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "praydog", "UEVRInjector_Path_" + PathHash(injectorPath), version);
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, "user.config");
            File.WriteAllText(file, DefaultUserConfig);
        }

        var doc = XDocument.Load(file);
        var section = doc.Descendants("UEVR.MainWindowSettings").FirstOrDefault()
                      ?? throw new InvalidDataException("Unexpected injector settings format: " + file);
        void Set(string name, string value)
        {
            var setting = section.Elements("setting").FirstOrDefault(s => (string?)s.Attribute("name") == name);
            if (setting == null)
            {
                setting = new XElement("setting", new XAttribute("name", name), new XAttribute("serializeAs", "String"), new XElement("value"));
                section.Add(setting);
            }
            setting.SetElementValue("value", value);
        }
        Set("OpenXRRadio", "True");
        Set("OpenVRRadio", "False");
        doc.Save(file);
    }

    private const string DefaultUserConfig = """
<?xml version="1.0" encoding="utf-8"?>
<configuration>
    <configSections>
        <sectionGroup name="userSettings" type="System.Configuration.UserSettingsGroup, System.Configuration.ConfigurationManager, Version=6.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51" >
            <section name="UEVR.MainWindowSettings" type="System.Configuration.ClientSettingsSection, System.Configuration.ConfigurationManager, Version=6.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51" allowExeDefinition="MachineToLocalUser" requirePermission="false" />
        </sectionGroup>
    </configSections>
    <userSettings>
        <UEVR.MainWindowSettings>
            <setting name="OpenXRRadio" serializeAs="String">
                <value>True</value>
            </setting>
            <setting name="OpenVRRadio" serializeAs="String">
                <value>False</value>
            </setting>
            <setting name="NullifyVRPluginsCheckbox" serializeAs="String">
                <value>True</value>
            </setting>
            <setting name="FocusGameOnInjection" serializeAs="String">
                <value>True</value>
            </setting>
            <setting name="IgnoreFutureVDWarnings" serializeAs="String">
                <value>False</value>
            </setting>
        </UEVR.MainWindowSettings>
    </userSettings>
</configuration>
""";

    private static readonly char[] Base32Chars = "abcdefghijklmnopqrstuvwxyz012345".ToCharArray();

    // Same encoding .NET uses for isolated-storage / user-settings folder names.
    private static string ToBase32StringSuitableForDirName(byte[] buff)
    {
        var sb = new StringBuilder();
        int i = 0, l = buff.Length;
        do
        {
            byte b0 = i < l ? buff[i++] : (byte)0;
            byte b1 = i < l ? buff[i++] : (byte)0;
            byte b2 = i < l ? buff[i++] : (byte)0;
            byte b3 = i < l ? buff[i++] : (byte)0;
            byte b4 = i < l ? buff[i++] : (byte)0;
            sb.Append(Base32Chars[b0 & 0x1F]);
            sb.Append(Base32Chars[b1 & 0x1F]);
            sb.Append(Base32Chars[b2 & 0x1F]);
            sb.Append(Base32Chars[b3 & 0x1F]);
            sb.Append(Base32Chars[b4 & 0x1F]);
            sb.Append(Base32Chars[((b0 & 0xE0) >> 5) | ((b3 & 0x60) >> 2)]);
            sb.Append(Base32Chars[((b1 & 0xE0) >> 5) | ((b4 & 0x60) >> 2)]);
            b2 >>= 5;
            if ((b3 & 0x80) != 0) b2 |= 0x08;
            if ((b4 & 0x80) != 0) b2 |= 0x10;
            sb.Append(Base32Chars[b2]);
        } while (i < l);
        return sb.ToString();
    }
}
