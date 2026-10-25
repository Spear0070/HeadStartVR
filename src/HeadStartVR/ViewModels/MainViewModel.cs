using System.Collections.ObjectModel;
using HeadStartVR.Services;

namespace HeadStartVR.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<GameItem> Games { get; } = new();

    public MainViewModel() => Games.CollectionChanged += (_, _) => RaiseViews();

    private GameItem? _selected;
    public GameItem? Selected { get => _selected; set { Set(ref _selected, value); RaiseViews(); } }

    private LaunchSession? _session;
    public LaunchSession? Session { get => _session; set { Set(ref _session, value); RaiseViews(); } }

    public bool ShowEmpty => Session == null && Games.Count == 0;
    public bool ShowDetail => Session == null && Selected != null;
    public bool ShowLaunch => Session != null;
    public string LibraryCount => Games.Count.ToString();

    private void RaiseViews()
    {
        Raise(nameof(ShowEmpty));
        Raise(nameof(ShowDetail));
        Raise(nameof(ShowLaunch));
        Raise(nameof(LibraryCount));
    }

    // "New profile downloaded" banner --------------------------------------------------------------
    private string? _bannerText;
    public string? BannerText { get => _bannerText; set => Set(ref _bannerText, value); }

    public GameItem? BannerGame { get; set; }

    // Status bar ---------------------------------------------------------------------------------
    private bool _isAdmin;
    public bool IsAdmin { get => _isAdmin; set => Set(ref _isAdmin, value); }

    private string _runtime = "";
    public string Runtime { get => _runtime; set => Set(ref _runtime, value); }

    private bool _runtimeOk;
    public bool RuntimeOk { get => _runtimeOk; set => Set(ref _runtimeOk, value); }

    private bool _vdRunning;
    public bool VdRunning { get => _vdRunning; set => Set(ref _vdRunning, value); }

    private bool _ofxrArmed;
    public bool OfxrArmed { get => _ofxrArmed; set => Set(ref _ofxrArmed, value); }

    private bool _vdInstalled;
    public bool VdInstalled { get => _vdInstalled; set => Set(ref _vdInstalled, value); }

    /// <summary>Set from settings; Virtual Desktop is optional.</summary>
    public string VdStreamerPath { get; set; } = "";

    public void RefreshStatus()
    {
        IsAdmin = ProcessLauncher.IsElevated;
        var rt = VirtualDesktop.GetActiveRuntime();
        VdInstalled = VirtualDesktop.IsInstalled(VdStreamerPath);
        Runtime = VirtualDesktop.DescribeRuntime(rt);
        RuntimeOk = rt != null && VirtualDesktop.GetRuntimeOverride() == null && (!VdInstalled || VirtualDesktop.IsVdRuntime(rt));
        VdRunning = VirtualDesktop.IsStreamerRunning;
        OfxrArmed = Ofxr.IsArmed;
    }
}
