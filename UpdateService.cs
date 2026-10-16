using System.Diagnostics;
using System.Windows.Threading;

namespace Fader;

/// <summary>Update state shared by the tray menu and the settings window. All members run on the UI thread.</summary>
public sealed class UpdateService : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _autoCheck;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _available;
    private string _status = "";
    private bool _busy;
    private DateTime _lastCheck = DateTime.MinValue;

    /// <summary>Raised once per newly found version (for the tray balloon).</summary>
    public event Action<UpdateInfo>? Found;
    /// <summary>The new exe is in place and started: the app must shut down now.</summary>
    public event Action? RestartRequested;

    public UpdateService(Dispatcher dispatcher, Func<bool> autoCheck)
    {
        _dispatcher = dispatcher;
        _autoCheck = autoCheck;
        _status = $"You have v{Current}.";
        // First check shortly after start (not during boot), then twice a day.
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(45) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(12);
            if (_autoCheck()) await CheckAsync(manual: false);
        };
        _timer.Start();
    }

    public string Current => Updater.Current.ToString(3);
    public UpdateInfo? Available { get => _available; private set { if (Set(ref _available, value)) { Raise(nameof(HasUpdate)); Raise(nameof(NoUpdate)); Raise(nameof(UpdateLabel)); } } }
    public bool HasUpdate => _available != null;
    public bool NoUpdate => _available == null;
    public string UpdateLabel => _available == null ? "" : _available.DownloadUrl != null ? $"Update to v{_available.Version.ToString(3)}" : $"Get v{_available.Version.ToString(3)}";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;

    public async Task CheckAsync(bool manual)
    {
        if (Busy) return;
        if (!manual && DateTime.UtcNow - _lastCheck < TimeSpan.FromHours(1)) return;
        Busy = true;
        if (manual) Status = "Checking GitHub…";
        try
        {
            var info = await Updater.CheckAsync();
            _lastCheck = DateTime.UtcNow;
            bool isNew = info != null && info.Version != _available?.Version;
            Available = info;
            Status = info == null
                ? $"You have v{Current}, the latest version."
                : info.DownloadUrl != null
                    ? $"v{info.Version.ToString(3)} is available (you have v{Current})."
                    : $"v{info.Version.ToString(3)} is out, but has no {(Updater.IsSelfContained ? "portable" : "small")} exe: get it from the release page.";
            Log.Write($"[update] check: latest={(info?.Tag ?? "none newer")} current={Current}");
            if (isNew) Found?.Invoke(info!);
        }
        catch (Exception ex)
        {
            Log.Error("update check", ex);
            if (manual) Status = "Couldn't reach GitHub. Check your connection and try again.";
        }
        finally { Busy = false; }
    }

    /// <summary>Download, verify, swap, restart. Without a download for this build it opens the release page instead.</summary>
    public async Task InstallAsync(bool quiet = false)
    {
        var info = _available;
        if (info == null || Busy) return;
        if (info.DownloadUrl == null) { OpenReleasePage(); return; }
        Busy = true;
        try
        {
            var progress = new Progress<double>(p => Status = $"Downloading v{info.Version.ToString(3)}… {p:0%}");
            string staged = await Updater.DownloadAsync(info, progress);
            Status = "Installing…";
            Updater.InstallAndRestart(staged, quiet);
            RestartRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("update install", ex);
            Status = ex is UnauthorizedAccessException or System.IO.IOException
                ? "Couldn't replace Fader.exe (is its folder read-only?). Download it from the release page instead."
                : $"Update failed: {ex.Message}";
            Busy = false;
        }
    }

    public void OpenReleasePage()
    {
        try { Process.Start(new ProcessStartInfo(_available?.PageUrl ?? Updater.ReleasesPage) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open release page", ex); }
    }
}
