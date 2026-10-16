using System.Windows.Threading;
using Fader.Audio;
using Fader.Native;
using Fader.Rules;
using Fader.UI;
using static Fader.Native.NativeMethods;

namespace Fader;

/// <summary>
/// Everything driven by the mouse hook:
///  - modifier + wheel over any window → that app's volume (OSD pill on the window);
///  - wheel over a taskbar button, no modifier → that app's volume; over the tray/clock/empty taskbar → master;
///  - hovering a taskbar button → its level pill above the button (or above the taskbar thumbnail);
///  - left click on the Windows speaker icon → Fader's flyout; any click outside the flyout → closes it.
/// </summary>
public sealed class GestureController : IDisposable
{
    private readonly AudioSessionService _service;
    private readonly Func<Settings> _settings;
    private readonly OsdWindow _osd;
    private readonly Dispatcher _ui;
    private readonly TaskbarTracker _taskbar;
    private readonly MouseWheelHook _hook;
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private int _wheelAccum;
    private long _lastWheelTicks;

    private bool _taskbarLookupBusy;
    private int _taskbarPendingSteps;

    private readonly DispatcherTimer _dwell;
    private readonly DispatcherTimer _hoverRefresh;
    private POINT _hoverPt;
    private RECT? _hoverButtonRect;
    private AppAudio? _hoverApp;
    private bool _hoverResolving;
    private bool _pillSticky;

    public event Action<POINT>? SpeakerClicked;
    public event Action? ClickedOutsideFlyout;

    public bool HookInstalled => _hook.IsInstalled;

