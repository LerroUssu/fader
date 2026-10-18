using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using WF = System.Windows.Forms;
using static Otto.Native.NativeMethods;

namespace Otto.Tray;

/// <summary>Notification-area icon (WinForms NotifyIcon) with a WPF-styled context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly Icon _drawnIcon;
    private readonly Window _anchor;
    private readonly ContextMenu _menu;
    private readonly MenuItem _autoStartItem;
    private readonly MenuItem _updateItem;
    private readonly Separator _updateSeparator;

    public event Action? LeftClick;
    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action<bool>? AutoStartToggled;
    public event Action? UpdateRequested;
    public event Action? CheckUpdatesRequested;
    public event Action? BalloonClicked;
    public Func<bool> IsAutoStart { get; set; } = () => false;

    public TrayIcon()
    {
        _drawnIcon = DrawIcon();
        _icon = new WF.NotifyIcon { Text = "Otto — per-app volume", Icon = _drawnIcon, Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) LeftClick?.Invoke();
            else if (e.Button == WF.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();

        // Invisible 1×1 window: a WPF ContextMenu needs an owning window to take focus, otherwise it never closes.
        _anchor = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, Topmost = true,
            Opacity = 0, ResizeMode = ResizeMode.NoResize, ShowActivated = true
        };

        _menu = new ContextMenu
        {
            Style = (Style)Application.Current.FindResource("DarkContextMenu"),
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };
        _updateItem = AddItem("Update", () => UpdateRequested?.Invoke());
        _updateItem.FontWeight = FontWeights.SemiBold;
        _updateItem.Visibility = Visibility.Collapsed;
        _updateSeparator = new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator"), Visibility = Visibility.Collapsed };
        _menu.Items.Add(_updateSeparator);
        AddItem("Open mixer", () => OpenRequested?.Invoke());
        AddItem("Settings…", () => SettingsRequested?.Invoke());
        _menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator") });
        _autoStartItem = AddItem("Start with Windows", () =>
        {
            bool on = !_autoStartItem!.IsChecked;
            _autoStartItem.IsChecked = on;
            AutoStartToggled?.Invoke(on);
        });
        AddItem("Check for updates", () => CheckUpdatesRequested?.Invoke());
        _menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator") });
        AddItem("Quit Otto", () => QuitRequested?.Invoke());
        _menu.Closed += (_, _) => _anchor.Hide();
    }

    private MenuItem AddItem(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = (Style)Application.Current.FindResource("DarkMenuItem") };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return item;
    }

    private void ShowMenu()
    {
        try
        {
            _autoStartItem.IsChecked = IsAutoStart();
            GetCursorPos(out var pt);
            double scale = GetScaleForPoint(pt);
            _anchor.Left = pt.x / scale;
            _anchor.Top = pt.y / scale;
            _anchor.Show();
            _anchor.Activate();
            SetForegroundWindow(new WindowInteropHelper(_anchor).Handle);
            _menu.PlacementTarget = _anchor;
            _menu.IsOpen = true;
        }
        catch (Exception ex) { Log.Error("tray menu", ex); }
    }

    /// <summary>Shows "Update to vX" at the top of the menu; null hides it.</summary>
    public void SetUpdate(string? label)
    {
        _updateItem.Header = label ?? "Update";
        var v = label == null ? Visibility.Collapsed : Visibility.Visible;
        _updateItem.Visibility = v;
        _updateSeparator.Visibility = v;
        _icon.Text = label == null ? "Otto — per-app volume" : $"Otto — {label.ToLowerInvariant()} available";
    }

    public void Balloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.None); } catch { }
    }

    /// <summary>Green rounded square (Otto's cloak) with three faders, drawn at the system's small-icon size so it stays crisp per DPI.</summary>
    private static Icon DrawIcon()
    {
        int size = Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), size * 0.26f);
            using var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(0x46, 0xA8, 0x73), Color.FromArgb(0x24, 0x5E, 0x40));
            g.FillPath(bg, path);

            float[] xs = { 0.30f, 0.50f, 0.70f };
            float[] ys = { 0.62f, 0.40f, 0.72f };
            using var track = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1f, size * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var knob = new SolidBrush(Color.White);
            for (int i = 0; i < 3; i++)
            {
                g.DrawLine(track, xs[i] * size, size * 0.22f, xs[i] * size, size * 0.78f);
                float r = size * 0.11f;
                g.FillEllipse(knob, xs[i] * size - r, ys[i] * size - r, 2 * r, 2 * r);
            }
        }
        var h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        try { _icon.Visible = false; _icon.Dispose(); _drawnIcon.Dispose(); _anchor.Close(); } catch { }
    }
}
