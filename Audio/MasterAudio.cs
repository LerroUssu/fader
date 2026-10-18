using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace Otto.Audio;

/// <summary>Master volume of the default render device.</summary>
public sealed class MasterAudio : ObservableObject
{
    private readonly Dispatcher _ui;
    private AudioEndpointVolume? _endpoint;
    private AudioMeterInformation? _meter;
    private AudioEndpointVolumeNotificationDelegate? _handler;
    private double _volumePercent;
    private bool _muted;
    private double _peak;
    private string _deviceName = "No output device";
    private bool _syncing;

    public MasterAudio(Dispatcher ui) { _ui = ui; }

    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public double Peak { get => _peak; set => Set(ref _peak, value); }
    public bool IsBound => _endpoint != null;

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var v = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _volumePercent, v) || _syncing || _endpoint == null) return;
            try { _endpoint.MasterVolumeLevelScalar = (float)(v / 100.0); } catch (Exception ex) { Log.Error("master volume", ex); }
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value) || _syncing || _endpoint == null) return;
            try { _endpoint.Mute = value; } catch (Exception ex) { Log.Error("master mute", ex); }
        }
    }

    public void Nudge(int percentDelta) => VolumePercent += percentDelta;

    internal void Bind(MMDevice device)
    {
        Unbind();
        try
        {
            _endpoint = device.AudioEndpointVolume;
            _meter = device.AudioMeterInformation;
            DeviceName = device.FriendlyName;
            _handler = data => _ui.BeginInvoke(() => Sync(data.MasterVolume, data.Muted));
            _endpoint.OnVolumeNotification += _handler;
            Sync(_endpoint.MasterVolumeLevelScalar, _endpoint.Mute);
        }
        catch (Exception ex) { Log.Error("bind master", ex); _endpoint = null; }
    }

    internal void Unbind()
    {
        if (_endpoint != null && _handler != null) { try { _endpoint.OnVolumeNotification -= _handler; } catch { } }
        _endpoint = null; _meter = null; _handler = null;
        DeviceName = "No output device";
    }

    internal float ReadPeak()
    {
        try { return _meter?.MasterPeakValue ?? 0f; } catch { return 0f; }
    }

    private void Sync(float volume, bool muted)
    {
        _syncing = true;
        try { VolumePercent = volume * 100.0; Muted = muted; }
        finally { _syncing = false; }
    }
}
