using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Fader.Audio;

/// <summary>One WASAPI audio session (one process ↔ one stream group). Several of these make up an <see cref="AppAudio"/>.</summary>
public sealed class SessionEntry : IDisposable
{
    private readonly Handler _handler;
    private readonly Queue<(float value, long tick)> _pending = new();

    public AudioSessionControl Control { get; }
    public uint Pid { get; }
    public string InstanceId { get; }
    public ProcessInfo Process { get; }
    public bool IsSystemSounds { get; }
    public string Key { get; }
    public DateTime Created { get; } = DateTime.UtcNow;

    /// <summary>Gain currently applied in Windows (what the Windows mixer shows).</summary>
    public float Volume { get; internal set; }
    /// <summary>Gain the user wants (the slider). Equal to <see cref="Volume"/>; kept separate so external changes are recognised as new intent.</summary>
    public float UserVolume { get; internal set; }
    public bool Mute { get; internal set; }
    public AudioSessionState State { get; internal set; }

    public SessionEntry(AudioSessionControl control, uint pid, string instanceId, ProcessInfo process, bool isSystemSounds, string key, AudioSessionService service)
    {
        Control = control; Pid = pid; InstanceId = instanceId; Process = process; IsSystemSounds = isSystemSounds; Key = key;
        try { Volume = control.SimpleAudioVolume.Volume; Mute = control.SimpleAudioVolume.Mute; } catch (Exception ex) { Log.Error("read session volume", ex); }
        UserVolume = Volume;
        try { State = control.State; } catch { State = AudioSessionState.AudioSessionStateInactive; }
        _handler = new Handler(this, service);
        try { control.RegisterEventClient(_handler); } catch (Exception ex) { Log.Error("register session events", ex); }
    }

    /// <summary>Peak of the session's output since the last read, 0..1 (Windows measures it before the session gain).</summary>
    public float Peak
    {
        get { try { return Control.AudioMeterInformation?.MasterPeakValue ?? 0f; } catch { return 0f; } }
    }

    public void SetVolume(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        try { Control.SimpleAudioVolume.Volume = v; Expect(v); Volume = v; UserVolume = v; }
        catch (Exception ex) { Log.Error($"set volume pid {Pid}", ex); }
    }

    public void SetMute(bool m)
    {
        try { Control.SimpleAudioVolume.Mute = m; Expect(Volume); Mute = m; }
        catch (Exception ex) { Log.Error($"set mute pid {Pid}", ex); }
    }

    private void Expect(float v)
    {
        lock (_pending)
        {
            _pending.Enqueue((v, Environment.TickCount64));
            while (_pending.Count > 32) _pending.Dequeue();
        }
    }

    /// <summary>True when a volume reported by Windows is the echo of a value Fader itself set recently.</summary>
    public bool IsOurEcho(float reported)
    {
        lock (_pending)
        {
            long now = Environment.TickCount64;
            bool found = false;
            int n = _pending.Count;
            for (int i = 0; i < n; i++)
            {
                var p = _pending.Dequeue();
                if (!found && Math.Abs(p.value - reported) < 0.0005f) { found = true; continue; }
                if (now - p.tick < 2000) _pending.Enqueue(p);
            }
            return found;
        }
    }

    public void Dispose()
    {
        try { Control.Dispose(); }
        catch (Exception ex)
        {
            // NAudio's finalizer would call Dispose() again on the finalizer thread and crash the process.
            GC.SuppressFinalize(Control);
            Log.Write($"[session] unregister failed for pid {Pid}: {ex.Message}");
        }
    }

    /// <summary>COM callbacks arrive on an RPC thread; the service marshals them to the UI thread.</summary>
    private sealed class Handler : IAudioSessionEventsHandler
    {
        private readonly SessionEntry _entry;
        private readonly AudioSessionService _service;
        public Handler(SessionEntry entry, AudioSessionService service) { _entry = entry; _service = service; }

        public void OnVolumeChanged(float volume, bool isMuted) => _service.SessionVolumeChangedFromCom(_entry, volume, isMuted);
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
        public void OnStateChanged(AudioSessionState state) => _service.SessionStateChangedFromCom(_entry, state);
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => _service.SessionDisconnectedFromCom(_entry);
    }
}
