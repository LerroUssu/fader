using Microsoft.Win32;

namespace Fader.Native;

/// <summary>"Start with Windows" through the per-user Run key. Only touched when the user toggles it.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Fader";

    private static string ExePath => Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Fader.exe");

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, $"\"{ExePath}\" --startup");
            else k.DeleteValue(ValueName, false);
            Log.Write($"[autostart] {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }
}
