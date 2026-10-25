using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HeadStartVR.Services;

public sealed record ProbeResult(bool Ok, string Result, string? Runtime, string? System, string? Error);

/// <summary>
/// Asks the active OpenXR runtime for a head-mounted display, exactly like UEVR does at injection
/// (xrCreateInstance + xrGetSystem(HMD)). It only succeeds while the headset is connected (e.g. streaming in
/// Virtual Desktop); otherwise runtimes return XR_ERROR_FORM_FACTOR_UNAVAILABLE.
/// Runs in a child process so the OpenXR loader, runtime DLL and any implicit layers never load into the launcher
/// and the layer-disable environment variables never leak into the game.
/// </summary>
public static class OpenXrProbe
{
    public const string ProbeArg = "--probe";

    public static async Task<ProbeResult> RunAsync(string loaderPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(ProbeArg);
        psi.ArgumentList.Add(loaderPath);

        // Keep implicit layers (e.g. OFXR Bridge) out of the probe; they are only meant for the game.
        foreach (var layer in OpenXrLayers.GetImplicitLayers())
        {
            if (!string.IsNullOrEmpty(layer.DisableEnvironment))
                psi.Environment[layer.DisableEnvironment] = "1";
        }

        using var proc = Process.Start(psi)!;
        var readTask = proc.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            ct.ThrowIfCancellationRequested();
            return new ProbeResult(false, "TIMEOUT", null, null, "The OpenXR runtime did not answer within 20 seconds.");
        }

