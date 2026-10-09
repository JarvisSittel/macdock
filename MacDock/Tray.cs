using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using IOPath = System.IO.Path;
using Shapes = System.Windows.Shapes;

namespace MacDock;

/// <summary>Notification-area icons. Uses the same "show in taskbar" choices as Windows Settings, plus overrides.</summary>
public class TrayItem : FixedItem
{
    static readonly HashSet<Guid> SystemIcons = new[] { NotificationArea.VOLUME_GUID, NotificationArea.NETWORK_GUID, NotificationArea.POWER_GUID }
        .Select(Guid.Parse).ToHashSet();

    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _chevron;
    readonly WrapPanel _overflowPanel = new() { MaxWidth = 6 * 30 };
    readonly Popup _overflow;
    readonly DispatcherTimer _refresh;
    readonly FrameworkElement _chevronGlyph = Glyphs.Box(14, Glyphs.Stroke(Glyphs.Chevron, 2.2, 0.85));
    string _signature;
    double _k = 1;

    NotificationArea Area => App.Shell.NotificationArea;

    public TrayItem(DockWindow dock) : base(dock)
    {
        _chevron = Ui.Chip(_chevronGlyph, new Thickness(5, 7, 5, 7));
        _chevron.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) e.Handled = true; };
        _chevron.MouseLeftButtonUp += (_, e) => { ToggleOverflow(); e.Handled = true; };
        _chevron.MouseEnter += (_, _) => Dock.SetCustomLabel("Show hidden icons", _chevron);
        _chevron.MouseLeave += (_, _) => Dock.ClearCustomLabel(_chevron);

        _overflow = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true, // the dock closes it on outside clicks; see DockWindow.RegisterPopup
            PopupAnimation = PopupAnimation.Fade,
            Placement = PlacementMode.Custom,
            PlacementTarget = _chevron,
            CustomPopupPlacementCallback = Ui.Above,
            Child = new Border { Style = (Style)Application.Current.FindResource("DockPanelBorder"), Padding = new Thickness(6), Child = _overflowPanel },
        };
        Dock.RegisterPopup(_overflow, _chevron);

        SetContent(_row);

        if (Area?.TrayIcons != null)
            Area.TrayIcons.CollectionChanged += (_, _) => Dock.Dispatcher.BeginInvoke(DispatcherPriority.Background, Rebuild);
        // Also picks up changes made in Windows Settings > Taskbar > Other system tray icons.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refresh.Tick += (_, _) => Rebuild();
        _refresh.Start();
        Rebuild();
    }

    public override void ApplyScale(double k)
    {
        if (k == _k) return;
        _k = k;
        _chevronGlyph.Width = _chevronGlyph.Height = 14 * k;
        _chevron.Padding = new Thickness(5 * k, 7 * k, 5 * k, 7 * k);
        _signature = null; // re-create the icon chips at the new size
        Rebuild();
    }

    void ToggleOverflow() => _overflow.IsOpen = !_overflow.IsOpen;

    void Rebuild()
    {
        if (Area?.TrayIcons == null) return;
        var all = Area.TrayIcons.Where(i => i.Icon != null && !i.IsHidden && !SystemIcons.Contains(i.GUID)).ToList();
        var promoted = PromotedExes();
        var shown = all.Where(i => IsShown(i, promoted)).ToList();
        var hidden = all.Except(shown).ToList();

        string sig = string.Join("|", shown.Select(Id)) + "#" + string.Join("|", hidden.Select(Id));
        if (sig == _signature) return;
        _signature = sig;

        _row.Children.Clear();
        if (hidden.Count > 0) _row.Children.Add(_chevron);
        foreach (var icon in shown) _row.Children.Add(MakeIcon(icon, false));

        _overflowPanel.Children.Clear();
        foreach (var icon in hidden) _overflowPanel.Children.Add(MakeIcon(icon, true));
        if (hidden.Count == 0) _overflow.IsOpen = false;

        Dock.InvalidateItems();
    }

    static string Id(NotifyIcon i) => i.Identifier ?? $"{i.HWnd}:{i.UID}";

    static string ExeName(NotifyIcon i) => string.IsNullOrEmpty(i.Path) ? "" : IOPath.GetFileName(i.Path).ToLowerInvariant();

    static bool Matches(List<string> list, NotifyIcon i)
    {
        string exe = ExeName(i), stem = IOPath.GetFileNameWithoutExtension(exe), title = i.Title?.Split('\n')[0].Trim() ?? "";
        return list.Any(s => s.Equals(exe, StringComparison.OrdinalIgnoreCase) || s.Equals(stem, StringComparison.OrdinalIgnoreCase)
                          || s.Equals(title, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsShown(NotifyIcon i, HashSet<string> promoted)
    {
        if (Matches(App.Settings.TrayAlwaysHide, i)) return false;
        if (Matches(App.Settings.TrayAlwaysShow, i)) return true;
        return promoted.Contains(ExeName(i));
    }

    /// <summary>Exes that Windows 11 is set to show on the taskbar (Settings > Personalization > Taskbar).</summary>
    static HashSet<string> PromotedExes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return set;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("IsPromoted") is int p && p == 1 && key.GetValue("ExecutablePath") is string path)
                    set.Add(IOPath.GetFileName(path));
            }
        }
        catch (Exception e) { Log.Error("tray promoted", e); }
        return set;
    }

    /// <summary>Middle-click a tray icon to move it between the dock and the overflow.</summary>
    void TogglePinned(NotifyIcon icon, bool currentlyInOverflow)
    {
        string key = string.IsNullOrEmpty(ExeName(icon)) ? icon.Title : ExeName(icon);
        var s = App.Settings;
        s.TrayAlwaysShow.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        s.TrayAlwaysHide.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        (currentlyInOverflow ? s.TrayAlwaysShow : s.TrayAlwaysHide).Add(key);
        s.Save();
        _signature = null;
        Rebuild();
    }

    FrameworkElement MakeIcon(NotifyIcon icon, bool inOverflow)
    {
        // 16px tray art stays at 16px whatever the dock size; scaling it would blur it.
        double size = icon.Icon is BitmapSource b && b.PixelWidth <= 16 ? 16 : Math.Max(16, Math.Round(18 * _k));
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        img.SetBinding(Image.SourceProperty, new Binding(nameof(NotifyIcon.Icon)) { Source = icon });

        var chip = Ui.Chip(img, new Thickness(Math.Max(2, (26 * _k - size) / 2)));
        string Title() => icon.Title?.Split('\n')[0].Trim();
        uint Cursor() => MouseHelper.GetCursorPositionParam();
        int DoubleClick() => (int)Native.GetDoubleClickTime();
        void Place()
        {
            // Apps position their own popups from this rect (Shell_NotifyIconGetRect).
            try
            {
                var p = chip.PointToScreen(new Point(0, 0));
                double dpi = Dock.Dpi;
                icon.Placement = new ManagedShell.Interop.NativeMethods.Rect(
                    (int)p.X, (int)p.Y, (int)(p.X + chip.ActualWidth * dpi), (int)(p.Y + chip.ActualHeight * dpi));
            }
            catch { }
        }

        if (inOverflow) chip.ToolTip = Title();
        chip.MouseEnter += (_, _) =>
        {
            Place();
            icon.IconMouseEnter(Cursor());
            if (!inOverflow) Dock.SetCustomLabel(Title(), chip);
        };
        chip.MouseLeave += (_, _) =>
        {
            icon.IconMouseLeave(Cursor());
            if (!inOverflow) Dock.ClearCustomLabel(chip);
        };
        chip.MouseMove += (_, _) => icon.IconMouseMove(Cursor());
        chip.MouseDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Middle) return;
            Place();
            icon.IconMouseDown(e.ChangedButton, Cursor(), DoubleClick());
        };
        chip.MouseUp += (_, e) =>
        {
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Middle)
            {
                TogglePinned(icon, inOverflow);
                return;
            }
            icon.IconMouseUp(e.ChangedButton, Cursor(), DoubleClick());
            if (inOverflow) _overflow.IsOpen = false;
        };
        return chip;
    }

    public override void Dispose() => _refresh.Stop();
}

