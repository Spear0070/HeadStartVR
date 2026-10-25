using System.IO;
using System.IO.Compression;

namespace HeadStartVR.Tests;

/// <summary>A throwaway folder per test, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeadStartVR-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    /// <summary>Creates a zip with the given entries (name → content).</summary>
    public string Zip(string relative, params (string name, string content)[] entries)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        using var zip = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        return p;
    }

    /// <summary>A minimal folder that looks like a UEVR build.</summary>
    public string Build(params string[] parts)
    {
        var d = Sub(parts);
        System.IO.File.WriteAllText(System.IO.Path.Combine(d, "UEVRInjector.exe"), "x");
        System.IO.File.WriteAllText(System.IO.Path.Combine(d, "UEVRBackend.dll"), "x");
        return d;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
