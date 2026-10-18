using System.Diagnostics;
using System.Globalization;
using System.IO;
using static Otto.Native.NativeMethods;

namespace Otto.Audio;

/// <summary>What we know about the process behind an audio session.</summary>
public sealed record ProcessInfo(uint Pid, string? ExePath, string? ExeName, string? Description, DateTime? StartTime, bool Alive)
{
    public static ProcessInfo Get(uint pid)
    {
        var path = GetProcessPath(pid);
        string? name = path != null ? Path.GetFileName(path) : null;
        string? desc = null;
        DateTime? start = null;
        bool alive = true;

        try
        {
            using var p = Process.GetProcessById((int)pid);
            try { start = p.StartTime; } catch { /* access denied for protected processes */ }
            if (name == null) { try { name = p.ProcessName + ".exe"; } catch { } }
        }
        catch (ArgumentException) { alive = false; }
        catch { /* ignore */ }

        if (path != null)
        {
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
        }
        if (string.IsNullOrWhiteSpace(desc)) desc = null;
        return new ProcessInfo(pid, path, name, desc, start, alive);
    }

    public static bool IsAlive(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>"chrome.exe" → "Chrome", "my_game-x64.exe" → "My Game X64".</summary>
    public static string PrettyName(string? exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return "Unknown app";
        var stem = Path.GetFileNameWithoutExtension(exeName).Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem);
    }
}