/// <summary>Network + volume button. Click for the control center above it, scroll for volume, middle-click to mute.</summary>
public class ControlsItem : FixedItem
{
    readonly VolumeService _volume = new();
    readonly ControlCenter _center;
    readonly Border _chip;
    readonly Border _netHost = new();
    readonly Shapes.Path _wave1 = Glyphs.Stroke(Glyphs.Wave1), _wave2 = Glyphs.Stroke(Glyphs.Wave2), _wave3 = Glyphs.Stroke(Glyphs.Wave3), _mute = Glyphs.Stroke(Glyphs.MuteX);
    readonly FrameworkElement _volBox;
    readonly Border _spacer = new() { Width = 10 };
    string _netText = "";
    bool _hovered;
    double _glyph = 20, _k = 1;

    public ControlsItem(DockWindow dock) : base(dock)
    {
        _volBox = Glyphs.Box(_glyph, Glyphs.Fill(Glyphs.Speaker), _wave1, _wave2, _wave3, _mute);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_netHost);
        row.Children.Add(_spacer);
        row.Children.Add(_volBox);
        _chip = Ui.Chip(row, new Thickness(9, 8, 9, 8));
        SetContent(_chip);
        _center = new ControlCenter(dock, _volume, _chip);

        _chip.MouseDown += (_, e) => { if (e.ChangedButton != MouseButton.Right) e.Handled = true; };
        _chip.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                Dock.ClearCustomLabel(_chip); // the panel takes the label's place
                _center.Toggle();
            }
            else if (e.ChangedButton == MouseButton.Middle) _volume.ToggleMute();
            else return;
            e.Handled = true;
        };
        _chip.MouseWheel += (_, e) =>
        {
            _volume.Step(e.Delta > 0 ? 0.02f : -0.02f);
            e.Handled = true;
        };
        _chip.MouseEnter += (_, _) => { _hovered = true; ShowLabel(); };
        _chip.MouseLeave += (_, _) => { _hovered = false; Dock.ClearCustomLabel(_chip); };

        _volume.Changed += () => Dock.Dispatcher.BeginInvoke(() =>
        {
            _volume.SyncDefaultDevice();
            UpdateVolume();
            if (_center.IsOpen) _center.SyncVolume();
        });
        NetworkChange.NetworkAddressChanged += (_, _) => Dock.Dispatcher.BeginInvoke(UpdateNetwork);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dock.Dispatcher.BeginInvoke(UpdateNetwork);

        UpdateNetwork();
        UpdateVolume();
    }

    void ShowLabel()
    {
        if (_hovered && !_center.IsOpen) Dock.SetCustomLabel(LabelText(), _chip);
    }

    string LabelText()
    {
        string vol = _volume.Muted ? "Muted" : $"Volume {Math.Round(_volume.Level * 100)}%";
        return $"{_netText}   ·   {vol}";
    }

    void UpdateVolume()
    {
        float level = _volume.Level;
        bool muted = _volume.Muted || !_volume.HasDevice;
        _mute.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        _wave1.Visibility = !muted && level > 0.001f ? Visibility.Visible : Visibility.Collapsed;
        _wave2.Visibility = !muted && level > 0.33f ? Visibility.Visible : Visibility.Collapsed;
        _wave3.Visibility = !muted && level > 0.66f ? Visibility.Visible : Visibility.Collapsed;
        ShowLabel();
    }

    void UpdateNetwork()
    {
        var kind = Network.Current();
        _netText = kind switch { Network.Kind.Wired => "Ethernet", Network.Kind.Wifi => "Wi-Fi", _ => "Not connected" };
        _netHost.Child = Network.Glyph(kind, _glyph);
        if (_center.IsOpen) _center.RefreshNetwork();
        ShowLabel();
    }

    public override void ApplyScale(double k)
    {
        if (k == _k) return;
        _k = k;
        _glyph = 20 * k;
        _volBox.Width = _volBox.Height = _glyph;
        _spacer.Width = 10 * k;
        _chip.Padding = new Thickness(9 * k, 8 * k, 9 * k, 8 * k);
        UpdateNetwork();
    }

    public override void Dispose() => _volume.Dispose();
}

