using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using HeadStartVR.Models;
using HeadStartVR.Services;

namespace HeadStartVR.ViewModels;

public enum StepState
{
    Pending,
    Running,
    WaitingForYou,
    Done,
    Warning,
    Failed,
    Skipped,
}

public sealed class LaunchStep : ObservableObject
{
    public LaunchStep(string title) => Title = title;

    public string Title { get; }

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private StepState _state = StepState.Pending;
    public StepState State { get => _state; set => Set(ref _state, value); }
}

public sealed class PromptButton
{
    public required string Label { get; init; }
    public required string Key { get; init; }
    public bool IsPrimary { get; init; }
    public required RelayCommand Command { get; init; }
}

/// <summary>
/// One "Launch in VR" run: OFXR → Virtual Desktop → wait for the headset → start the game → countdown → UEVR inject → play.
/// OFXR and Virtual Desktop are optional and skipped when not installed/used.
/// Runs on the UI thread with async/await; questions to the user are shown in the launch panel (no modal dialogs),
/// so everything can be answered from the desktop view inside Virtual Desktop as well.
/// </summary>
public sealed class LaunchSession : ObservableObject
{
    private readonly GameEntry _game;
    private readonly AppSettings _settings;
    private readonly ProfileManager _profiles;
    private readonly Action _saveGames;
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<string>? _prompt;
    private volatile bool _injectNow;
    private bool _headsetOn;
    private bool _vdInstalled;
    private int _extraSeconds;

    public LaunchSession(GameEntry game, AppSettings settings, ProfileManager profiles, Action saveGames)
    {
        _game = game;
        _settings = settings;
        _profiles = profiles;
        _saveGames = saveGames;
        // Always executable: they only set flags, and CanExecute wouldn't refresh while you're in the headset.
        CancelCommand = new RelayCommand(Cancel);
        InjectNowCommand = new RelayCommand(() => _injectNow = true);
        AddTimeCommand = new RelayCommand(() => _extraSeconds += 15);
    }

    public string GameName => _game.Name;
    public ObservableCollection<LaunchStep> Steps { get; } = new();
    public ObservableCollection<PromptButton> PromptButtons { get; } = new();
    public RelayCommand CancelCommand { get; }
    public RelayCommand InjectNowCommand { get; }
    public RelayCommand AddTimeCommand { get; }

    private string? _promptTitle;
    public string? PromptTitle { get => _promptTitle; private set => Set(ref _promptTitle, value); }

    private string? _promptMessage;
    public string? PromptMessage { get => _promptMessage; private set { Set(ref _promptMessage, value); Raise(nameof(HasPrompt)); } }