        var output = (await readTask).Trim();
        var line = output.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
        if (line == null)
            return new ProbeResult(false, "NO_OUTPUT", null, null, $"Probe exited with code {proc.ExitCode}.");
        return JsonSerializer.Deserialize<ProbeResult>(line)!;
    }

    /// <summary>Entry point for the child process (HeadStartVR.exe --probe &lt;openxr_loader.dll&gt;).</summary>
    public static int RunChild(string loaderPath)
    {
        ProbeResult result;
        try
        {
            result = ProbeInProcess(loaderPath);
        }
        catch (Exception ex)
        {
            result = new ProbeResult(false, "EXCEPTION", null, null, ex.Message);
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        stdout.WriteLine(JsonSerializer.Serialize(result));
        return result.Ok ? 0 : 1;
    }

    private const int XR_TYPE_INSTANCE_CREATE_INFO = 3;
    private const int XR_TYPE_SYSTEM_GET_INFO = 4;
    private const int XR_TYPE_SYSTEM_PROPERTIES = 5;
    private const int XR_TYPE_INSTANCE_PROPERTIES = 32;
    private const int XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY = 1;
    private const int XR_ERROR_API_VERSION_UNSUPPORTED = -4;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XrApplicationInfo
    {
        public fixed byte applicationName[128];
        public uint applicationVersion;
        public fixed byte engineName[128];
        public uint engineVersion;
        public ulong apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XrInstanceCreateInfo
    {
        public int type;
        public void* next;
        public ulong createFlags;
        public XrApplicationInfo applicationInfo;
        public uint enabledApiLayerCount;
        public byte** enabledApiLayerNames;
        public uint enabledExtensionCount;
        public byte** enabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XrSystemGetInfo
    {
        public int type;
        public void* next;
        public int formFactor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XrInstanceProperties
    {
        public int type;
        public void* next;
        public ulong runtimeVersion;
        public fixed byte runtimeName[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XrSystemProperties
    {
        public int type;
        public void* next;
        public ulong systemId;
        public uint vendorId;
        public fixed byte systemName[256];
        public uint maxSwapchainImageHeight;
        public uint maxSwapchainImageWidth;
        public uint maxLayerCount;
        public uint orientationTracking;
        public uint positionTracking;
    }

    private static ulong MakeVersion(ulong major, ulong minor, ulong patch) => (major << 48) | (minor << 32) | patch;

    private static unsafe void WriteAscii(byte* dest, int capacity, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var n = Math.Min(bytes.Length, capacity - 1);
        for (var i = 0; i < n; i++) dest[i] = bytes[i];
        dest[n] = 0;
    }

    private static unsafe string ReadAscii(byte* src, int capacity)
    {
        var len = 0;
        while (len < capacity && src[len] != 0) len++;
        return Encoding.UTF8.GetString(src, len);
    }

    private static unsafe ProbeResult ProbeInProcess(string loaderPath)
    {
        if (!File.Exists(loaderPath))
            return new ProbeResult(false, "NO_LOADER", null, null, $"openxr_loader.dll not found at {loaderPath}");

        var lib = NativeLibrary.Load(loaderPath);
        var xrCreateInstance = (delegate* unmanaged<XrInstanceCreateInfo*, ulong*, int>)NativeLibrary.GetExport(lib, "xrCreateInstance");
        var xrDestroyInstance = (delegate* unmanaged<ulong, int>)NativeLibrary.GetExport(lib, "xrDestroyInstance");
        var xrGetSystem = (delegate* unmanaged<ulong, XrSystemGetInfo*, ulong*, int>)NativeLibrary.GetExport(lib, "xrGetSystem");
        var xrResultToString = (delegate* unmanaged<ulong, int, byte*, int>)NativeLibrary.GetExport(lib, "xrResultToString");
        var xrGetInstanceProperties = (delegate* unmanaged<ulong, XrInstanceProperties*, int>)NativeLibrary.GetExport(lib, "xrGetInstanceProperties");
        var xrGetSystemProperties = (delegate* unmanaged<ulong, ulong, XrSystemProperties*, int>)NativeLibrary.GetExport(lib, "xrGetSystemProperties");

        ulong instance = 0;
        int res = 0;
        foreach (var apiVersion in new[] { MakeVersion(1, 0, 0), MakeVersion(1, 1, 0) })
        {
            var ci = new XrInstanceCreateInfo { type = XR_TYPE_INSTANCE_CREATE_INFO };
            WriteAscii(ci.applicationInfo.applicationName, 128, "HeadStartVR-HeadsetProbe");
            WriteAscii(ci.applicationInfo.engineName, 128, "HeadStartVR");
            ci.applicationInfo.applicationVersion = 1;
            ci.applicationInfo.apiVersion = apiVersion;
            res = xrCreateInstance(&ci, &instance);
            if (res != XR_ERROR_API_VERSION_UNSUPPORTED) break;
        }

        if (res != 0)
        {
            var name = res switch
            {
                -51 => "XR_ERROR_RUNTIME_UNAVAILABLE",
                -2 => "XR_ERROR_RUNTIME_FAILURE",
                -46 => "XR_ERROR_INSTANCE_LOST",
                _ => res.ToString(),
            };
            return new ProbeResult(false, name, null, null, "Could not create an OpenXR instance (is the Virtual Desktop Streamer running?).");
        }

        try
        {
            string? runtime = null;
            var props = new XrInstanceProperties { type = XR_TYPE_INSTANCE_PROPERTIES };
            if (xrGetInstanceProperties(instance, &props) == 0)
            {
                var v = props.runtimeVersion;
                runtime = $"{ReadAscii(props.runtimeName, 128)} {v >> 48}.{(v >> 32) & 0xFFFF}.{v & 0xFFFFFFFF}";
            }

            var gi = new XrSystemGetInfo { type = XR_TYPE_SYSTEM_GET_INFO, formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY };
            ulong systemId = 0;
            res = xrGetSystem(instance, &gi, &systemId);
            if (res != 0)
            {
                var buf = stackalloc byte[64];
                var name = xrResultToString(instance, res, buf) == 0 ? ReadAscii(buf, 64) : res.ToString();
                return new ProbeResult(false, name, runtime, null, null);
            }

            string? systemName = null;
            var sp = new XrSystemProperties { type = XR_TYPE_SYSTEM_PROPERTIES };
            if (xrGetSystemProperties(instance, systemId, &sp) == 0)
                systemName = ReadAscii(sp.systemName, 256);

            return new ProbeResult(true, "XR_SUCCESS", runtime, systemName, null);
        }
        finally
        {
            xrDestroyInstance(instance);
        }
    }
}