    public GestureController(AudioSessionService service, Func<Settings> settings, OsdWindow osd, Dispatcher ui, TaskbarTracker taskbar)
    {
        _service = service; _settings = settings; _osd = osd; _ui = ui; _taskbar = taskbar;
        _hook = new MouseWheelHook
        {
            ModifierHeld = () => _settings().ScrollGestureEnabled && _settings().ModifierHeld(),
            Taskbar = taskbar,
        };
        ApplySettings();
        _hook.WheelWithModifier += OnWheel;
        _hook.WheelOverTaskbar += OnWheelTaskbar;
        _hook.TaskbarHover += (pt, inside) => _ui.BeginInvoke(DispatcherPriority.Normal, () => HoverUi(pt, inside));
        _hook.SpeakerClicked += pt => _ui.BeginInvoke(() => SpeakerClicked?.Invoke(pt));
        _hook.ClickOutside += () => _ui.BeginInvoke(() => ClickedOutsideFlyout?.Invoke());

        _dwell = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(300) };
        _dwell.Tick += (_, _) => { _dwell.Stop(); ResolveHover(_hoverPt); };
        _hoverRefresh = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverRefresh.Tick += (_, _) => RefreshPill();
    }

    public void ApplySettings()
    {
        var s = _settings();
        _hook.TaskbarWheelEnabled = s.TaskbarScroll;
        _hook.TaskbarHoverEnabled = s.TaskbarHoverPill || s.TaskbarScroll;
        _hook.SpeakerClickEnabled = s.SpeakerIconOpensFader;
    }

    /// <summary>Screen rectangle of the open flyout (null when closed): clicks outside it close the flyout.</summary>
    public void SetFlyoutRect(RECT? rect) => _hook.SetFlyoutRect(rect);

    // ---- wheel (hook thread) -------------------------------------------------------------

    /// <summary>Whole notches only; precision touchpads deliver many small deltas.</summary>
    private int Accumulate(int delta)
    {
        long now = Environment.TickCount64;
        if (now - _lastWheelTicks > 400 || Math.Sign(delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _lastWheelTicks = now;
        _wheelAccum += delta;
        int notches = _wheelAccum / 120;
        _wheelAccum -= notches * 120;
        return Math.Clamp(notches, -5, 5);
    }

    private void OnWheel(int delta, POINT pt)
    {
        int notches = Accumulate(delta);
        if (notches == 0) { _ui.BeginInvoke(DispatcherPriority.Input, CancelAltMenu); return; }
        _ui.BeginInvoke(DispatcherPriority.Input, () => Handle(notches, pt));
    }

    private void OnWheelTaskbar(int delta, POINT pt)
    {
        int notches = Accumulate(delta);
        if (notches == 0) return;
        _ui.BeginInvoke(DispatcherPriority.Input, () => HandleTaskbar(notches * _settings().ScrollStep, pt));
    }

    /// <summary>A bare Alt press would focus the app's menu bar on release; an unassigned key in between cancels that.</summary>
    private void CancelAltMenu()
    {
        if (_settings().ModifierUsesAlt) SendCancelAltMenuKey();
    }

    // ---- modifier + wheel over anything (UI thread) --------------------------------------

    private void Handle(int notches, POINT pt)
    {
        try
        {
            CancelAltMenu();
            int steps = notches * _settings().ScrollStep;
            var target = WindowResolver.Resolve(pt, _ownPid);
            switch (target.Kind)
            {
                case TargetKind.None:
                case TargetKind.Self:
                    return;
                case TargetKind.Taskbar:
                    HandleTaskbar(steps, pt);
                    return;
                case TargetKind.Window:
                    if (target.ClassName is "Progman" or "WorkerW") { AdjustMaster(steps); return; } // the desktop itself
                    var app = _service.FindByExe(target.ExeName);
                    if (app != null) { AdjustApp(app, steps, target.Hwnd); return; }
                    if (_settings().ShowOsd) ShowNoAudio(target);
                    return;
            }
        }
        catch (Exception ex) { Log.Error("gesture", ex); }
    }

    /// <summary>Version-info and shell-icon lookups can take 100 ms cold: keep them off the UI thread.</summary>
    private void ShowNoAudio(ResolvedTarget target)
    {
        Task.Run(() =>
        {
            var name = ProcessInfo.Get(target.Pid).Description ?? ProcessInfo.PrettyName(target.ExeName);
            return (name, icon: IconExtractor.ForApp(null, target.ExePath));
        }).ContinueWith(t =>
        {
            if (_service.FindByExe(target.ExeName) != null) return; // it started playing meanwhile
            _osd.ShowMessage(t.Result.name, t.Result.icon, "no audio right now", target.Hwnd);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AdjustApp(AppAudio app, int steps, IntPtr anchorWindow)
    {
        app.Nudge(steps);
        if (_settings().ShowOsd) _osd.ShowLevel(app.Name, app.Icon, app.VolumePercent, app.Muted, anchorWindow);
    }

    private void AdjustAppAt(AppAudio app, int steps, RECT anchor)
    {
        app.Nudge(steps);
        if (_settings().ShowOsd) _osd.ShowLevelAt(app.Name, app.Icon, app.VolumePercent, app.Muted, anchor, _pillSticky);
    }

    private void AdjustMaster(int steps)
    {
        _service.Master.Nudge(steps);
        if (_settings().ShowOsd) _osd.ShowLevel(_service.Master.DeviceName, null, _service.Master.VolumePercent, _service.Master.Muted, IntPtr.Zero);
    }

    // ---- taskbar (UI thread) -------------------------------------------------------------

    private void HandleTaskbar(int steps, POINT pt)
    {
        try
        {
            var target = WindowResolver.Resolve(pt, _ownPid);
            switch (target.ChildClassName)
            {
                case "Start":
                case "TrayDummySearchControl":
                case "TrayButton":
                case "TrayShowDesktopButtonWClass":
                    return;
                case "MSTaskListWClass":
                    TaskbarButtonGesture(steps, pt);
                    return;
                default: // tray icons, clock, empty taskbar
                    AdjustMaster(steps);
                    return;
            }
        }
        catch (Exception ex) { Log.Error("taskbar gesture", ex); }
    }

    /// <summary>UI Automation is slow (tens of ms): resolve the hovered taskbar button off-thread, coalescing wheel notches meanwhile.</summary>
    private void TaskbarButtonGesture(int steps, POINT pt)
    {
        if (_hoverApp != null && _hoverButtonRect is RECT cached && TaskbarTracker.Contains(cached, pt))
        {
            AdjustAppAt(_hoverApp, steps, PillAnchor(cached));
            return;
        }
        _taskbarPendingSteps += steps;
        if (_taskbarLookupBusy) return;
        _taskbarLookupBusy = true;
        Task.Run(() => LookupButton(pt)).ContinueWith(t =>
        {
            _taskbarLookupBusy = false;
            int pending = _taskbarPendingSteps;
            _taskbarPendingSteps = 0;
            var (button, app) = t.Result;
            if (button != null) { _hoverButtonRect = button.Value.Rect; _hoverApp = app; }
            if (pending == 0) return;
            if (app != null && button != null) AdjustAppAt(app, pending, PillAnchor(button.Value.Rect));
            else if (button != null) { if (_settings().ShowOsd) _osd.ShowMessageAt(button.Value.Name, null, "no audio right now", PillAnchor(button.Value.Rect)); }
            else AdjustMaster(pending); // empty part of the task band
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private ((string Name, RECT Rect)? Button, AppAudio? App) LookupButton(POINT pt)
    {
        var button = TaskbarTracker.ButtonAt(pt);
        if (button == null) return (null, null);
        var windows = WindowResolver.VisibleWindows();
        AppAudio? app = null;
        _ui.Invoke(() => app = _service.FindByTaskbarButton(button.Value.Name, windows)); // Apps lives on the UI thread
        return (button, app);
    }

    /// <summary>The pill goes above the taskbar thumbnail preview when one is showing for this button, else above the button.</summary>
    private static RECT PillAnchor(RECT button)
    {
        var thumb = TaskbarTracker.ThumbnailRect();
        if (thumb is RECT t && t.right > button.left - 240 && t.left < button.right + 240) return t;
        return button;
    }

    // ---- hover pill ----------------------------------------------------------------------

    private void HoverUi(POINT pt, bool inside)
    {
        try
        {
            if (!inside) { EndHover(); return; }
            _hoverPt = pt;
            if (_hoverButtonRect is RECT r && TaskbarTracker.Contains(r, pt)) return; // same button: pill stays
            EndHover();
            if (!_settings().TaskbarHoverPill) return;
            var target = WindowResolver.Resolve(pt, _ownPid);
            if (target.ChildClassName != "MSTaskListWClass") return;
            _dwell.Stop();
            _dwell.Start();
        }
        catch (Exception ex) { Log.Error("hover", ex); }
    }

    private void EndHover()
    {
        _dwell.Stop();
        _hoverRefresh.Stop();
        _hoverApp = null;
        _hoverButtonRect = null;
        if (_pillSticky) { _pillSticky = false; _osd.Release(); }
    }

    private void ResolveHover(POINT pt)
    {
        if (_hoverResolving) return;
        _hoverResolving = true;
        Task.Run(() => LookupButton(pt)).ContinueWith(t =>
        {
            _hoverResolving = false;
            var (button, app) = t.Result;
            if (button == null) return;
            GetCursorPos(out var now);
            if (!TaskbarTracker.Contains(button.Value.Rect, now)) return; // moved on meanwhile
            _hoverButtonRect = button.Value.Rect;
            _hoverApp = app;
            if (app != null && _settings().ShowOsd)
            {
                _pillSticky = true;
                _osd.ShowLevelAt(app.Name, app.Icon, app.VolumePercent, app.Muted, PillAnchor(button.Value.Rect), sticky: true);
                _hoverRefresh.Start();
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>While hovering: keep the pill's level current and move it above the thumbnail once that appears.</summary>
    private void RefreshPill()
    {
        if (!_pillSticky || _hoverApp == null || _hoverButtonRect is not RECT r) { _hoverRefresh.Stop(); return; }
        GetCursorPos(out var p);
        if (!_taskbar.IsOverTaskbar(p)) { EndHover(); return; }
        _osd.ShowLevelAt(_hoverApp.Name, _hoverApp.Icon, _hoverApp.VolumePercent, _hoverApp.Muted, PillAnchor(r), sticky: true);
    }

    public void Dispose()
    {
        _dwell.Stop();
        _hoverRefresh.Stop();
        _hook.WheelWithModifier -= OnWheel;
        _hook.WheelOverTaskbar -= OnWheelTaskbar;
        _hook.Dispose();
    }
}
