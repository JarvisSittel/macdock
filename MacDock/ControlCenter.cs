using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.Common.Helpers;
using Shapes = System.Windows.Shapes;

namespace MacDock;

/// <summary>
/// Sound and network panel that opens directly above the dock's controls button. Windows' own Quick Settings
/// panel can't be repositioned (the shell draws it in the bottom-right corner), so this replaces it, with a link
/// to the full Windows panel for everything else.
/// </summary>
sealed class ControlCenter
{
    const double PanelWidth = 300;

    readonly DockWindow _dock;
    readonly VolumeService _volume;
    readonly Popup _popup;
    readonly Slider _slider = new() { Minimum = 0, Maximum = 100, IsMoveToPointEnabled = true, SmallChange = 2, LargeChange = 10 };
    readonly TextBlock _percent = new() { Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.SecondaryBrush };
    readonly Border _muteButton;
    readonly Shapes.Path _w1 = Glyphs.Stroke(Glyphs.Wave1), _w2 = Glyphs.Stroke(Glyphs.Wave2), _w3 = Glyphs.Stroke(Glyphs.Wave3), _x = Glyphs.Stroke(Glyphs.MuteX);
    readonly TextBlock _deviceName = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _deviceList = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
    readonly Border _netGlyph = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _netName = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _netStatus = new() { FontSize = 11, Foreground = Ui.SecondaryBrush };
    bool _syncing;

    public ControlCenter(DockWindow dock, VolumeService volume, FrameworkElement anchor)
    {
        _dock = dock;
        _volume = volume;

        _slider.Style = (Style)Application.Current.FindResource("DockSlider");
        _slider.Resources["SliderAccent"] = Ui.AccentBrush;
        _slider.ValueChanged += (_, e) =>
        {
            if (!_syncing) _volume.SetLevel((float)(e.NewValue / 100));
            _percent.Text = $"{Math.Round(e.NewValue)}%";
        };

        _muteButton = Ui.Chip(Glyphs.Box(20, Glyphs.Fill(Glyphs.Speaker), _w1, _w2, _w3, _x), new Thickness(6));
        _muteButton.MouseLeftButtonUp += (_, _) => _volume.ToggleMute();

        var volumeRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_muteButton, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(_percent, System.Windows.Controls.Dock.Right);
        volumeRow.Children.Add(_muteButton);
        volumeRow.Children.Add(_percent);
        volumeRow.Children.Add(new Border { Margin = new Thickness(8, 0, 0, 0), Child = _slider, VerticalAlignment = VerticalAlignment.Center });

        var deviceRow = new DockPanel();
        var chevron = Glyphs.Box(12, Glyphs.Stroke(Glyphs.ChevronDown, 2, 0.7));
        DockPanel.SetDock(chevron, System.Windows.Controls.Dock.Right);
        deviceRow.Children.Add(chevron);
        deviceRow.Children.Add(_deviceName);
        var deviceButton = Ui.Chip(deviceRow, new Thickness(8, 6, 8, 6));
        deviceButton.Margin = new Thickness(0, 6, 0, 0);
        deviceButton.MouseLeftButtonUp += (_, _) => ToggleDeviceList();

        var netText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        netText.Children.Add(_netName);
        netText.Children.Add(_netStatus);
        var netRow = new StackPanel { Orientation = Orientation.Horizontal };
        netRow.Children.Add(_netGlyph);
        netRow.Children.Add(netText);
        var netButton = Ui.Chip(netRow, new Thickness(8, 6, 8, 6));
        netButton.MouseLeftButtonUp += (_, _) => Run("ms-settings:network-status");

        var footer = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        footer.Children.Add(Link("Sound settings", () => Run("ms-settings:sound")));
        footer.Children.Add(Link("Network settings", () => Run("ms-settings:network")));
        footer.Children.Add(Link("All quick settings", () =>
        {
            _popup.IsOpen = false;
            ShellHelper.ShowActionCenter();
        }));

        var root = new StackPanel { Width = PanelWidth };
        root.Children.Add(Heading("Sound"));
        root.Children.Add(volumeRow);
        root.Children.Add(deviceButton);
        root.Children.Add(_deviceList);
        root.Children.Add(Divider());
        root.Children.Add(Heading("Network"));
        root.Children.Add(netButton);
        root.Children.Add(Divider());
        root.Children.Add(footer);

        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true, // the dock closes it on outside clicks; see DockWindow.RegisterPopup
            PopupAnimation = PopupAnimation.Fade,
            Placement = PlacementMode.Custom,
            PlacementTarget = anchor,
            CustomPopupPlacementCallback = Ui.Above,
            Child = new Border { Style = (Style)Application.Current.FindResource("DockPanelBorder"), Padding = new Thickness(14, 12, 14, 12), Child = root },
        };
        _popup.MouseWheel += (_, e) => _volume.Step(e.Delta > 0 ? 0.02f : -0.02f);
        dock.RegisterPopup(_popup, anchor);
    }

    public bool IsOpen => _popup.IsOpen;

    public void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }
        _deviceList.Visibility = Visibility.Collapsed;
        SyncVolume();
        RefreshNetwork();
        _popup.IsOpen = true;
    }

    public void SyncVolume()
    {
        _syncing = true;
        _slider.Value = Math.Round(_volume.Level * 100);
        _syncing = false;
        _percent.Text = $"{Math.Round(_slider.Value)}%";

        bool muted = _volume.Muted || !_volume.HasDevice;
        _x.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        _w1.Visibility = !muted && _slider.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
        _w2.Visibility = !muted && _slider.Value > 33 ? Visibility.Visible : Visibility.Collapsed;
        _w3.Visibility = !muted && _slider.Value > 66 ? Visibility.Visible : Visibility.Collapsed;
        _slider.Opacity = muted ? 0.5 : 1;
        _deviceName.Text = _volume.DefaultDeviceName ?? "No output device";
        if (_deviceList.Visibility == Visibility.Visible) FillDeviceList();
    }

    public void RefreshNetwork()
    {
        var kind = Network.Current();
        _netGlyph.Child = Network.Glyph(kind, 22);
        var (name, internet) = Network.Details();
        _netName.Text = name ?? (kind == Network.Kind.Wired ? "Ethernet" : kind == Network.Kind.Wifi ? "Wi-Fi" : "Not connected");
        _netStatus.Text = kind == Network.Kind.None ? "No network" : internet ? "Connected" : "No internet";
    }

    void ToggleDeviceList()
    {
        bool show = _deviceList.Visibility != Visibility.Visible;
        if (show) FillDeviceList();
        _deviceList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    void FillDeviceList()
    {
        _deviceList.Children.Clear();
        string current = _volume.DefaultDeviceId;
        foreach (var (id, name) in _volume.OutputDevices())
        {
            var row = new DockPanel();
            var check = new TextBlock { Text = id == current ? "✓" : "", Width = 18, Foreground = Ui.AccentBrush };
            DockPanel.SetDock(check, System.Windows.Controls.Dock.Left);
            row.Children.Add(check);
            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            var chip = Ui.Chip(row, new Thickness(8, 5, 8, 5));
            string target = id;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _volume.SetDefaultDevice(target);
                _deviceList.Visibility = Visibility.Collapsed;
            };
            _deviceList.Children.Add(chip);
        }
    }

    static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Ui.SecondaryBrush,
        Margin = new Thickness(2, 0, 0, 6),
    };

    static Border Divider() => new() { Height = 1, Background = Ui.Brush("#3C3C3C"), Margin = new Thickness(0, 12, 0, 10) };

    static FrameworkElement Link(string text, Action onClick)
    {
        var chip = Ui.Chip(new TextBlock { Text = text, FontSize = 12, Foreground = Ui.AccentBrush }, new Thickness(6, 4, 6, 4));
        chip.MouseLeftButtonUp += (_, _) => onClick();
        return chip;
    }

    void Run(string uri)
    {
        _popup.IsOpen = false;
        Ui.Open(uri);
    }
}

