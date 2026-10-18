using System.Runtime.InteropServices;
using static Otto.Native.NativeMethods;

namespace Otto.Native;

/// <summary>
/// Low-level mouse hook on its own STA thread with a minimal message loop (a busy UI thread can
/// never make the callback miss the LowLevelHooksTimeout, after which Windows silently removes
/// slow hooks). The callback only reads key state and cached rectangles, raises events and
/// returns; subscribers marshal to the UI themselves.
///
/// It intercepts: modifier + wheel anywhere; wheel over the taskbar (no modifier); mouse moves
/// over the taskbar (for the hover pill); left clicks on the Windows speaker tray icon.
/// </summary>
public sealed class MouseWheelHook : IDisposable
{
    private readonly LowLevelMouseProc _proc;   // keep the delegate alive for the lifetime of the hook
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hook;
    private uint _threadId;
    private volatile bool _disposed;
    private bool _wasOverTaskbar;
    private bool _swallowingSpeakerClick;
    private long _lastHoverTick;
    private RECT _flyoutRect;
    private volatile bool _hasFlyoutRect;

    /// <summary>Returns true when the current keyboard state matches the gesture modifier. Called on the hook thread.</summary>
    public Func<bool> ModifierHeld { get; set; } = () => false;
    public bool Enabled { get; set; } = true;
    public TaskbarTracker? Taskbar { get; set; }
    public bool TaskbarWheelEnabled { get; set; }
    public bool TaskbarHoverEnabled { get; set; }
    public bool SpeakerClickEnabled { get; set; }

    /// <summary>Raised on the hook thread: wheel delta (±120 per notch, less for precision touchpads) and the cursor position in screen pixels.</summary>
    public event Action<int, POINT>? WheelWithModifier;
    /// <summary>Wheel over the taskbar without any modifier.</summary>
    public event Action<int, POINT>? WheelOverTaskbar;
    /// <summary>Mouse moved over the taskbar (inside = true, throttled), or just left it (inside = false).</summary>
    public event Action<POINT, bool>? TaskbarHover;
    /// <summary>Left click on the Windows speaker icon (both button messages were swallowed).</summary>
    public event Action<POINT>? SpeakerClicked;
    /// <summary>Any mouse button went down outside the flyout while it was open.</summary>
    public event Action? ClickOutside;

    public void SetFlyoutRect(RECT? rect)
    {
        if (rect is RECT r) { _flyoutRect = r; _hasFlyoutRect = true; }
        else _hasFlyoutRect = false;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public MouseWheelHook()
    {
        _proc = Callback;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "Otto.MouseHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write($"[hook] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled && !_disposed)
        {
            try
            {
                int msg = (int)wParam;
                switch (msg)
                {
                    case WM_MOUSEWHEEL:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                        if (ModifierHeld())
                        {
                            WheelWithModifier?.Invoke(delta, info.pt);
                            return (IntPtr)1; // swallow: the app under the cursor must not zoom/scroll
                        }
                        if (TaskbarWheelEnabled && Taskbar != null && Taskbar.IsOverTaskbar(info.pt))
                        {
                            WheelOverTaskbar?.Invoke(delta, info.pt);
                            return (IntPtr)1;
                        }
                        break;
                    }
                    case WM_MOUSEMOVE:
                    {
                        if (!TaskbarHoverEnabled || Taskbar == null) break;
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        bool inside = Taskbar.IsOverTaskbar(info.pt);
                        long now = Environment.TickCount64;
                        if (inside && now - _lastHoverTick >= 40) { _lastHoverTick = now; TaskbarHover?.Invoke(info.pt, true); }
                        else if (!inside && _wasOverTaskbar) TaskbarHover?.Invoke(info.pt, false);
                        _wasOverTaskbar = inside;
                        break;
                    }
                    case WM_RBUTTONDOWN:
                    case WM_MBUTTONDOWN:
                    {
                        if (!_hasFlyoutRect) break;
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (!TaskbarTracker.Contains(_flyoutRect, info.pt)) ClickOutside?.Invoke();
                        break;
                    }
                    case WM_LBUTTONDOWN:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (_hasFlyoutRect && !TaskbarTracker.Contains(_flyoutRect, info.pt)) ClickOutside?.Invoke();
                        if (!SpeakerClickEnabled || Taskbar == null) break;
                        if (Taskbar.IsOverSpeaker(info.pt)) { _swallowingSpeakerClick = true; return (IntPtr)1; }
                        break;
                    }
                    case WM_LBUTTONUP:
                    {
                        if (!_swallowingSpeakerClick) break;
                        _swallowingSpeakerClick = false;
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        SpeakerClicked?.Invoke(info.pt);
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex) { Log.Error("mouse hook", ex); }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
