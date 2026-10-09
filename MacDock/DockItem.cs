using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace MacDock;

/// <summary>All dock sizes, in DIPs, derived from the icon size.</summary>
public class DockMetrics
{
    public DockMetrics(DockSettings s)
    {
        S = Math.Clamp(s.IconSize, 24, 128);
        Mag = s.Magnification ? Math.Clamp(s.MagnifiedSize, S, 256) : S; // no headroom needed when off
        Range = Math.Clamp(s.MagnifyRange, 1, 6);
        _gap = s.Magnification ? 14 : 6; // extra breathing room between icons when they magnify
    }

    readonly double _gap;

    public double S { get; }          // icon size
    public double Mag { get; }        // magnified icon size
    public double Range { get; }      // magnification spread, in item widths
    /// <summary>Scale of everything else relative to the original 54px design, so the whole dock resizes together.</summary>
    public double K => S / 54.0;
    public double PadH => 7 * K;
    public double PadTop => 6 * K;
    public double PadBottom => Math.Max(7, 10 * K); // room for the running indicator
    public double BottomGap => 5 * K;               // space between dock and screen edge
    public double LabelSpace => Math.Max(36, 44 * K);
    public double SepW => 15 * K;

    public double ItemW => S + _gap * K;
    public double PillH => S + PadTop + PadBottom;
    public double Bounce => S * 0.42;
    public double H => BottomGap + PillH + Math.Max(Mag - S, Bounce) + LabelSpace;
    public double PillTop => H - BottomGap - PillH;
    public double ContentTop => PillTop + PadTop;
    public double ContentBottom => H - BottomGap - PadBottom;
    public double Radius => 8; // Windows 11 window corners, as Flow Launcher uses
    public double HiddenOffset => PillH + BottomGap + 2;
}

public abstract class DockItem
{
    public const double Hop = 0.5; // seconds per bounce

    protected readonly DockWindow Dock;
    public readonly Canvas View = new();

    public double Scale = 1;
    public double X, Width;
    public double BounceStart = -1, BounceEnd = -1;
    public bool Attention;

    protected DockItem(DockWindow dock) { Dock = dock; }

    public virtual bool Magnifies => false;
    public virtual string Label => null;
    public virtual FrameworkElement MenuAnchor => View;

    public abstract double BaseWidth(DockMetrics m);
    public abstract void Layout(DockMetrics m, double x, double width, double bounce);

    /// <summary>Called on every relayout with <see cref="DockMetrics.K"/>; items with fixed-size content resize here.</summary>
    public virtual void ApplyScale(double k) { }

    public virtual void OnClick(MouseButtonEventArgs e) { }
    public virtual void OnMiddleClick() { }
    public virtual ContextMenu BuildMenu() => null;
    public virtual void SetPressed(bool pressed) { }

    public virtual DragDropEffects DropEffect(IDataObject data) => DragDropEffects.None;
    public virtual string DropLabel(IDataObject data) => null;
    public virtual void Drop(IDataObject data) { }

    public virtual void Dispose() { }

    // --- Bouncing ---------------------------------------------------------------

    public bool IsBouncing => BounceStart >= 0;

    public void StartBounce(double now, double seconds)
    {
        if (BounceStart < 0) BounceStart = now;
        BounceEnd = BounceStart + Math.Ceiling((now + seconds - BounceStart) / Hop) * Hop;
    }

    /// <summary>Stops after the current hop lands, so the icon never snaps mid-air.</summary>
    public void StopBounce(double now)
    {
        if (BounceStart < 0 || Attention) return;
        BounceEnd = BounceStart + Math.Ceiling((now - BounceStart) / Hop) * Hop;
    }

    public double BounceOffset(double now, double amplitude)
    {
        if (BounceStart < 0) return 0;
        double t = now - BounceStart;
        if (Attention)
        {
            // Three hops, then a pause, for as long as the window is flashing.
            double cycle = 3 * Hop + 1.6, tc = t % cycle;
            if (tc >= 3 * Hop) return 0;
            double fa = tc % Hop / Hop;
            return amplitude * 4 * fa * (1 - fa);
        }
        if (now >= BounceEnd) { BounceStart = -1; return 0; }
        double f = t % Hop / Hop;
        return amplitude * 4 * f * (1 - f);
    }
}

/// <summary>A magnifying icon with a running-indicator dot.</summary>
public abstract class IconItem : DockItem
{
    protected readonly Image Icon = new() { Stretch = Stretch.Uniform };
    /// <summary>Grey dot when running; a wider accent-coloured bar for the active app, like Flow's selection bullet.</summary>
    protected readonly Border Indicator = new() { Height = 3, CornerRadius = new CornerRadius(1.5), Visibility = Visibility.Hidden };
    readonly Border _hit = new() { Background = Ui.HitBrush };

    public double IconTop, IconSize;

    protected IconItem(DockWindow dock) : base(dock)
    {
        RenderOptions.SetBitmapScalingMode(Icon, BitmapScalingMode.HighQuality);
        View.Children.Add(_hit);
        View.Children.Add(Icon);
        View.Children.Add(Indicator);
    }