/// <summary>Current network connection, for the dock button and the control center.</summary>
public static class Network
{
    public enum Kind { None, Wifi, Wired }

    public static Kind Current()
    {
        bool wifi = false, wired = false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var desc = nic.Description;
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
                bool hasGateway = nic.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address != null && !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any));
                if (!hasGateway) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi = true;
                else wired = true;
            }
        }
        catch (Exception e) { Log.Error("network", e); }
        return wired ? Kind.Wired : wifi ? Kind.Wifi : Kind.None;
    }

    public static FrameworkElement Glyph(Kind kind, double size)
    {
        if (kind == Kind.Wired) return Glyphs.Box(size, Glyphs.Stroke(Glyphs.Ethernet, 1.6));
        double o = kind == Kind.Wifi ? 1 : 0.35;
        return Glyphs.Box(size, Glyphs.Stroke(Glyphs.WifiArc3, 1.9, o), Glyphs.Stroke(Glyphs.WifiArc2, 1.9, o),
            Glyphs.Stroke(Glyphs.WifiArc1, 1.9, o), Glyphs.Fill(Glyphs.WifiDot, o));
    }

    /// <summary>Name of the connected network (as Windows shows it) and whether it reaches the internet.</summary>
    public static (string Name, bool Internet) Details()
    {
        try
        {
            // Network List Manager, via late-bound COM.
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
            string name = null;
            bool internet = false;
            foreach (dynamic net in nlm.GetNetworks(1 /* NLM_ENUM_NETWORK_CONNECTED */))
            {
                int connectivity = net.GetConnectivity();
                bool hasInternet = (connectivity & (0x40 | 0x400)) != 0; // IPv4 / IPv6 internet
                if (name == null || (hasInternet && !internet)) name = net.GetName();
                internet |= hasInternet;
            }
            return (name, internet);
        }
        catch (Exception e)
        {
            Log.Error("network details", e);
            return (null, Current() != Kind.None);
        }
    }
}

/// <summary>Switches the default audio output. IPolicyConfig is undocumented but is what Windows' own sound UI uses.</summary>
static class AudioPolicy
{
    public static void SetDefaultEndpoint(string deviceId)
    {
        object client = null;
        try
        {
            client = new PolicyConfigClient();
            var config = (IPolicyConfig)client;
            for (int role = 0; role < 3; role++) config.SetDefaultEndpoint(deviceId, role); // console, multimedia, communications
        }
        catch (Exception e) { Log.Error("set default audio device", e); }
        finally
        {
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
