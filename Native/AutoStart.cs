using Microsoft.Win32;

namespace Otto.Native;

/// <summary>"Start with Windows" through the per-user Run key. Only touched when the user toggles it.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Otto";
    private const string LegacyValueName = "Fader";

    private static string ExePath => Updater.ExePath;

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The app was called Fader until 0.4.0: carry its Run entry over to the new name and path.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(LegacyValueName) is not string) return;
            k.DeleteValue(LegacyValueName, false);
            k.SetValue(ValueName, $"\"{ExePath}\" --startup");
            Log.Write("[autostart] migrated the Fader entry to Otto");
        }
        catch (Exception ex) { Log.Error("autostart migrate", ex); }
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
