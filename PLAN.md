# Fader — per-app volume for Windows, done right

A tray app that makes changing one application's volume as fast as changing the master
volume, and that tames apps that launch too loud.

## Why

Windows 10's taskbar speaker icon only exposes the master level. Per-app volume is buried
in Settings → Sound → App volume and device preferences (slow, ugly, no live meters).
Apps also remember their last level, so Chrome comes back at 100 % and a game at 20 %.

## What it does

1. **Mixer flyout** — left-click the tray icon (or press `Ctrl+Alt+V`): a dark, glassy,
   Refbox-style panel (black, Figtree, hairline borders, violet accent) lists every app
   currently holding an audio session on the default output device. Each row: app icon
   (click = mute), name, slider, live peak meter, percentage. Master volume at the top.
   Scrolling over a row nudges it. Click outside / Esc closes it.
2. **Alt + scroll on any window** — hold `Alt` and scroll the mouse wheel over any
   window (including borderless full-screen games) to change *that app's* volume. A small
   on-screen pill shows icon, name, level. The wheel event is swallowed so the app under
   the cursor doesn't zoom or scroll. Works over taskbar buttons too (matches the hovered
   button to its app); over an empty part of the taskbar it changes the master volume.
   Modifier is configurable (Alt, Ctrl+Alt, Shift+Alt, Ctrl+Shift).
3. **New-app rules** — the moment Windows creates an audio session for an app (that is
   when the app first initialises audio, before the first sound is audible), Fader:
   sets it to the **start level** (default 50 %) if the app was just launched (process
   younger than 60 s) or had disappeared from the mixer for 10+ minutes. Per-app rules
   override the default (e.g. Chrome starts at 40 %, Spotify is exempt). A "cap" (ceiling on
   the app's volume setting) existed until v0.2.1 and was removed at the user's request: apps
   do not raise their own session volume, so it never had anything to do.
4. **One-handed taskbar control** (added 2026-10-13): wheel over an app's taskbar button, no
   key held, changes its volume; hovering a button shows its level pill above the button (or
   above the taskbar thumbnail); wheel over the clock/tray/empty taskbar = master volume.
   Left-clicking the normal Windows speaker tray icon opens Fader's mixer (right-click stays
   Windows'). Implemented in the mouse hook with cached taskbar/speaker-icon rectangles
   (`TaskbarTracker`, UI Automation off the UI thread).
5. **Loudness guard — removed (2026-10-13).** A per-app leveler on the session peak meters was
   built twice. The real-session log showed why it can never work for Chrome: the session meter
   (pre-gain) reads ~0.15 for speech *and* for a loud YouTube video, so loud content is not
   distinguishable at that level of the stack. Only a system-wide APO (Windows "Loudness
   Equalization") sees the audio samples. Removed at the user's request.
6. **Window chrome** (2026-10-13): the mixer can be dragged (then it stays open) or pinned; the
   Settings window is a normal resizable window (the custom ScrollViewer template was missing
   `PART_ScrollContentPresenter`, so nothing scrolled). The acrylic "blur behind" option was
   removed: on Windows 10 the accent blur paints a square region behind the rounded panel.
6b. **Pill position** (v0.2.2): the OSD pill used to anchor to what was scrolled (window top, taskbar thumbnail, screen bottom) and jumped around; it now always sits at a fixed spot (top / centre / bottom of the cursor's monitor, setting, default bottom).
7. **Packaging**: `dist\portable\Fader.exe` is self-contained (runtime inside, compressed single
   file); a `portable` marker or `settings.json` next to the exe makes it keep its data there.
   Releases are copied to `M:\SOFTWARES\Fader`. `--quit` exits a running instance cleanly,
   `--autostart` turns on start-with-Windows (used to launch it from outside my sandbox via Explorer).
8. Settings window for all of the above, "start with Windows" toggle, single instance,
   runs silently in the tray.

### Why "taskbar right-click" is not an option
The taskbar jump-list menus belong to Explorer; third-party apps cannot add items to
them. The Alt+scroll gesture and the tray flyout replace that idea and also cover
full-screen apps.

## Tech

- C# / .NET 8, WPF (custom-drawn chrome, transparent rounded windows), WinForms only for
  the tray icon. Single `Fader.exe` (framework-dependent, uses the .NET 8 Desktop runtime
  already installed on this PC).
- Audio: Windows Core Audio (WASAPI session API) through NAudio.Wasapi —
  `IAudioSessionManager2` (session enumeration + `OnSessionCreated`),
  `ISimpleAudioVolume` (volume/mute), `IAudioMeterInformation` (peak meters),
  `IAudioSessionEvents` (volume/state callbacks), `IMMNotificationClient` (default device
  changes), `IAudioEndpointVolume` (master).
- Sessions are grouped per executable (Chrome's audio lives in a child `chrome.exe`;
  grouping by exe name maps a window under the cursor to the right session set).
- Input: `WH_MOUSE_LL` hook for the modifier+wheel gesture, `RegisterHotKey` for the
  flyout. UI Automation to identify hovered taskbar buttons.
- Icons: `IShellItemImageFactory` (same high-res icons Explorer uses), fallbacks to
  `ExtractIconEx` and the session's own icon path (system sounds).
- Settings: `%APPDATA%\Fader\settings.json`. Log: `%APPDATA%\Fader\fader.log`.

## Layout

```
fader/
  Fader.csproj, app.manifest, App.xaml(.cs)
  Audio/   AudioSessionService.cs, AppAudio.cs, SessionEntry.cs, ProcessInfo.cs
  Rules/   Settings.cs, RulesEngine.cs
  Native/  NativeMethods.cs, MouseWheelHook.cs, WindowResolver.cs, IconExtractor.cs,
           HotkeyManager.cs, WindowEffects.cs, AutoStart.cs
  UI/      Theme.xaml, FlyoutWindow.xaml(.cs), OsdWindow.xaml(.cs), SettingsWindow.xaml(.cs)
  Tray/    TrayIcon.cs
  Probe.cs (headless self-test: `Fader.exe --probe out.json`, opens no window, changes nothing)
  assets/  fonts (Figtree, OFL), fader.ico
```

## Build / run

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
→ bin\Release\net8.0-windows\win-x64\publish\Fader.exe
```

## Review notes (2026-10-12)

Four-lens code review (Core Audio, Win32, WPF, UX) found and led to these fixes: the mouse
hook now runs on its own thread (a stalled UI thread would have got it silently removed by
Windows); sub-notch wheel deltas from precision touchpads are accumulated into whole notches;
the tray click that closes the flyout no longer reopens it; the flyout re-anchors when rows
appear while it is open; Settings no longer leaks handlers per opening and saves are debounced;
a user-set level survives Chrome-style audio helper processes being recycled and default-device
switches; NAudio dispose failures can no longer crash the process from the finalizer thread;
mixed-DPI positioning uses the target monitor's scale. Deliberately not done: resetting an app
to the start level when it merely resumes after a long pause.

## Known limits

- Apps running as administrator do not receive the Alt+scroll gesture unless Fader itself
  runs elevated (Windows UIPI). The flyout still controls them.
- Exclusive full-screen (old DirectX) games hide every overlay; borderless is fine.
- Taskbar-button matching relies on button names; a window whose title doesn't contain
  the app name (Spotify shows the song title) is matched through its window title.
