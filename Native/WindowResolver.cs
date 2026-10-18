using System.IO;
using System.Windows.Automation;
using static Otto.Native.NativeMethods;

namespace Otto.Native;

public enum TargetKind { None, Self, Window, Taskbar }

/// <summary>What is under a screen point: a top-level window (with the owning process), the taskbar, or us.</summary>
public sealed record ResolvedTarget(TargetKind Kind, IntPtr Hwnd, uint Pid, string? ExePath, string? ExeName, string Title, string ClassName, string ChildClassName)
{
    public static readonly ResolvedTarget None = new(TargetKind.None, IntPtr.Zero, 0, null, null, string.Empty, string.Empty, string.Empty);
}

public static class WindowResolver
{
    private static readonly string[] TaskbarClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    public static ResolvedTarget Resolve(POINT pt, uint ourPid)
    {
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return ResolvedTarget.None;
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        var cls = GetClassNameOf(root);
        var childCls = hwnd == root ? cls : GetClassNameOf(hwnd);
        GetWindowThreadProcessId(root, out var pid);
        if (pid == ourPid) return new ResolvedTarget(TargetKind.Self, root, pid, null, null, string.Empty, cls, childCls);
        if (TaskbarClasses.Contains(cls)) return new ResolvedTarget(TargetKind.Taskbar, root, pid, null, null, string.Empty, cls, childCls);

        var path = GetProcessPath(pid);
        var name = path != null ? Path.GetFileName(path) : null;

        // UWP / packaged apps are hosted by ApplicationFrameHost; the real process owns the CoreWindow child.
        if (name != null && name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var core = FindChildByClass(root, "Windows.UI.Core.CoreWindow");
            if (core != IntPtr.Zero)
            {
                GetWindowThreadProcessId(core, out var corePid);
                if (corePid != 0 && corePid != pid)
                {
                    pid = corePid;
                    path = GetProcessPath(pid);
                    name = path != null ? Path.GetFileName(path) : null;
                }
            }
        }
        return new ResolvedTarget(TargetKind.Window, root, pid, path, name, GetWindowTitle(root), cls, childCls);
    }

    private static IntPtr FindChildByClass(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            if (GetClassNameOf(h) == className) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Name of the taskbar button under the point, via UI Automation. Slow-ish: call off the UI thread.</summary>
    public static string? TaskbarButtonNameAt(POINT pt)
    {
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.x, pt.y));
            var walker = TreeWalker.ControlViewWalker;
            int guard = 0;
            while (el != null && guard++ < 10)
            {
                var ct = el.Current.ControlType;
                if (ct == ControlType.Button || ct == ControlType.MenuItem || ct == ControlType.ListItem)
                {
                    var n = el.Current.Name;
                    return string.IsNullOrWhiteSpace(n) ? null : n;
                }
                if (TaskbarClasses.Contains(el.Current.ClassName)) break;
                el = walker.GetParent(el);
            }
        }
        catch (Exception ex) { Log.Error("uia", ex); }
        return null;
    }

    /// <summary>Visible top-level windows as (title, exe name): used to map a taskbar button name to an app.</summary>
    public static List<(string Title, string ExeName)> VisibleWindows()
    {
        var list = new List<(string, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = GetWindowTitle(h);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            var path = GetProcessPath(pid);
            if (path != null) list.Add((title, Path.GetFileName(path)));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
