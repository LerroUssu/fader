using System.Windows.Input;
using System.Windows.Interop;
using static Otto.Native.NativeMethods;

namespace Otto.Native;

/// <summary>Registers one global hotkey on a hidden message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0xFA0E;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("OttoHotkeyWindow")
        {
            Width = 0, Height = 0, WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE: message-only window
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Parses strings like "Ctrl+Alt+V", "Shift+F9", "Win+Space". Returns false if it cannot be registered.</summary>
    public bool Register(string? hotkey)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        if (!TryParse(hotkey, out var mods, out var vk)) { Log.Write($"[hotkey] cannot parse '{hotkey}'"); return false; }
        _registered = RegisterHotKey(_source.Handle, HotkeyId, mods | MOD_NOREPEAT, vk);
        if (!_registered) Log.Write($"[hotkey] RegisterHotKey failed for '{hotkey}' (already taken by another app?)");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) { UnregisterHotKey(_source.Handle, HotkeyId); _registered = false; }
    }

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default: return false;
            }
        }
        var keyName = parts[^1];
        if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName;
        if (!Enum.TryParse<Key>(keyName, true, out var key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId) { Pressed?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose() { Unregister(); _source.Dispose(); }
}