    public bool HasPrompt => PromptMessage != null;

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }

    private string _status = "Starting…";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _showCountdown;
    public bool ShowCountdown { get => _showCountdown; private set => Set(ref _showCountdown, value); }

    private int _countdown;
    public int Countdown { get => _countdown; private set => Set(ref _countdown, value); }

    private double _countdownProgress;
    public double CountdownProgress { get => _countdownProgress; private set => Set(ref _countdownProgress, value); }

    public event EventHandler? Finished;

    public void Cancel()
    {
        _cts.Cancel();
        _prompt?.TrySetCanceled();
    }

    private LaunchStep AddStep(string title)
    {
        var step = new LaunchStep(title) { State = StepState.Running };
        Steps.Add(step);
        return step;
    }

    private async Task<string> AskAsync(LaunchStep? step, string title, string message, params (string label, string key, bool primary)[] buttons)
    {
        if (step != null) step.State = StepState.WaitingForYou;
        _prompt = new TaskCompletionSource<string>();
        PromptTitle = title;
        PromptMessage = message;
        PromptButtons.Clear();
        foreach (var (label, key, primary) in buttons)
        {
            var k = key;
            PromptButtons.Add(new PromptButton { Label = label, Key = key, IsPrimary = primary, Command = new RelayCommand(() => _prompt?.TrySetResult(k)) });
        }
        try
        {
            using var reg = _cts.Token.Register(() => _prompt.TrySetCanceled());
            return await _prompt.Task;
        }
        finally
        {
            PromptMessage = null;
            PromptTitle = null;
            PromptButtons.Clear();
            if (step != null && step.State == StepState.WaitingForYou) step.State = StepState.Running;
        }
    }

    public async Task RunAsync()
    {
        IsRunning = true;
        var ct = _cts.Token;
        _vdInstalled = VirtualDesktop.IsInstalled(_settings.VdStreamerPath);
        LaunchStep? current = null;
        try
        {
            var build = UevrBuilds.FromFolder(_game.UevrBuildPath);
            var alreadyRunning = false;

            // 1. Pre-flight -------------------------------------------------------------------------------
            current = AddStep("Check setup");
            Status = "Checking setup";
            if (!File.Exists(_game.ExePath)) throw new LaunchFailed($"Game exe not found:\n{_game.ExePath}");
            if (!File.Exists(build.InjectorPath)) throw new LaunchFailed($"UEVRInjector.exe not found in\n{build.Folder}");
            if (!File.Exists(build.LoaderPath)) throw new LaunchFailed($"openxr_loader.dll not found in\n{build.Folder}");

            var notes = new List<string>();
            var runtime = UevrBuilds.GetRuntime(build.InjectorPath);
            if (runtime != InjectorRuntime.OpenXR)
            {
                var why = runtime == InjectorRuntime.NotSet
                    ? "has never been opened from this folder, so it will default to OpenVR"
                    : "is set to OpenVR";
                var answer = await AskAsync(current, "UEVR runtime",
                    $"The UEVR build “{build.Name}” {why}. With Virtual Desktop it must use OpenXR (OpenVR causes head-rotation lag).\n\n" +
                    "Switch it to OpenXR? This changes only the injector's saved OpenXR/OpenVR choice for this build.",
                    ("Switch to OpenXR", "switch", true), ("Keep as is", "keep", false));
                if (answer == "switch")
                {
                    if (Process.GetProcessesByName("UEVRInjector").Length > 0)
                        throw new LaunchFailed("Close the open UEVR injector window first (it saves its settings when it closes), then launch again.");
                    UevrBuilds.SetOpenXR(build.InjectorPath);
                    notes.Add("UEVR build switched to OpenXR");
                }
            }

            var activeRuntime = VirtualDesktop.GetActiveRuntime();
            var overrideJson = VirtualDesktop.GetRuntimeOverride();
            if (overrideJson != null)
            {
                var a = await AskAsync(current, "OpenXR override found",
                    $"The XR_RUNTIME_JSON environment variable forces every OpenXR app to use:\n{overrideJson}\n\n" +
                    "This is a known cause of UEVR failing to start VR. Remove it in Windows' environment variables unless you set it on purpose.",
                    ("Continue anyway", "go", true), ("Cancel", "cancel", false));
                if (a == "cancel") throw new OperationCanceledException();
            }
            else if (string.IsNullOrEmpty(activeRuntime))
            {
                var a = await AskAsync(current, "No OpenXR runtime",
                    "Windows has no active OpenXR runtime, so UEVR can't start VR. Open your headset software (Virtual Desktop, SteamVR or Meta Link) and set it as the OpenXR runtime.",
                    ("Continue anyway", "go", false), ("Cancel", "cancel", true));
                if (a == "cancel") throw new OperationCanceledException();
            }
            else if (_vdInstalled && !VirtualDesktop.IsVdRuntime(activeRuntime))
            {
                var a = await AskAsync(current, "OpenXR runtime",
                    $"The active OpenXR runtime is {VirtualDesktop.DescribeRuntime(activeRuntime)}, not Virtual Desktop.\n\n" +
                    "If you play through Virtual Desktop, set “OpenXR Runtime” to Automatic or VDXR in the Virtual Desktop Streamer app for the best performance with UEVR.",
                    ("Continue anyway", "go", true), ("Cancel", "cancel", false));
                if (a == "cancel") throw new OperationCanceledException();
            }
            else
            {
                notes.Add("OpenXR runtime: " + VirtualDesktop.DescribeRuntime(activeRuntime));
            }

            if (ProfileManager.IsGameRunning(_game))
            {
                alreadyRunning = true;
                notes.Add("game is already running; will inject into it");
            }
            else if (_profiles.FindPendingUpdate(_game) is { } pending)
            {
                var zip = pending.ZipPath;
                var where = pending.FromWatchedFolder ? $"was found in {Path.GetDirectoryName(zip)}" : "is linked to this game";
                var dllNote = ProfileManager.ZipContainsDll(zip)
                    ? "\n\nThis profile includes plugin DLLs, which run code inside the game. Only apply it if it comes from a trusted author."
                    : "";
                var a = await AskAsync(current, "New UEVR profile",
                    $"{Path.GetFileName(zip)} (modified {File.GetLastWriteTime(zip):d MMM yyyy HH:mm}) {where} and differs from the profile last applied.\n\n" +
                    $"Apply it now? The current profile is backed up to {_settings.BackupsRoot} first, then replaced.{dllNote}",
                    ("Apply new profile", "apply", true), ("Not now", "later", false), ("Ignore this zip", "ignore", false));
                if (a == "apply")
                {
                    var r = _profiles.Apply(_game, zip);
                    notes.Add($"new profile applied ({r.FilesWritten} files)");
                }
                else if (a == "ignore")
                {
                    _game.IgnoredProfileHash = pending.Hash;
                }
                _saveGames();
            }

            current.Detail = notes.Count == 0 ? "All good" : string.Join(" · ", notes);
            current.State = StepState.Done;

            // 2. OFXR --------------------------------------------------------------------------------------
            if (_game.UseOfxr)
            {
                current = AddStep("Arm OFXR");
                Status = "OFXR";
                if (!Ofxr.IsInstalled(_settings.OfxrTrayPath) && !Ofxr.IsTrayRunning(_settings.OfxrTrayPath))
                {
                    var a = await AskAsync(current, "OFXR not found",
                        "This game is set to use OFXR Bridge frame generation, but OFXRBridgeTray.exe wasn't found. Set its location in Settings, or turn OFXR off for this game with Edit.",
                        ("Continue without OFXR", "skip", true), ("Cancel", "cancel", false));
                    if (a == "cancel") throw new OperationCanceledException();
                    current.Detail = "Not found; skipped";
                    current.State = StepState.Skipped;
                }
                else if (!Ofxr.IsTrayRunning(_settings.OfxrTrayPath))
                {
                    current.Detail = "Starting the OFXR tray app…";
                    ProcessLauncher.StartAsUser(_settings.OfxrTrayPath);
                    for (var i = 0; i < 20 && !Ofxr.IsTrayRunning(_settings.OfxrTrayPath); i++) await Task.Delay(500, ct);
                }

                if (current.State == StepState.Skipped)
                {
                    // OFXR not installed: nothing to arm.
                }
                else if (Ofxr.IsArmed)
                {
                    current.Detail = "Already armed";
                }
                else
                {
                    Voice.Say("Please arm O F X R.");
                    while (true)
                    {
                        current.Detail = "Waiting for you to arm it from the tray icon";
                        var a = await AskAsync(current, "Arm OFXR",
                            "Right-click the OFXR icon in the Windows system tray (bottom-right, maybe under ^) and choose “Arm bridge until manual disarm”.\n\nThen click Done.",
                            ("Done", "done", true), ("Continue without OFXR", "skip", false));
                        if (a == "skip")
                        {
                            current.Detail = "Skipped for this launch";
                            current.State = StepState.Skipped;
                            break;
                        }
                        for (var i = 0; i < 6 && !Ofxr.IsArmed; i++) await Task.Delay(250, ct);
                        if (Ofxr.IsArmed)
                        {
                            current.Detail = "Armed";
                            break;
                        }
                        var b = await AskAsync(current, "OFXR not armed yet",
                            "I can't see OFXR's OpenXR layer registered yet. Make sure you picked “Arm bridge until manual disarm” in the tray menu.",
                            ("Check again", "again", true), ("Continue without OFXR", "skip", false));
                        if (b == "skip")
                        {
                            current.Detail = "Skipped for this launch";
                            current.State = StepState.Skipped;
                            break;
                        }
                        if (Ofxr.IsArmed)
                        {
                            current.Detail = "Armed";
                            break;
                        }
                    }
                }
                if (current.State != StepState.Skipped) current.State = StepState.Done;
            }
            else if (Ofxr.IsArmed)
            {
                current = AddStep("OFXR");
                var a = await AskAsync(current, "OFXR is armed",
                    "This game is set to run without OFXR, but OFXR is currently armed and would apply to it. Disarm it from the tray icon if you don't want frame generation.",
                    ("I disarmed it", "ok", true), ("Keep it armed", "keep", false));
                current.Detail = a == "keep" ? "Left armed" : (Ofxr.IsArmed ? "Still armed" : "Disarmed");
                current.State = Ofxr.IsArmed ? StepState.Warning : StepState.Done;
            }

            // 3. Virtual Desktop Streamer (only for people who use Virtual Desktop) ---------------------
            if (_vdInstalled)
            {
                current = AddStep("Virtual Desktop Streamer");
                Status = "Virtual Desktop";
                if (VirtualDesktop.IsStreamerRunning)
                {
                    current.Detail = "Running";
                }
                else
                {
                    current.Detail = "Starting…";
                    ProcessLauncher.StartAsUser(_settings.VdStreamerPath);
                    for (var i = 0; i < 60 && !VirtualDesktop.IsStreamerRunning; i++) await Task.Delay(500, ct);
                    if (!VirtualDesktop.IsStreamerRunning) throw new LaunchFailed("Virtual Desktop Streamer did not start.");
                    current.Detail = "Started";
                }
                current.State = StepState.Done;
            }

            // 4–7 run in a loop so a failed VR start can be retried with a fresh game process
            // (UEVR can't be injected twice, and it only looks for the headset once).
            Process game;
            for (var attempt = 1; ; attempt++)
            {
                var suffix = attempt == 1 ? "" : $" (try {attempt})";

                // 4. Headset --------------------------------------------------------------------------------
                current = AddStep("Headset connected" + suffix);
                Status = "Waiting for headset";
                await WaitForHeadsetAsync(current, build, firstTime: true, ct);
                // From here on you're wearing the headset: no more questions, only spoken notices.
                _headsetOn = true;

                // 5. Game -----------------------------------------------------------------------------------
                current = AddStep("Start game" + suffix);
                Status = "Starting game";
                if (alreadyRunning)
                {
                    current.Detail = "Already running";
                    game = Process.GetProcessesByName(_game.ExeStem).First();
                }
                else
                {
                    StartGame(current);
                    Voice.Say("Starting the game.");
                    game = await WaitForGameProcessAsync(current, ct);
                }

                current.Detail = "Waiting for the game window…";
                var windowStart = DateTime.Now;
                var windowNotice = windowStart.AddMinutes(3);
                while (true)
                {
                    game.Refresh();
                    if (game.HasExited) throw new LaunchFailed("The game closed before its window appeared.");
                    if (game.MainWindowHandle != IntPtr.Zero && game.MainWindowTitle.Length > 0) break;
                    if (DateTime.Now > windowNotice)
                    {
                        // Headset is on: tell, don't ask. Cancel stays available on the launcher.
                        Voice.Say("The game is running but hasn't opened its window yet. Still waiting.");
                        current.Detail = $"Still waiting for the game window ({(int)(DateTime.Now - windowStart).TotalMinutes} min). Cancel from the launcher to stop.";
                        windowNotice = DateTime.Now.AddMinutes(3);
                    }
                    await Task.Delay(500, ct);
                }
                current.Detail = $"Running (pid {game.Id})";
                current.State = StepState.Done;

                // 6. Countdown ------------------------------------------------------------------------------
                if (!alreadyRunning && _game.InjectDelaySeconds > 0)
                {
                    current = AddStep("Let the game reach its menu" + suffix);
                    Status = "Countdown";
                    await CountdownAsync(current, game, ct);
                    current.State = StepState.Done;
                }

                // 7. Inject ---------------------------------------------------------------------------------
                current = AddStep("Inject UEVR" + suffix);
                Status = "Injecting";
                var outcome = await InjectAsync(current, build, game, ct);
                if (outcome == InjectOutcome.Ok) break;

                // You're wearing the headset by now, so nothing here asks for a click. The game has only just
                // reached its menu, so restarting it once costs no progress.
                if (attempt >= MaxAttempts)
                {
                    current.Detail += "\nStill no VR after a restart, so the game keeps running as is. Check Virtual Desktop and the UEVR build, then launch again.";
                    Voice.Say("V R still didn't start. The game keeps running without V R.");
                    break;
                }
                Voice.Say("V R didn't start. Restarting the game to try again.");
                current.Detail += "\nRestarting the game and trying once more.";
                await CloseGameAsync(game, ct);
                alreadyRunning = false;
            }

            // 8. Playing ------------------------------------------------------------------------------------
            _game.LastPlayed = DateTime.Now;
            _saveGames();
            current = AddStep("Playing");
            Status = "Playing";
            current.Detail = "Have fun! This page updates when the game closes.";
            while (!game.HasExited)
            {
                await Task.Delay(2000, ct);
                game.Refresh();
            }
            current.Detail = "Game closed";
            current.State = StepState.Done;
            Status = "Finished";
        }
        catch (OperationCanceledException)
        {
            if (current != null && current.State is StepState.Running or StepState.WaitingForYou)
            {
                current.State = StepState.Skipped;
                current.Detail = "Cancelled";
            }
            Status = "Cancelled";
        }
        catch (LaunchFailed ex)
        {
            Fail(current, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("Launch error: " + ex);
            Fail(current, ex.Message);
        }
        finally
        {
            ShowCountdown = false;
            IsRunning = false;
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>First try plus one automatic restart.</summary>
    private const int MaxAttempts = 2;

    private enum InjectOutcome
    {
        Ok,
        NotLoaded,
        VrFailed,
    }

    private void StartGame(LaunchStep step)
    {
        if (_game.LaunchMethod == LaunchMethod.Steam && !string.IsNullOrEmpty(_game.SteamAppId))
        {
            var uri = string.IsNullOrWhiteSpace(_game.LaunchArgs)
                ? $"steam://rungameid/{_game.SteamAppId}"
                : $"steam://run/{_game.SteamAppId}//{Uri.EscapeDataString(_game.LaunchArgs)}/";
            ProcessLauncher.OpenUriAsUser(uri);
            step.Detail = "Started through Steam";
        }
        else if (_game.LaunchMethod == LaunchMethod.Uri && !string.IsNullOrWhiteSpace(_game.LaunchUri))
        {
            ProcessLauncher.OpenUriAsUser(_game.LaunchUri);
            step.Detail = "Started through its launcher link";
        }
        else
        {
            ProcessLauncher.StartAsUser(_game.ExePath, _game.LaunchArgs, Path.GetDirectoryName(_game.ExePath));
            step.Detail = "Started " + _game.ExeFileName;
        }
    }

    private async Task<InjectOutcome> InjectAsync(LaunchStep step, UevrBuild build, Process game, CancellationToken ct)
    {
        // The headset may have gone to sleep during the countdown; UEVR checks for it only once.
        await WaitForHeadsetAsync(step, build, firstTime: false, ct);
        step.State = StepState.Running;
        game.Refresh();
        if (game.HasExited) throw new LaunchFailed("The game closed before UEVR could be injected.");

        var injectStart = DateTime.Now;
        step.Detail = $"Running {build.Name}\\UEVRInjector.exe as administrator…";
        ProcessLauncher.StartElevated(build.InjectorPath, new[] { "--attach=" + _game.ExeFileName }, build.Folder);

        var injected = false;
        for (var i = 0; i < 60 && !injected; i++)
        {
            await Task.Delay(1000, ct);
            game.Refresh();
            if (game.HasExited) throw new LaunchFailed("The game closed during injection.");
            injected = HasModule(game, "UEVRBackend.dll");
        }
        if (!injected)
        {
            step.State = StepState.Warning;
            step.Detail = "Couldn't confirm UEVR loaded into the game within 60 s. Check the UEVR injector window.";
            return InjectOutcome.NotLoaded;
        }

        step.Detail = "UEVR loaded, starting VR…";
        var vr = await WatchUevrLogAsync(injectStart, ct);
        if (vr.ok == false)
        {
            step.Detail = "UEVR could not start VR: " + vr.message;
            step.State = StepState.Failed;
            Voice.Say("U E V R could not start V R.");
            return InjectOutcome.VrFailed;
        }

        step.Detail = vr.ok == true ? "UEVR is running in OpenXR" : "UEVR loaded (VR start not confirmed in its log)";
        step.State = StepState.Done;
        if (vr.ok == true) Voice.Say("U E V R injected. Enjoy!");
        return InjectOutcome.Ok;
    }

    /// <summary>Close the game for a retry: ask it nicely, then force it after a few seconds (no prompts).</summary>
    internal async Task CloseGameAsync(Process game, CancellationToken ct, int graceSeconds = 10)
    {
        var step = AddStep("Restart game");
        Status = "Restarting game";
        step.Detail = "Closing the game…";
        try { game.CloseMainWindow(); } catch { }

        for (var i = 0; i < graceSeconds * 2 && !game.HasExited; i++)
        {
            await Task.Delay(500, ct);
            game.Refresh();
        }
        if (!game.HasExited)
        {
            // Usually a "quit game?" dialog. It's at the main menu, so nothing is lost.
            step.Detail = "Game didn't close by itself; forcing it closed…";
            try { game.Kill(entireProcessTree: true); } catch { }
            for (var i = 0; i < 30 && !game.HasExited; i++)
            {
                await Task.Delay(500, ct);
                game.Refresh();
            }
        }
        if (!game.HasExited) throw new LaunchFailed("The game couldn't be closed for the retry.");

        // Let Steam/launchers notice the exit before starting again.
        await Task.Delay(3000, ct);
        step.Detail = "Closed";
        step.State = StepState.Done;
    }

    /// <summary>The first sentence of a failure, without paths, so it reads well aloud.</summary>
    private static string SpokenReason(string message)
    {
        var first = message.Split('\n')[0].Trim();
        return first.Contains('\\') || first.Length > 120 ? "" : first.TrimEnd('.') + ".";
    }

    private void Fail(LaunchStep? step, string message)
    {
        Log.Write($"Launch of {_game.Name} failed: {message}");
        if (_headsetOn) Voice.Say("The launch stopped. " + SpokenReason(message) + " Details are on the launcher.");
        if (step != null)
        {
            step.State = StepState.Failed;
            step.Detail = message;
        }
        Status = "Failed";
    }

    private async Task WaitForHeadsetAsync(LaunchStep step, UevrBuild build, bool firstTime, CancellationToken ct)
    {
        var consecutive = 0;
        var announced = false;
        var skip = false;
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.HeadsetPollSeconds, 1, 10));

        // Offer a skip button while polling (non-blocking prompt).
        Task<string>? skipPrompt = null;

        try
        {
            while (true)
            {
                var r = await OpenXrProbe.RunAsync(build.LoaderPath, ct);
                if (r.Ok)
                {
                    consecutive++;
                    if (consecutive >= 2)
                    {
                        if (firstTime)
                        {
                            step.Detail = $"{r.System ?? "Headset"} via {r.Runtime ?? "OpenXR"}";
                            step.State = StepState.Done;
                            Voice.Say("Headset connected.");
                        }
                        return;
                    }
                }
                else
                {
                    consecutive = 0;
                    if (!announced)
                    {
                        announced = true;
                        Voice.Say(firstTime
                            ? (_vdInstalled ? "Put on your headset and connect Virtual Desktop." : "Put on your headset.")
                            : "The headset is not connected. Put it on to continue.");
                        skipPrompt = AskAsync(step, firstTime ? "Connect your headset" : "Headset disconnected",
                            (_vdInstalled
                                ? "Put on your headset and connect to this PC in the Virtual Desktop app."
                                : "Put on your headset and connect it to this PC (SteamVR, Air Link, Steam Link or a cable).") +
                            " The launcher continues automatically once the headset is available to OpenXR.",
                            ("Skip this check", "skip", false));
                    }
                    var what = r.Result switch
                    {
                        "XR_ERROR_FORM_FACTOR_UNAVAILABLE" => "headset not connected",
                        "XR_ERROR_RUNTIME_UNAVAILABLE" => "OpenXR runtime not available",
                        _ => r.Result,
                    };
                    step.Detail = $"Waiting… ({what}{(r.Runtime != null ? ", " + r.Runtime : "")})";
                }

                if (skipPrompt != null)
                {
                    var delay = Task.Delay(r.Ok ? TimeSpan.FromMilliseconds(1500) : interval, ct);
                    if (await Task.WhenAny(skipPrompt, delay) == skipPrompt && !skipPrompt.IsCanceled)
                    {
                        skip = true;
                        break;
                    }
                    await delay.ContinueWith(_ => { }, TaskScheduler.Default);
                    ct.ThrowIfCancellationRequested();
                }
                else
                {
                    await Task.Delay(r.Ok ? TimeSpan.FromMilliseconds(1500) : interval, ct);
                }
            }
        }
        finally
        {
            // Close the skip prompt if the headset turned up on its own.
            if (skipPrompt != null && !skipPrompt.IsCompleted) _prompt?.TrySetResult("connected");
        }

        if (skip)
        {
            step.Detail = "Check skipped. UEVR may fall back to flat if the headset isn't connected.";
            step.State = firstTime ? StepState.Warning : StepState.Running;
        }
    }

    private async Task<Process> WaitForGameProcessAsync(LaunchStep step, CancellationToken ct)
    {
        var start = DateTime.Now;
        var notice = start.AddMinutes(2);
        step.Detail = $"Waiting for {_game.ExeFileName}…";
        while (true)
        {
            var p = Process.GetProcessesByName(_game.ExeStem).FirstOrDefault();
            if (p != null) return p;
            if (DateTime.Now > notice)
            {
                // Headset is on: tell, don't ask. Cancel stays available on the launcher.
                Voice.Say("The game hasn't started yet. A launcher or Steam window may be waiting on your desktop.");
                step.Detail = $"Still waiting for {_game.ExeFileName} ({(int)(DateTime.Now - start).TotalMinutes} min). A launcher or Steam window may need you; Cancel from the launcher to stop.";
                notice = DateTime.Now.AddMinutes(3);
            }
            await Task.Delay(500, ct);
        }
    }

    private async Task CountdownAsync(LaunchStep step, Process game, CancellationToken ct)
    {
        _injectNow = false;
        _extraSeconds = 0;
        var total = _game.InjectDelaySeconds;
        var start = DateTime.Now;
        DateTime? comboSince = null;
        ShowCountdown = true;
        Voice.Say($"Injecting U E V R in {total} seconds.");
        var spokeFive = false;
        try
        {
            while (true)
            {
                var elapsed = (DateTime.Now - start).TotalSeconds;
                var limit = total + _extraSeconds;
                var remaining = Math.Max(0, limit - elapsed);
                Countdown = (int)Math.Ceiling(remaining);
                CountdownProgress = limit <= 0 ? 1 : Math.Clamp(elapsed / limit, 0, 1);
                step.Detail = _settings.GamepadInjectCombo
                    ? $"Injecting in {Countdown}s. Hold Back + Start on your controller to inject now."
                    : $"Injecting in {Countdown}s";

                if (remaining <= 0 || _injectNow) break;
                if (!spokeFive && remaining <= 5)
                {
                    spokeFive = true;
                    Voice.Say("Injecting in 5 seconds.");
                }

                if (_settings.GamepadInjectCombo)
                {
                    if (Gamepad.IsBackStartHeld())
                    {
                        comboSince ??= DateTime.Now;
                        if ((DateTime.Now - comboSince.Value).TotalSeconds >= 1) break;
                    }
                    else comboSince = null;
                }

                game.Refresh();
                if (game.HasExited) throw new LaunchFailed("The game closed during the countdown.");
                await Task.Delay(100, ct);
            }
        }
        finally
        {
            ShowCountdown = false;
        }
        step.Detail = Countdown > 0 ? $"Skipped the last {Countdown}s" : "Ready";
    }

    private static bool HasModule(Process p, string module)
    {
        try
        {
            p.Refresh();
            foreach (ProcessModule m in p.Modules)
                if (string.Equals(m.ModuleName, module, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch
        {
            // Access denied while not elevated, or the process is mid-exit.
        }
        return false;
    }

    /// <summary>Reads UEVR's log.txt for the OpenXR start-up result (success: "Creating OpenXR session").</summary>
    private async Task<(bool? ok, string message)> WatchUevrLogAsync(DateTime since, CancellationToken ct)
    {
        var log = Path.Combine(_profiles.ProfileFolder(_game), "log.txt");
        for (var i = 0; i < 40; i++)
        {
            try
            {
                if (File.Exists(log) && File.GetLastWriteTime(log) >= since.AddSeconds(-2))
                {
                    string text;
                    using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                        text = await sr.ReadToEndAsync(ct);

                    var lines = text.Split('\n');
                    var err = lines.LastOrDefault(l => l.Contains("Could not create openxr", StringComparison.OrdinalIgnoreCase) ||
                                                       l.Contains("OpenXR not loaded", StringComparison.OrdinalIgnoreCase));
                    if (err != null)
                    {
                        var idx = err.IndexOf("Could not", StringComparison.OrdinalIgnoreCase);
                        return (false, (idx >= 0 ? err[idx..] : err).Trim());
                    }
                    if (lines.Any(l => l.Contains("Creating OpenXR session", StringComparison.OrdinalIgnoreCase)))
                        return (true, "");
                }
            }
            catch (IOException) { }
            await Task.Delay(500, ct);
        }
        return (null, "");
    }
}

public sealed class LaunchFailed : Exception
{
    public LaunchFailed(string message) : base(message) { }
}
