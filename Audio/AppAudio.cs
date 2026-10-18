using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Otto.Audio;

/// <summary>All audio sessions of one application (grouped by executable), as shown in one mixer row.</summary>
public sealed class AppAudio : ObservableObject
{
    private ImageSource? _icon;
    private double _volumePercent;
    private bool _muted;
    private double _peak;
    private bool _isActive;
    private bool _syncing;

    public string Key { get; }
    public string Name { get; }
    public string? ExePath { get; }
    public bool IsSystemSounds { get; }
    public List<SessionEntry> Sessions { get; } = new();

    /// <summary>Raised after the user (slider, gesture, hotkey) changed the volume — not for external changes.</summary>
    public event Action<AppAudio>? AdjustedByUser;

    public AppAudio(string key, string name, string? exePath, bool isSystemSounds)
    {
        Key = key; Name = name; ExePath = exePath; IsSystemSounds = isSystemSounds;
    }

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public double Peak { get => _peak; set => Set(ref _peak, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string Pids => string.Join(", ", Sessions.Select(s => s.Pid));

    /// <summary>0–100. Setting it (outside a sync) pushes the level to every session of the app.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var v = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _volumePercent, v)) return;
            if (_syncing) return;
            foreach (var s in Sessions) s.SetVolume((float)(v / 100.0));
            AdjustedByUser?.Invoke(this);
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            if (_syncing) return;
            foreach (var s in Sessions) s.SetMute(value);
            AdjustedByUser?.Invoke(this);
        }
    }

    public void Nudge(int percentDelta) => VolumePercent += percentDelta;

    /// <summary>Pull volume / mute / state from the sessions (UI thread, after COM events).</summary>
    public void SyncFromSessions()
    {
        if (Sessions.Count == 0) return;
        _syncing = true;
        try
        {
            VolumePercent = Sessions.Max(s => s.UserVolume) * 100.0;
            Muted = Sessions.All(s => s.Mute);
            IsActive = Sessions.Any(s => s.State == AudioSessionState.AudioSessionStateActive);
        }
        finally { _syncing = false; }
    }
}
