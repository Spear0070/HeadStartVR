using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using HeadStartVR.Services;
using HeadStartVR.Views;

namespace HeadStartVR;

public partial class App : Application
{
    public static Store Store { get; private set; } = null!;
    public static string DataDir { get; private set; } = "";

    /// <summary>Game id or name from --launch (desktop shortcuts).</summary>
    public static string? LaunchRequest { get; private set; }

    /// <summary>True when the user declined the UAC prompt; the injector will then ask for admin itself.</summary>
    public static bool ElevationDeclined { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;

        // Child-process mode used by the headset check.
        var probeIdx = Array.IndexOf(args, OpenXrProbe.ProbeArg);
        if (probeIdx >= 0 && probeIdx + 1 < args.Length)
        {
            Shutdown(OpenXrProbe.RunChild(args[probeIdx + 1]));
            return;
        }

        var noElevate = args.Contains("--no-elevate");
        var dataIdx = Array.IndexOf(args, "--data");
        var customData = dataIdx >= 0 && dataIdx + 1 < args.Length;
        var dataDir = customData ? args[dataIdx + 1] : Path.Combine(AppContext.BaseDirectory, "data");
        var launchIdx = Array.IndexOf(args, "--launch");
        var launchRequest = launchIdx >= 0 && launchIdx + 1 < args.Length ? args[launchIdx + 1] : null;

        // One launcher at a time: hand shortcut requests to the one already running (before asking for admin,
        // so a desktop shortcut doesn't trigger a UAC prompt when the launcher is already open).
        // A custom --data folder (testing / portable copies) runs independently.
        if (!customData && InstanceBridge.IsAnotherInstanceRunning())
        {
            if (InstanceBridge.TrySend(dataDir, launchRequest != null ? "launch " + launchRequest : "show") || ProcessLauncher.IsElevated)
            {
                Shutdown(0);
                return;
            }
            // Couldn't write the request unelevated: the elevated copy started below will forward it.
        }

        // Run as administrator so the UEVR injector can be started elevated later without a UAC prompt
        // popping up while the headset is on.
        if (!noElevate && !ProcessLauncher.IsElevated)
        {
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
                Shutdown(0);
                return;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                ElevationDeclined = true; // UAC cancelled: carry on unelevated.
            }
        }

        if (!customData) InstanceBridge.Claim();
        DataDir = dataDir;
        LaunchRequest = launchRequest;
        Store = new Store(dataDir);
        Log.Init(dataDir);
        Voice.Enabled = Store.Settings.VoicePrompts;
        Log.Write($"Started (elevated: {ProcessLauncher.IsElevated}, data: {dataDir})");

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("Unhandled: " + ex.Exception);
            Dialog.Show(MainWindow, "Something went wrong", ex.Exception.Message, ("OK", "ok", true));
            ex.Handled = true;
        };

        base.OnStartup(e);
        MainWindow = new Views.MainWindow();
        MainWindow.Show();
    }
}
