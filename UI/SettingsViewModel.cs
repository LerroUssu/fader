using System.Collections.ObjectModel;
using Fader.Audio;
using Fader.Native;
using Fader.Rules;

namespace Fader.UI;

/// <summary>Binding surface for the settings window. Every change writes through to <see cref="Settings"/> and triggers a save.</summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly Settings _s;
    private readonly Action _changed;
    private readonly System.ComponentModel.PropertyChangedEventHandler _ruleChanged;
    private int _lastStart;

    public ObservableCollection<AppRule> Rules { get; }
    public ObservableCollection<AppAudio> KnownApps { get; }

    public SettingsViewModel(Settings settings, AudioSessionService service, Action changed)
    {
        _s = settings;
        _changed = changed;
        _ruleChanged = (_, _) => _changed();
        _lastStart = settings.NewAppStartVolume ?? 50;
        KnownApps = service.Apps;
        Rules = new ObservableCollection<AppRule>(settings.Rules);
        foreach (var r in Rules) Hook(r);
        Rules.CollectionChanged += (_, _) => { _s.Rules = Rules.ToList(); _changed(); };
    }

    private void Hook(AppRule r) => r.PropertyChanged += _ruleChanged;

    /// <summary>AppRule objects are shared with Settings and outlive this window: detach so handlers do not pile up per opening.</summary>
    public void Dispose()
    {
        foreach (var r in Rules) r.PropertyChanged -= _ruleChanged;
    }

    // ---- gesture -------------------------------------------------------------------------

    public bool GestureEnabled { get => _s.ScrollGestureEnabled; set { _s.ScrollGestureEnabled = value; Raise(); _changed(); } }
    public bool OverTaskbar { get => _s.ScrollOverTaskbar; set { _s.ScrollOverTaskbar = value; Raise(); _changed(); } }
    public bool ShowOsd { get => _s.ShowOsd; set { _s.ShowOsd = value; Raise(); _changed(); } }
    public bool PosTop { get => _s.OsdPosition == OsdPosition.Top; set { if (value) SetPosition(OsdPosition.Top); } }
    public bool PosCenter { get => _s.OsdPosition == OsdPosition.Center; set { if (value) SetPosition(OsdPosition.Center); } }
    public bool PosBottom { get => _s.OsdPosition == OsdPosition.Bottom; set { if (value) SetPosition(OsdPosition.Bottom); } }

    private void SetPosition(OsdPosition pos)
    {
        _s.OsdPosition = pos;
        Raise(nameof(PosTop)); Raise(nameof(PosCenter)); Raise(nameof(PosBottom));
        _changed();
    }
    public bool TaskbarScroll { get => _s.TaskbarScroll; set { _s.TaskbarScroll = value; Raise(); _changed(); } }
    public bool TaskbarHoverPill { get => _s.TaskbarHoverPill; set { _s.TaskbarHoverPill = value; Raise(); _changed(); } }
    public bool SpeakerIconOpensFader { get => _s.SpeakerIconOpensFader; set { _s.SpeakerIconOpensFader = value; Raise(); _changed(); } }

    public bool ModAlt { get => _s.ScrollModifier == ScrollModifier.Alt; set { if (value) SetModifier(ScrollModifier.Alt); } }
    public bool ModCtrlAlt { get => _s.ScrollModifier == ScrollModifier.CtrlAlt; set { if (value) SetModifier(ScrollModifier.CtrlAlt); } }
    public bool ModShiftAlt { get => _s.ScrollModifier == ScrollModifier.ShiftAlt; set { if (value) SetModifier(ScrollModifier.ShiftAlt); } }
    public bool ModCtrlShift { get => _s.ScrollModifier == ScrollModifier.CtrlShift; set { if (value) SetModifier(ScrollModifier.CtrlShift); } }

    private void SetModifier(ScrollModifier m)
    {
        _s.ScrollModifier = m;
        Raise(nameof(ModAlt)); Raise(nameof(ModCtrlAlt)); Raise(nameof(ModShiftAlt)); Raise(nameof(ModCtrlShift));
        _changed();
    }

    public bool Step1 { get => _s.ScrollStep == 1; set { if (value) SetStep(1); } }
    public bool Step2 { get => _s.ScrollStep == 2; set { if (value) SetStep(2); } }
    public bool Step5 { get => _s.ScrollStep == 5; set { if (value) SetStep(5); } }

    private void SetStep(int step)
    {
        _s.ScrollStep = step;
        Raise(nameof(Step1)); Raise(nameof(Step2)); Raise(nameof(Step5));
        _changed();
    }

    // ---- mixer ---------------------------------------------------------------------------

    public string Hotkey { get => _s.Hotkey; set { _s.Hotkey = value; Raise(); _changed(); } }
    public bool AutoStartEnabled { get => AutoStart.IsEnabled(); set { AutoStart.Set(value); Raise(); } }

    // ---- new apps ------------------------------------------------------------------------

    public bool StartEnabled
    {
        get => _s.NewAppStartVolume != null;
        set { _s.NewAppStartVolume = value ? _lastStart : null; Raise(); Raise(nameof(StartVolume)); _changed(); }
    }

    public double StartVolume
    {
        get => _s.NewAppStartVolume ?? _lastStart;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (v == _lastStart) return;
            _lastStart = v;
            if (_s.NewAppStartVolume != null) _s.NewAppStartVolume = v;
            Raise(); _changed();
        }
    }

    // ---- per-app rules -------------------------------------------------------------------

    public void AddRule(AppAudio app)
    {
        if (app.IsSystemSounds) return;
        if (Rules.Any(r => r.App.Equals(app.Key, StringComparison.OrdinalIgnoreCase))) return;
        var rule = new AppRule { App = app.Key, Name = app.Name, StartVolume = _s.NewAppStartVolume };
        Hook(rule);
        Rules.Add(rule);
    }

    public void RemoveRule(AppRule rule)
    {
        rule.PropertyChanged -= _ruleChanged;
        Rules.Remove(rule);
    }
}