    protected void SetIndicator(bool running, bool active)
    {
        Indicator.Visibility = running ? Visibility.Visible : Visibility.Hidden;
        Indicator.Width = active ? 16 : 6;
        Indicator.Background = active ? Ui.AccentBrush : Ui.RunningBrush;
    }

    public override bool Magnifies => true;
    public override FrameworkElement MenuAnchor => Icon;
    public override double BaseWidth(DockMetrics m) => m.ItemW;

    public override void SetPressed(bool pressed) => Icon.Opacity = pressed ? 0.7 : 1;

    public override void Layout(DockMetrics m, double x, double width, double bounce)
    {
        double size = m.S * Scale;
        IconSize = size;
        IconTop = m.ContentBottom - size - bounce;

        Canvas.SetLeft(View, x);
        View.Width = width;
        View.Height = m.H;

        Icon.Width = Icon.Height = size;
        Canvas.SetLeft(Icon, (width - size) / 2);
        Canvas.SetTop(Icon, IconTop);

        // Invisible hit area from the icon's top down to the dock's bottom, so transparent icon pixels still take clicks.
        double hitTop = Math.Min(IconTop, m.PillTop);
        Canvas.SetTop(_hit, hitTop);
        _hit.Width = width;
        _hit.Height = m.H - m.BottomGap - hitTop;

        Canvas.SetLeft(Indicator, (width - Indicator.Width) / 2);
        Canvas.SetTop(Indicator, m.ContentBottom + (m.PadBottom - Indicator.Height) / 2);

        LayoutExtras(m, width, size, IconTop);
    }

    protected virtual void LayoutExtras(DockMetrics m, double width, double size, double top) { }
}

/// <summary>A non-magnifying item (tray, controls, clock), vertically centred in the dock.</summary>
public abstract class FixedItem : DockItem
{
    protected FrameworkElement Content { get; private set; }

    protected FixedItem(DockWindow dock) : base(dock) { }

    protected void SetContent(FrameworkElement content)
    {
        Content = content;
        View.Children.Add(content);
    }

    public override double BaseWidth(DockMetrics m)
    {
        Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Content.DesiredSize.Width + 6;
    }

    public override void Layout(DockMetrics m, double x, double width, double bounce)
    {
        Canvas.SetLeft(View, x);
        View.Width = width;
        View.Height = m.H;
        var d = Content.DesiredSize;
        Canvas.SetLeft(Content, (width - d.Width) / 2);
        Canvas.SetTop(Content, m.ContentTop + (m.S - d.Height) / 2);
    }
}

public class SeparatorItem : DockItem
{
    readonly Border _line = new() { Width = 1, Background = Ui.Brush("#24FFFFFF") }; // Flow's SeparatorForeground

    public SeparatorItem(DockWindow dock) : base(dock) { View.Children.Add(_line); }

    public override double BaseWidth(DockMetrics m) => m.SepW;

    public override void Layout(DockMetrics m, double x, double width, double bounce)
    {
        Canvas.SetLeft(View, x);
        View.Width = width;
        View.Height = m.H;
        double h = m.S * 0.72;
        _line.Height = h;
        Canvas.SetLeft(_line, (width - 1) / 2);
        Canvas.SetTop(_line, m.ContentTop + (m.S - h) / 2 + 2);
    }
}

static class Ui
{
    public static Brush Brush(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    /// <summary>Alpha 1/255: invisible, but the layered window still routes clicks to it.</summary>
    public static readonly Brush HitBrush = Brush("#01000000");
    public static readonly Brush HoverBrush = Brush("#198F8F8F");      // Flow's ItemSelectedBackgroundColor
    public static readonly Brush SecondaryBrush = Brush("#999999");
    public static readonly Brush RunningBrush = Brush("#9A9A9A");

    /// <summary>Windows accent colour, light variant (Flow's BasicSystemAccentColor = SystemAccentColorLight1).</summary>
    public static Brush AccentBrush { get; private set; } = LoadAccent();

    static Ui()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General) AccentBrush = LoadAccent();
        };
    }

    static Brush LoadAccent()
    {
        var color = SystemParameters.WindowGlassColor;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            // AccentPalette: 8 RGBA entries, Light3, Light2, Light1, Base, Dark1...
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 12) color = Color.FromRgb(p[8], p[9], p[10]);
        }
        catch { }
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    public static Border Chip(UIElement child, Thickness padding)
    {
        var b = new Border { Child = child, Padding = padding, CornerRadius = new CornerRadius(5), Background = HitBrush };
        b.MouseEnter += (_, _) => b.Background = HoverBrush;
        b.MouseLeave += (_, _) => b.Background = HitBrush;
        return b;
    }

    public static CustomPopupPlacement[] Above(Size popup, Size target, Point offset) =>
        new[] { new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height - 10), PopupPrimaryAxis.Horizontal) };

    public static MenuItem Item(string header, Action onClick, bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        if (onClick != null) item.Click += (_, _) => onClick();
        return item;
    }

    public static void Open(string target, string args = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e) { Log.Error("open " + target, e); }
    }

    public static void RevealInExplorer(string path) => Open("explorer.exe", $"/select,\"{path}\"");
}