public class ClockItem : FixedItem
{
    readonly TextBlock _time = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _date = new() { FontSize = 11, Foreground = Ui.SecondaryBrush, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Border _chip;
    readonly ClockPanel _panel;
    readonly DispatcherTimer _timer;
    bool _hovered;

    public ClockItem(DockWindow dock) : base(dock)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_time);
        stack.Children.Add(_date);
        _chip = Ui.Chip(stack, new Thickness(9, 5, 9, 6));
        SetContent(_chip);
        _panel = new ClockPanel(dock, _chip);

        _chip.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) e.Handled = true; };
        _chip.MouseLeftButtonUp += (_, e) =>
        {
            Dock.ClearCustomLabel(_chip); // the panel takes the label's place
            _panel.Toggle();
            e.Handled = true;
        };
        _chip.MouseEnter += (_, _) => { _hovered = true; if (!_panel.IsOpen) Dock.SetCustomLabel(LongDate(), _chip); };
        _chip.MouseLeave += (_, _) => { _hovered = false; Dock.ClearCustomLabel(_chip); };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    public override void ApplyScale(double k)
    {
        _time.FontSize = 15 * k;
        _date.FontSize = Math.Max(9, 11 * k);
        _chip.Padding = new Thickness(9 * k, 5 * k, 9 * k, 6 * k);
    }

    static string LongDate() => DateTime.Now.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);

    void Update()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        string time = now.ToString(App.Settings.ClockTimeFormat ?? culture.DateTimeFormat.ShortTimePattern, culture);
        string date = now.ToString(App.Settings.ClockDateFormat ?? "ddd d MMM", culture);
        if (time == _time.Text && date == _date.Text) return;
        _time.Text = time;
        _date.Text = date;
        if (_hovered && !_panel.IsOpen) Dock.SetCustomLabel(LongDate(), _chip);
        Dock.InvalidateItems();
    }

    public override void Dispose() => _timer.Stop();
}

