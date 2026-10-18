using System.Windows;
using System.Windows.Interop;
using static Otto.Native.NativeMethods;

namespace Otto.Native;

/// <summary>Extended window styles for the pop-up windows.</summary>
public static class WindowEffects
{
    private static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Hide from Alt+Tab; optionally never take focus and let clicks pass through (OSD).</summary>
    public static void MakeToolWindow(Window w, bool noActivate)
    {
        var h = Handle(w);
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE | WS_EX_TRANSPARENT; // never focused, clicks and WindowFromPoint pass through
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    }
}
