using System.IO;

namespace Otto;

/// <summary>
/// Append-only log next to the settings. Data lives in %APPDATA%\Otto, or next to Otto.exe when a
/// "settings.json" or "portable" file sits there (USB stick / copy-anywhere use). Never throws.
/// </summary>
public static class Log
{
    public static readonly string Dir = ResolveDataDir();
    public static readonly string File = Path.Combine(Dir, "otto.log");
    public static readonly string FallbackFile = Path.Combine(Path.GetTempPath(), "otto.log");
    public static bool IsPortable { get; private set; }

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _writerPath;

    /// <summary>Last failure while opening or writing the log, for the probe.</summary>
    public static string? LastError { get; private set; }
    public static string? ActivePath => _writerPath;

    private static string ResolveDataDir()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir != null && (System.IO.File.Exists(Path.Combine(exeDir, "settings.json")) || System.IO.File.Exists(Path.Combine(exeDir, "portable"))))
            {
                IsPortable = true;
                return exeDir;
            }
        }
        catch { }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Otto");
        // Renamed from Fader in 0.4.0: bring the old settings along once.
        try
        {
            var legacy = Path.Combine(appData, "Fader", "settings.json");
            var target = Path.Combine(dir, "settings.json");
            if (!System.IO.File.Exists(target) && System.IO.File.Exists(legacy))
            {
                Directory.CreateDirectory(dir);
                System.IO.File.Copy(legacy, target);
            }
        }
        catch { }
        return dir;
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            if (!TryWrite(File, line) && !TryWrite(FallbackFile, line)) { /* nothing more we can do */ }
        }
    }

    private static bool TryWrite(string path, string line)
    {
        try
        {
            if (_writer == null || _writerPath != path)
            {
                _writer?.Dispose();
                _writer = null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: > 2_000_000 }) System.IO.File.Delete(path);
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                _writerPath = path;
            }
            _writer.WriteLine(line);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{path}: {ex.GetType().Name}: {ex.Message}";
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _writerPath = null;
            return false;
        }
    }

    public static void Error(string context, Exception ex) => Write($"[error] {context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
