using Otto.Audio;

namespace Otto.Rules;

/// <summary>
/// The "new app" behaviour: when Windows creates an audio session for an app that was just
/// launched (or came back after being gone for a while), set it to the configured start level.
/// </summary>
public sealed class RulesEngine
{
    private readonly AudioSessionService _service;
    private readonly Func<Settings> _settings;

    public event Action<AppAudio, string>? Applied;

    public RulesEngine(AudioSessionService service, Func<Settings> settings)
    {
        _service = service;
        _settings = settings;
        service.SessionAdded += OnSessionAdded;
    }

    private void OnSessionAdded(AppAudio app, SessionEntry entry, bool initial)
    {
        if (app.IsSystemSounds || initial) return; // sessions that existed when Otto started are left alone
        var settings = _settings();
        if (settings.EffectiveStart(app.Key) is not int start) return;
        if (!IsFreshLaunch(app, entry, settings)) return;

        entry.SetVolume(start / 100f);
        app.SyncFromSessions();
        Log.Write($"[rules] {app.Name}: start {start}%");
        Applied?.Invoke(app, $"start {start}%");
    }

    /// <summary>
    /// "Fresh" = the app just started (process younger than FreshProcessSeconds), or it has no other
    /// live session and has not been in the mixer for longer than the cooldown. A level the user set by
    /// hand during this run survives session re-creation unless the process itself is new.
    /// </summary>
    private bool IsFreshLaunch(AppAudio app, SessionEntry entry, Settings s)
    {
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, s.CooldownMinutes));
        bool recentlyGone = _service.WasRecentlyGone(app.Key, cooldown);
        // Chrome/Electron/Steam host audio in a helper process that exits when idle and respawns on the next sound:
        // a young process is not a new app when the user set this app's level and it was audible moments ago.
        if (_service.UserAdjusted(app.Key) && recentlyGone) return false;
        if (entry.Process.StartTime is DateTime started && DateTime.Now - started < TimeSpan.FromSeconds(Math.Max(5, s.FreshProcessSeconds))) return true;
        if (DateTime.UtcNow - _service.LastBindUtc < TimeSpan.FromSeconds(10)) return false; // streams migrating to a new default device
        if (app.Sessions.Count > 1) return false; // the app already had a live session: leave it
        if (_service.UserAdjusted(app.Key)) return false;
        if (recentlyGone) return false;
        return true;
    }
}
