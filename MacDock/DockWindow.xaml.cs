using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using Microsoft.Win32;

namespace MacDock;

/// <summary>
/// The dock. Items are laid out by hand every frame (rather than by a WPF panel) so magnification can
/// grow icons smoothly while keeping the point under the cursor fixed, the way macOS does.
/// </summary>
public partial class DockWindow : Window
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly BlurWindow _blur;
    readonly DispatcherTimer _poll;
    readonly AppTracker _apps;
    readonly SeparatorItem _placesSep, _systemSep;
    readonly TrashItem _trash;
    readonly TrayItem _tray;
    readonly ControlsItem _controls;
    readonly ClockItem _clockItem;

    IntPtr _hwnd;
    List<DockItem> _items = new();
    double[] _baseW = Array.Empty<double>(), _w = Array.Empty<double>();
    double _baseTotal, _pillLeft, _pillWidth;
    double _screenW, _screenH; // DIPs

    bool _animating, _layoutPending, _appBarRegistered, _lastFullscreen;
    double _lastFrame, _busyAt;
    TimeSpan _lastRenderTime;
    int _pollCount;

    bool _hover;
    double _lastU = -1; // cursor position along the un-magnified dock, the magnification anchor
    double _intensity;  // 0..1, how magnified the dock is overall
    DockItem _hoverItem;

    bool _shown = true;
    double _slide, _revealAt = -1, _leaveAt = -1;

    readonly List<(Popup Popup, FrameworkElement Anchor)> _popups = new();
    readonly Dictionary<Popup, double> _popupAnchorX = new();
    bool _mouseWasDown;
    ContextMenu _menu;

    DockItem _pressItem;
    Point _pressPoint, _dragPoint;
    bool _dragging, _dragOut;

    DockItem _dropItem;
    string _dropText;

    string _customText;
    FrameworkElement _customAnchor;

    public DockMetrics M { get; private set; }
    public double Now => _clock.Elapsed.TotalSeconds;
    public double Dpi { get; private set; } = 1;

    public DockWindow(BlurWindow blur)
    {
        InitializeComponent();
        _blur = blur;
        M = new DockMetrics(App.Settings);

        _placesSep = new SeparatorItem(this);
        _systemSep = new SeparatorItem(this);
        if (App.Settings.ShowTrash) _trash = new TrashItem(this);
        if (App.Settings.ShowTray && App.Shell.NotificationArea != null) _tray = new TrayItem(this);
        _controls = new ControlsItem(this);
        _clockItem = new ClockItem(this);
        _apps = new AppTracker(this);
        _apps.StructureChanged += RebuildItems;

        Root.MouseDown += OnMouseDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseUp += OnMouseUp;
        Root.LostMouseCapture += OnLostCapture;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => { _dropItem = null; StartAnimating(); };
        Drop += OnDrop;

        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _poll.Tick += (_, _) => Poll();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Relayout);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        WindowHelper.ExcludeWindowFromPeek(_hwnd);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        Dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        RebuildItems();
        _leaveAt = Now + 1.5; // show briefly at startup, then tuck away if auto-hide is on
        _poll.Start();
        StartAnimating();
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (msg == WM_MOUSEACTIVATE)
        {
            // Clicking the dock must never take focus from the app you're working in.
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dpi = newDpi.DpiScaleX;
        Relayout();
    }

    public void Shutdown()
    {
        _poll.Stop();
        if (_animating) CompositionTarget.Rendering -= OnRendering;
        if (_appBarRegistered)
        {
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        }
        foreach (var item in _items) item.Dispose();
    }

    // --- API for items -----------------------------------------------------------

    public void InvalidateItems()
    {
        if (_layoutPending) return;
        _layoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _layoutPending = false;
            Relayout();
        });
    }

    public void StartBounce(DockItem item, double seconds)
    {
        item.StartBounce(Now, seconds);
        StartAnimating();
    }

    public void StopBounce(DockItem item) => item.StopBounce(Now);

    public void SetAttention(DockItem item, bool on)
    {
        item.Attention = on;
        item.BounceStart = on ? Now : -1;
        StartAnimating();
    }

    public void SetCustomLabel(string text, FrameworkElement anchor)
    {
        _customText = text;
        _customAnchor = string.IsNullOrWhiteSpace(text) ? null : anchor;
        StartAnimating();
    }

    public void ClearCustomLabel(FrameworkElement anchor)
    {
        if (_customAnchor == anchor) _customAnchor = null;
        StartAnimating();
    }

    /// <summary>
    /// Keeps the dock up while this popup is open and closes it on any click outside it or its anchor.
    /// Use with StaysOpen = true: WPF's own click-away closing captures the mouse, which in this never-focused
    /// window froze the dock and swallowed the next click.
    /// </summary>
    public void RegisterPopup(Popup popup, FrameworkElement anchor)
    {
        _popups.Add((popup, anchor));
        popup.Opened += (_, _) => StartAnimating();
        popup.Closed += (_, _) => StartAnimating();
    }

    // Read live rather than counted from Opened/Closed events, so a missed event can't leave the dock stuck open.
    bool MenuOpen => _menu?.IsOpen == true;
    bool PopupOpen => MenuOpen || _popups.Any(p => p.Popup.IsOpen);

    void UpdatePopups(Native.POINT cursor)
    {
        bool down = Native.IsAnyMouseButtonDown();
        bool newPress = down && !_mouseWasDown;
        _mouseWasDown = down;

        for (int i = 0; i < _popups.Count; i++)
        {
            var (popup, anchor) = _popups[i];
            if (!popup.IsOpen) continue;
            if (!anchor.IsVisible) // e.g. the tray's last hidden icon went away
            {
                popup.IsOpen = false;
                continue;
            }
            // The click itself isn't swallowed, so it still lands on whatever was clicked.
            if (newPress && !ContainsScreenPoint(popup.Child as FrameworkElement, cursor) && !ContainsScreenPoint(anchor, cursor))
            {
                popup.IsOpen = false;
                continue;
            }
            // Follow the anchor as magnification shifts it; changing an offset makes WPF re-place the popup.
            double x = anchor.PointToScreen(new Point(0, 0)).X;
            if (Math.Abs(x - _popupAnchorX.GetValueOrDefault(popup, x)) > 0.5)
                popup.HorizontalOffset = popup.HorizontalOffset == 0 ? 0.01 : 0;
            _popupAnchorX[popup] = x;
        }
    }

    static bool ContainsScreenPoint(FrameworkElement element, Native.POINT p)
    {
        if (element == null || !element.IsVisible) return false;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
    }

    /// <summary>Screen rectangle (physical pixels) of an item's icon, for minimize animations.</summary>
    public Native.RECT? ItemScreenRect(DockItem item)
    {
        if (!_items.Contains(item)) return null;
        double l = (Left + item.X) * Dpi, t = (Top + M.ContentTop + _slide) * Dpi;
        return new Native.RECT { Left = (int)l, Top = (int)t, Right = (int)(l + item.Width * Dpi), Bottom = (int)(t + M.S * Dpi) };
    }

    // --- Structure and layout --------------------------------------------------------

    void RebuildItems()
    {
        var list = new List<DockItem>(_apps.Items);
        if (_trash != null)
        {
            list.Add(_placesSep);
            if (_trash != null) list.Add(_trash);
        }
        list.Add(_systemSep);
        if (_tray != null) list.Add(_tray);
        list.Add(_controls);
        list.Add(_clockItem);

        foreach (var gone in _items.Except(list)) ItemsLayer.Children.Remove(gone.View);
        foreach (var added in list.Except(_items)) ItemsLayer.Children.Add(added.View);
        _items = list;
        if (_hoverItem != null && !list.Contains(_hoverItem)) _hoverItem = null;
        Relayout();
    }

    void Relayout()
    {
        _screenW = SystemParameters.PrimaryScreenWidth;
        _screenH = SystemParameters.PrimaryScreenHeight;

        int n = _items.Count;
        _baseW = new double[n];
        _w = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            _items[i].ApplyScale(M.K);
            sum += _baseW[i] = _items[i].BaseWidth(M);
        }
        _baseTotal = sum + 2 * M.PadH;

        // Wide enough for the dock fully magnified at either end, plus room for labels.
        double extra = App.Settings.Magnification ? (M.Mag / M.S - 1) * App.Settings.MagnifyPush * M.ItemW * M.Range : 0;
        double width = Math.Min(_screenW, Math.Ceiling(_baseTotal + 2 * extra + 240));
        Width = width;
        Height = M.H;
        Left = Math.Round((_screenW - width) / 2);
        Top = _screenH - M.H;

        PillOuter.CornerRadius = new CornerRadius(M.Radius);
        PillOuter.Background = BackgroundBrush();

        UpdateAppBar();
        Frame(0);
        StartAnimating();
    }

    static Brush BackgroundBrush()
    {
        try { return Ui.Brush(App.Settings.BackgroundColor); }
        catch { return Ui.Brush("#A61C1C1F"); }
    }

    DockItem ItemAt(double x)
    {
        foreach (var it in _items)
            if (x >= it.X && x < it.X + it.Width) return it;
        return null;
    }

    // --- Animation loop ----------------------------------------------------------------

    void StartAnimating()
    {
        _busyAt = Now;
        if (_animating) return;
        _animating = true;
        _lastFrame = Now;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object sender, EventArgs e)
    {
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderTime) return; // Rendering can fire more than once per frame
            _lastRenderTime = re.RenderingTime;
        }
        double now = Now;
        double dt = Math.Min(now - _lastFrame, 0.05);
        _lastFrame = now;
        if (dt <= 0) return;

        double frameStart = Now;
        Frame(dt);
        if (App.Settings.LogFrameStats) RecordFrame(dt, Now - frameStart);

        if (now - _busyAt > 0.4)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            FlushFrameStats();
        }
    }

    // --- Frame-rate diagnostics (settings: "LogFrameStats": true) ------------------------

    readonly List<double> _frameGaps = new();
    double _frameWorkMax, _edgeMin = double.MaxValue, _edgeMax = double.MinValue, _edgeTravel, _edgeLast = double.NaN, _edgeLastDelta;
    int _edgeReversals, _gcAtStart;

    void RecordFrame(double gap, double work)
    {
        _frameGaps.Add(gap);
        _frameWorkMax = Math.Max(_frameWorkMax, work);
        // How much the dock's left edge moves: the wobble metric.
        _edgeMin = Math.Min(_edgeMin, _pillLeft);
        _edgeMax = Math.Max(_edgeMax, _pillLeft);
        if (!double.IsNaN(_edgeLast))
        {
            double delta = _pillLeft - _edgeLast;
            _edgeTravel += Math.Abs(delta);
            if (Math.Abs(delta) > 0.01)
            {
                if (delta * _edgeLastDelta < 0) _edgeReversals++;
                _edgeLastDelta = delta;
            }
        }
        _edgeLast = _pillLeft;
        if (_frameGaps.Count >= 240) FlushFrameStats();
    }

    void FlushFrameStats()
    {
        if (_frameGaps.Count < 10) { _frameGaps.Clear(); return; }
        var sorted = _frameGaps.OrderBy(g => g).ToList();
        double total = _frameGaps.Sum();
        Log.Write($"frames: {_frameGaps.Count} in {total:F2}s = {_frameGaps.Count / total:F0} fps; gap median {sorted[sorted.Count / 2] * 1000:F1}ms, " +
                  $"p95 {sorted[(int)(sorted.Count * 0.95)] * 1000:F1}ms, max {sorted[^1] * 1000:F1}ms; layout work max {_frameWorkMax * 1000:F2}ms; " +
                  $"dock left edge range {_edgeMax - _edgeMin:F1}px, total travel {_edgeTravel:F1}px, direction reversals {_edgeReversals}; " +
                  $"slow frames at " + string.Join(",", _frameGaps.Select((g, i) => (g, i)).Where(t => t.g > 0.02).Select(t => $"{t.i}:{t.g * 1000:F0}")) + $" gen0 GCs {GC.CollectionCount(0) - _gcAtStart}");
        _gcAtStart = GC.CollectionCount(0);
        _frameGaps.Clear();
        _frameWorkMax = 0;
        _edgeMin = double.MaxValue; _edgeMax = double.MinValue; _edgeTravel = 0; _edgeLast = double.NaN; _edgeLastDelta = 0; _edgeReversals = 0;
    }

    /// <summary>Cheap idle check; wakes the animation loop when the cursor approaches.</summary>
    void Poll()
    {
        bool fullscreen = App.Shell.FullScreenHelper.FullScreenApps.Count > 0;
        if (!_animating)
        {
            Native.GetCursorPos(out var pt);
            double px = pt.X / Dpi - Left, py = pt.Y / Dpi - Top;
            bool near = px >= -60 && px <= Width + 60 && py >= -60;
            bool bouncing = _items.Any(i => i.IsBouncing);
            if (near || bouncing || fullscreen != _lastFullscreen) StartAnimating();
        }
        _lastFullscreen = fullscreen;

        // Other always-on-top windows can end up above us; reclaim the top every few seconds.
        if (++_pollCount % 100 == 0 && _shown && !fullscreen)
        {
            const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            if (_blur != null) Native.SetWindowPos(_blur.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        }
    }

    void Frame(double dt)
    {
        int n = _items.Count;
        if (n == 0 || _baseW.Length != n) return;

        double now = Now;
        Native.GetCursorPos(out var pt);
        UpdatePopups(pt);
        double W = Width;
        double px = pt.X / Dpi - Left, py = pt.Y / Dpi - Top;
        bool atBottomEdge = pt.Y >= (int)Math.Round(_screenH * Dpi) - 2;
        bool fullscreen = App.Shell.FullScreenHelper.FullScreenApps.Count > 0;

        double sumBase = _baseTotal - 2 * M.PadH;
        double left0 = (W - _baseTotal) / 2;

        // Hover: inside the dock, or (once hovering) anywhere over the magnified icons.
        bool inX = px >= _pillLeft && px <= _pillLeft + _pillWidth;
        bool inPill = inX && py >= M.PillTop + _slide && py <= M.H + 1;
        bool inIcons = _hover && inX && py >= M.PillTop + _slide - (M.Mag - M.S) - 4;
        _hover = _shown && _slide < M.PillH / 2 && (inPill || inIcons) && !MenuOpen;
        if (_hover) _lastU = Math.Clamp(px - left0 - M.PadH, 0, sumBase);
        if (_dragging) UpdateDrag();

        // Magnification. Icon size follows a cosine bell centred on the cursor; only one overall intensity eases
        // (in when the cursor arrives, out when it leaves), so sizes track the cursor exactly with no lag.
        //
        // Neighbours make room for part of each icon's growth (MagnifyPush: 1 = macOS, 0 = grow in place). The
        // room is the exact integral of the bell across each item, not a per-icon step, so every edge and icon
        // moves as a smooth function of the cursor and the point under the cursor never moves. Per-icon steps
        // made the whole dock twitch each time the cursor crossed from one icon to the next.
        bool busy = false;
        double intensityTarget = _hover && App.Settings.Magnification ? 1 : 0;
        if (dt > 0 && _intensity != intensityTarget)
        {
            _intensity += (intensityTarget - _intensity) * (1 - Math.Exp(-dt * 16));
            if (Math.Abs(intensityTarget - _intensity) < 0.002) _intensity = intensityTarget;
            else busy = true;
        }
        bool active = _lastU >= 0 && _intensity > 0;
        double reach = M.Range * M.ItemW, boost = (M.Mag / M.S - 1) * _intensity;
        double stretch = boost * Math.Clamp(App.Settings.MagnifyPush, 0, 1);
        double u = active ? _lastU : 0;

        // Integral of the bell (1 + cos(pi*d/R)) / 2 from 0 to d, flat beyond the bell's reach.
        double BellIntegral(double d)
        {
            d = Math.Clamp(d, -reach, reach);
            return d / 2 + reach / (2 * Math.PI) * Math.Sin(Math.PI * d / reach);
        }

        double a = 0, grown = 0, grownBeforeCursor = 0;
        for (int i = 0; i < n; i++)
        {
            var it = _items[i];
            double b = a + _baseW[i];
            double extra = active && it.Magnifies ? stretch * (BellIntegral(b - u) - BellIntegral(a - u)) : 0;
            if (active && u >= a && u <= b)
                grownBeforeCursor = grown + (it.Magnifies ? stretch * (0 - BellIntegral(a - u)) : 0);
            if (it.Magnifies)
            {
                double d = Math.Abs(u - (a + b) / 2);
                it.Scale = active && d < reach ? 1 + boost * (1 + Math.Cos(Math.PI * d / reach)) / 2 : 1;
                Panel.SetZIndex(it.View, (int)(it.Scale * 1000));
            }
            _w[i] = _baseW[i] + extra;
            grown += extra;
            a = b;
        }
        if (!_hover && _intensity == 0) _lastU = -1;

        // Growth left of the cursor pushes the dock's left edge out by the same amount, so the cursor's spot stays put.
        double total = _baseTotal + grown;
        double left1 = Math.Clamp(left0 - grownBeforeCursor, 2, Math.Max(2, W - total - 2));
        _pillLeft = left1;
        _pillWidth = total;

        Canvas.SetLeft(PillOuter, left1);
        Canvas.SetTop(PillOuter, M.PillTop);
        PillOuter.Width = total;
        PillOuter.Height = M.PillH;

        double x = left1 + M.PadH;
        for (int i = 0; i < n; i++)
        {
            var it = _items[i];
            it.X = x;
            it.Width = _w[i];
            double bounce = it.BounceOffset(now, M.Bounce);
            if (it.IsBouncing) busy = true;
            it.Layout(M, x, _w[i], bounce);
            x += _w[i];
        }

        if (_dragging && _pressItem != null)
        {
            if (_pressItem.View.RenderTransform is not TranslateTransform tt)
                _pressItem.View.RenderTransform = tt = new TranslateTransform();
            tt.X = _dragPoint.X - (_pressItem.X + _pressItem.Width / 2);
            // Follow the cursor up, but stop below the window's top edge (with room for the label) so it never clips away.
            double minY = _pressItem is IconItem icon ? 30 - icon.IconTop : 0;
            tt.Y = Math.Max(minY, Math.Min(0, _dragPoint.Y - _pressPoint.Y));
        }

        _hoverItem = _hover ? ItemAt(px) : null;
        UpdateLabel();

        UpdateVisibility(now, px, py, atBottomEdge, fullscreen);
        double slideTarget = _shown ? 0 : M.HiddenOffset;
        if (_slide != slideTarget)
        {
            double ks = dt > 0 ? 1 - Math.Exp(-dt * 14) : 0;
            _slide += (slideTarget - _slide) * ks;
            if (Math.Abs(slideTarget - _slide) < 0.5) _slide = slideTarget;
            else busy = true;
        }
        Slide.Y = _slide;

        _blur?.SetRect(
            (int)Math.Round((Left + left1) * Dpi),
            (int)Math.Round((Top + M.PillTop + _slide) * Dpi),
            (int)Math.Round(total * Dpi),
            (int)Math.Round(M.PillH * Dpi));

        bool near = px >= -60 && px <= W + 60 && py >= -60;
        if (busy || near || _dragging || _revealAt >= 0 || _leaveAt >= 0 || PopupOpen)
            _busyAt = now;
    }


    void UpdateVisibility(double now, double px, double py, bool atBottomEdge, bool fullscreen)
    {
        var s = App.Settings;
        if (!s.AutoHide)
        {
            _shown = !fullscreen || _hover;
            _revealAt = _leaveAt = -1;
            return;
        }

        if (_shown)
        {
            bool keep = _hover || _dragging || PopupOpen || _dropItem != null
                || (px >= _pillLeft - 12 && px <= _pillLeft + _pillWidth + 12 && py >= M.PillTop - 12 && py <= M.H + 1);
            if (keep) _leaveAt = -1;
            else if (_leaveAt < 0) _leaveAt = now;
            else if (now - _leaveAt >= s.HideDelayMs / 1000.0)
            {
                _shown = false;
                _leaveAt = -1;
            }
        }
        else
        {
            bool trigger = atBottomEdge && !fullscreen && px >= _pillLeft - 40 && px <= _pillLeft + _pillWidth + 40;
            if (!trigger) _revealAt = -1;
            else if (_revealAt < 0) _revealAt = now;
            if (_revealAt >= 0 && now - _revealAt >= s.ShowDelayMs / 1000.0)
            {
                _shown = true;
                _revealAt = -1;
            }
        }
    }

    void UpdateLabel()
    {
        string text = null;
        double cx = 0, top = 0;

        if (_dragging)
        {
            if (_dragOut && _pressItem is AppItem { Pinned: not null } a && a.View.RenderTransform is TranslateTransform tt)
            {
                text = "Remove from Dock";
                cx = a.X + a.Width / 2 + tt.X;
                top = a.IconTop + tt.Y;
            }
        }
        else if (_dropItem != null)
        {
            text = _dropText;
            cx = _dropItem.X + _dropItem.Width / 2;
            top = _dropItem is IconItem ii ? ii.IconTop : M.PillTop;
        }
        else if (MenuOpen)
        {
        }
        else if (_customAnchor != null)
        {
            try
            {
                if (_customAnchor.IsVisible && Root.IsAncestorOf(_customAnchor))
                {
                    var p = _customAnchor.TransformToAncestor(Root).Transform(new Point(_customAnchor.ActualWidth / 2, 0));
                    text = _customText;
                    cx = p.X;
                    top = Math.Min(p.Y, M.PillTop);
                }
                else _customAnchor = null;
            }
            catch { _customAnchor = null; }
        }
        else if (_hoverItem is IconItem hovered)
        {
            text = hovered.Label;
            cx = hovered.X + hovered.Width / 2;
            top = hovered.IconTop;
        }

        if (string.IsNullOrEmpty(text))
        {
            LabelBox.Visibility = Visibility.Collapsed;
            return;
        }
        LabelText.Text = text;
        LabelBox.Visibility = Visibility.Visible;
        LabelBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = LabelBox.DesiredSize;
        Canvas.SetLeft(LabelBox, Math.Clamp(cx - size.Width / 2, 2, Math.Max(2, Width - size.Width - 2)));
        Canvas.SetTop(LabelBox, Math.Max(0, top - 10 - size.Height));
    }

    // --- Mouse -----------------------------------------------------------------------

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left) return;
        var p = e.GetPosition(Root);
        _pressItem = ItemAt(p.X);
        _pressPoint = _dragPoint = p;
        _pressItem?.SetPressed(true);
        Root.CaptureMouse();
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressItem is not AppItem app || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Root);
        if (!_dragging)
        {
            if ((p - _pressPoint).Length < 6) return;
            _dragging = true;
            _apps.Paused = true; // window changes mid-drag would reset the order under you
            app.SetPressed(false);
        }
        _dragPoint = p;
        _dragOut = p.Y < M.PillTop - 70;
        app.View.Opacity = _dragOut ? 0.45 : 1;
        StartAnimating();
    }

    /// <summary>Runs every frame while dragging; reordering happens here, not on mouse moves.</summary>
    void UpdateDrag()
    {
        // The mouse-up can be lost (e.g. if capture is taken away); never stay stuck in a drag.
        if (!Native.IsPrimaryButtonDown())
        {
            Dispatcher.BeginInvoke(EndDrag); // not mid-frame: ending a drag can rebuild the item list
            return;
        }
        if (!_hover || _dragOut || _pressItem is not AppItem app) return;

        // _lastU is the cursor's position along the un-magnified dock, where every app slot is exactly
        // ItemW wide, so the slot under the cursor doesn't shift as icons magnify or swap places.
        int count = _apps.Items.Count, index = _apps.Items.IndexOf(app);
        int target = Math.Clamp((int)(_lastU / M.ItemW), 0, count - 1);
        if (index < 0 || target == index) return;
        _apps.Move(app, target);
        for (int i = 0; i < count; i++) _items[i] = _apps.Items[i];
    }

    void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging || _pressItem == null) return; // a drag is finished by UpdateDrag
        _pressItem.SetPressed(false);
        _pressItem = null;
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        var p = e.GetPosition(Root);
        var item = ItemAt(p.X);
        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                if (_dragging)
                {
                    EndDrag();
                    break;
                }
                var pressed = _pressItem;
                _pressItem = null;
                pressed?.SetPressed(false);
                if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
                if (pressed != null && pressed == item) pressed.OnClick(e);
                break;
            case MouseButton.Right:
                ShowMenu(item);
                break;
            case MouseButton.Middle:
                item?.OnMiddleClick();
                break;
        }
        e.Handled = true;
    }

    void EndDrag()
    {
        if (!_dragging) return;
        var app = _pressItem as AppItem;
        bool remove = _dragOut;
        _dragging = _dragOut = false;
        _pressItem = null;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        _apps.Paused = false;
        if (app != null)
        {
            app.View.RenderTransform = Transform.Identity;
            app.View.Opacity = 1;
            if (remove && app.Pinned != null) _apps.Unpin(app);
            else if (remove) _apps.Rebuild(); // a running app dragged away just goes back
            else _apps.CommitOrder(app);
        }
        StartAnimating();
    }

    void ShowMenu(DockItem item)
    {
        var menu = item?.BuildMenu();
        if (menu != null)
        {
            menu.PlacementTarget = item.MenuAnchor;
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = Ui.Above;
        }
        else
        {
            menu = BuildDockMenu();
            menu.Placement = PlacementMode.MousePoint;
        }
        menu.Opened += (_, _) => StartAnimating();
        menu.Closed += (_, _) => StartAnimating();
        _menu = menu;
        menu.IsOpen = true;
    }

    ContextMenu BuildDockMenu()
    {
        var s = App.Settings;
        var m = new ContextMenu();
        m.Items.Add(Ui.Item(s.AutoHide ? "Turn Hiding Off" : "Turn Hiding On", () => { s.AutoHide = !s.AutoHide; ApplySettings(); }));
        m.Items.Add(Ui.Item(s.Magnification ? "Turn Magnification Off" : "Turn Magnification On", () => { s.Magnification = !s.Magnification; ApplySettings(); }));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Icon Size", null, enabled: false));
        foreach (var (name, size) in new[] { ("Small", 36), ("Medium", 43), ("Large", 54), ("Extra Large", 64) })
        {
            m.Items.Add(Ui.Item("   " + name, () =>
            {
                s.MagnifiedSize = (int)Math.Round(size * (double)s.MagnifiedSize / s.IconSize);
                s.IconSize = size;
                ApplySettings();
            }, s.IconSize == size));
        }
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Hide Windows Taskbar", () =>
        {
            s.HideWindowsTaskbar = !s.HideWindowsTaskbar;
            App.SetTaskbarHidden(s.HideWindowsTaskbar);
            s.Save();
        }, s.HideWindowsTaskbar));
        m.Items.Add(Ui.Item("Start at Login", () => DockSettings.StartAtLogin = !DockSettings.StartAtLogin, DockSettings.StartAtLogin));
        if (s.HiddenApps.Count > 0)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Item("Hidden Apps (click to show again)", null, enabled: false));
            foreach (var key in s.HiddenApps.ToList())
                m.Items.Add(Ui.Item("   " + key, () => _apps.Unhide(key)));
        }
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Edit Settings File…", () => Ui.Open("notepad.exe", $"\"{DockSettings.FilePath}\"")));
        m.Items.Add(Ui.Item("Open Custom Icons Folder", () => Ui.Open(DockSettings.IconsDir)));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Restart Dock", App.Restart));
        m.Items.Add(Ui.Item("Quit Dock", App.Quit));
        return m;
    }

    void ApplySettings()
    {
        App.Settings.Save();
        M = new DockMetrics(App.Settings);
        Relayout();
    }

    void UpdateAppBar()
    {
        if (_hwnd == IntPtr.Zero) return;
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
        if (App.Settings.AutoHide)
        {
            if (_appBarRegistered) Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
            _appBarRegistered = false;
            return;
        }
        // Dock always visible: reserve its height so maximized windows stop above it.
        if (!_appBarRegistered) Native.SHAppBarMessage(Native.ABM_NEW, ref abd);
        _appBarRegistered = true;
        int sw = (int)Math.Round(_screenW * Dpi), sh = (int)Math.Round(_screenH * Dpi);
        int h = (int)Math.Ceiling((M.PillH + M.BottomGap) * Dpi);
        abd.uEdge = Native.ABE_BOTTOM;
        abd.rc = new Native.RECT { Left = 0, Top = sh - h, Right = sw, Bottom = sh };
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - h;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
    }

    // --- Files dragged from Explorer ---------------------------------------------------

    void OnDragOver(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Root);
        var item = p.Y >= M.PillTop - (M.Mag - M.S) - 10 ? ItemAt(p.X) : null;
        var effect = item?.DropEffect(e.Data) ?? DragDropEffects.None;
        e.Effects = effect;
        _dropItem = effect != DragDropEffects.None ? item : null;
        _dropText = _dropItem?.DropLabel(e.Data);
        e.Handled = true;
        StartAnimating();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var item = ItemAt(e.GetPosition(Root).X);
        if (item != null && item.DropEffect(e.Data) != DragDropEffects.None) item.Drop(e.Data);
        _dropItem = null;
        e.Handled = true;
        StartAnimating();
    }
}