/// <summary>Default output device volume via Core Audio.</summary>
sealed class VolumeService : IDisposable
{
    readonly MMDeviceEnumerator _enumerator = new();
    readonly MMDeviceNotificationClient _notifications;
    MMDevice _device;
    volatile bool _deviceChanged = true;

    public event Action Changed;

    public VolumeService()
    {
        try
        {
            _notifications = _enumerator.CreateNotificationClient(false);
            // Raised on a Core Audio thread: just flag it; the UI thread re-reads the device.
            _notifications.DefaultDeviceChanged += (_, _) =>
            {
                _deviceChanged = true;
                Changed?.Invoke();
            };
        }
        catch (Exception e) { Log.Error("audio notify", e); }
        SyncDefaultDevice();
    }

    public bool HasDevice => _device != null;
    public float Level { get { try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; } catch { return 0; } } }
    public bool Muted { get { try { return _device?.AudioEndpointVolume.Mute ?? false; } catch { return false; } } }

    /// <summary>Must run on the thread that created the enumerator (the UI thread).</summary>
    public void SyncDefaultDevice()
    {
        if (!_deviceChanged) return;
        _deviceChanged = false;
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _device.Dispose();
            }
            _device = _enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : null;
            if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
        }
        catch (Exception e)
        {
            Log.Error("audio device", e);
            _device = null;
        }
    }

    void OnVolume(AudioVolumeNotificationData data) => Changed?.Invoke();

    public string DefaultDeviceId { get { try { return _device?.ID; } catch { return null; } } }
    public string DefaultDeviceName { get { try { return _device?.FriendlyName; } catch { return null; } } }

    public List<(string Id, string Name)> OutputDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add((d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch (Exception e) { Log.Error("audio devices", e); }
        return list;
    }

    /// <summary>Windows then reports the change, which re-attaches this service to the new device.</summary>
    public void SetDefaultDevice(string id) => AudioPolicy.SetDefaultEndpoint(id);

    public void SetLevel(float level)
    {
        if (_device == null) return;
        try
        {
            var v = _device.AudioEndpointVolume;
            v.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            if (v.Mute && level > 0) v.Mute = false;
        }
        catch (Exception e) { Log.Error("volume set", e); }
    }

    public void Step(float delta)
    {
        if (_device == null) return;
        try
        {
            var v = _device.AudioEndpointVolume;
            v.MasterVolumeLevelScalar = Math.Clamp(v.MasterVolumeLevelScalar + delta, 0f, 1f);
            if (v.Mute && delta > 0) v.Mute = false;
        }
        catch (Exception e) { Log.Error("volume step", e); }
    }

    public void ToggleMute()
    {
        try { if (_device != null) _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute; }
        catch (Exception e) { Log.Error("mute", e); }
    }

    public void Dispose()
    {
        try
        {
            _notifications?.Dispose();
            _device?.Dispose();
            _enumerator.Dispose();
        }
        catch { }
    }
}
