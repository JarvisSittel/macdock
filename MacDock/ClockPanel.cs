using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MacDock;

/// <summary>
/// Calendar and notifications panel that opens directly above the clock, in place of Windows' notification
/// centre on the screen edge. Notifications are read through Windows' notification listener.
/// </summary>
sealed class ClockPanel
{
    const double PanelWidth = 320;

    readonly Popup _popup;
    readonly TextBlock _todayText = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10) };
    readonly TextBlock _monthTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly UniformGrid _weekdays = new() { Columns = 7, Margin = new Thickness(0, 6, 0, 2) };
    readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    readonly StackPanel _list = new();
    readonly TextBlock _empty = new() { Text = "No new notifications", Foreground = Ui.SecondaryBrush, Margin = new Thickness(2, 4, 0, 4) };
    readonly FrameworkElement _clearAll;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Dictionary<string, ImageSource> _logos = new();
    DateTime _month;
    string _signature;

    static UserNotificationListener Listener => UserNotificationListener.Current;

    public ClockPanel(DockWindow dock, FrameworkElement anchor)
    {
        // --- Calendar ---
        var prev = ChevronButton(Glyphs.ChevronLeft, () => ShowMonth(_month.AddMonths(-1)));
        var next = ChevronButton(Glyphs.ChevronRight, () => ShowMonth(_month.AddMonths(1)));
        var title = Ui.Chip(_monthTitle, new Thickness(6, 4, 6, 4));
        title.MouseLeftButtonUp += (_, _) => ShowMonth(DateTime.Today); // back to this month
        var monthRow = new DockPanel();
        DockPanel.SetDock(next, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(prev, System.Windows.Controls.Dock.Right);
        monthRow.Children.Add(next);
        monthRow.Children.Add(prev);
        monthRow.Children.Add(title);
        title.HorizontalAlignment = HorizontalAlignment.Left;

        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)culture.FirstDayOfWeek + i) % 7);
            _weekdays.Children.Add(new TextBlock
            {
                Text = culture.GetShortestDayName(day),
                FontSize = 11,
                Foreground = Ui.SecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        // --- Notifications ---
        var clear = Link("Clear all", ClearAll);
        _clearAll = clear;
        var notifHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(clear, System.Windows.Controls.Dock.Right);
        notifHeader.Children.Add(clear);
        notifHeader.Children.Add(Heading("Notifications"));

        var scroll = new ScrollViewer
        {
            Content = _list,
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // still scrolls with the wheel
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var footer = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        footer.Children.Add(Link("Notification settings", () => { _popup.IsOpen = false; Ui.Open("ms-settings:notifications"); }));
        footer.Children.Add(Link("Open in Windows", () => { _popup.IsOpen = false; ShellHelper.ShowNotificationCenter(); }));

        var root = new StackPanel { Width = PanelWidth };
        root.Children.Add(_todayText);
        root.Children.Add(monthRow);
        root.Children.Add(_weekdays);
        root.Children.Add(_days);
        root.Children.Add(Divider());
        root.Children.Add(notifHeader);
        root.Children.Add(_empty);
        root.Children.Add(scroll);
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
        _popup.Closed += (_, _) => _poll.Stop();
        dock.RegisterPopup(_popup, anchor);

        _poll.Tick += (_, _) => RefreshNotifications();
    }

    public bool IsOpen => _popup.IsOpen;

    public void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }
        _todayText.Text = DateTime.Now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
        ShowMonth(DateTime.Today);
        _signature = null;
        RefreshNotifications();
        _poll.Start();
        _popup.IsOpen = true;
    }

    // --- Calendar ---------------------------------------------------------------------

    void ShowMonth(DateTime anyDay)
    {
        _month = new DateTime(anyDay.Year, anyDay.Month, 1);
        _monthTitle.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _days.Children.Clear();

        var firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int offset = ((int)_month.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = _month.AddDays(-offset);
        var today = DateTime.Today;
        var accent = Ui.AccentBrush;
        var onAccent = IsLight(accent) ? Brushes.Black : Brushes.White;

        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            bool isToday = date == today, inMonth = date.Month == _month.Month;
            var text = new TextBlock
            {
                Text = date.Day.ToString(CultureInfo.CurrentCulture),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday ? onAccent : inMonth ? Brushes.White : Ui.Brush("#5D5D5D"),
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            };
            _days.Children.Add(new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = isToday ? accent : Brushes.Transparent,
                Child = text,
            });
        }
    }

    static bool IsLight(Brush brush)
    {
        if (brush is not SolidColorBrush b) return false;
        var c = b.Color;
        return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150;
    }

    // --- Notifications ------------------------------------------------------------------

    async void RefreshNotifications()
    {
        IReadOnlyList<UserNotification> notifications;
        try
        {
            if (Listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                ShowAccessMessage();
                return;
            }
            notifications = await Listener.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception e)
        {
            Log.Error("read notifications", e);
            return;
        }

        var items = notifications.OrderByDescending(n => n.CreationTime).ToList();
        string signature = string.Join(",", items.Select(n => n.Id));
        if (signature == _signature) return;
        _signature = signature;

        _list.Children.Clear();
        foreach (var n in items) _list.Children.Add(Card(n));
        _empty.Text = "No new notifications";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clearAll.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowAccessMessage()
    {
        _signature = null;
        _list.Children.Clear();
        _empty.Text = "Turn on notification access for apps in Windows Settings › Privacy & security › Notifications.";
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Visibility = Visibility.Visible;
        _clearAll.Visibility = Visibility.Collapsed;
    }

    FrameworkElement Card(UserNotification n)
    {
        string appName = null, appId = null;
        try
        {
            appName = n.AppInfo?.DisplayInfo?.DisplayName;
            appId = n.AppInfo?.AppUserModelId;
        }
        catch { }

        var texts = new List<string>();
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null) texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }
        catch { }

        var logo = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        LoadLogo(n, appId, logo);

        var close = Ui.Chip(Glyphs.Box(10, Glyphs.Stroke(Glyphs.Close, 2.4, 0.8)), new Thickness(4));
        close.Visibility = Visibility.Hidden;
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { Listener.RemoveNotification(n.Id); } catch (Exception ex) { Log.Error("dismiss notification", ex); }
            _signature = null;
            RefreshNotifications();
        };

        var header = new DockPanel();
        DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
        var when = new TextBlock { Text = TimeAgo(n.CreationTime), FontSize = 11, Foreground = Ui.SecondaryBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        DockPanel.SetDock(when, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(logo, System.Windows.Controls.Dock.Left);
        header.Children.Add(close);
        header.Children.Add(when);
        header.Children.Add(logo);
        header.Children.Add(new TextBlock { Text = appName ?? "Notification", FontSize = 11, Foreground = Ui.SecondaryBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var body = new StackPanel();
        body.Children.Add(header);
        if (texts.Count > 0)
            body.Children.Add(new TextBlock { Text = texts[0], FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
        if (texts.Count > 1)
            body.Children.Add(new TextBlock { Text = string.Join("\n", texts.Skip(1)), FontSize = 12, Foreground = Ui.Brush("#CFCFCF"), TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });

        var card = new Border
        {
            Background = Ui.Brush("#2B2B2B"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 6, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = body,
            Cursor = appId != null ? Cursors.Hand : null,
        };
        card.MouseEnter += (_, _) => { card.Background = Ui.Brush("#323232"); close.Visibility = Visibility.Visible; };
        card.MouseLeave += (_, _) => { card.Background = Ui.Brush("#2B2B2B"); close.Visibility = Visibility.Hidden; };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (appId == null) return;
            _popup.IsOpen = false;
            Ui.Open("explorer.exe", "shell:AppsFolder\\" + appId);
        };
        return card;
    }

    async void LoadLogo(UserNotification n, string appId, Image target)
    {
        if (appId != null && _logos.TryGetValue(appId, out var cached))
        {
            target.Source = cached;
            return;
        }
        try
        {
            var reference = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
            using var stream = await reference.OpenReadAsync();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream.AsStreamForRead();
            bmp.EndInit();
            bmp.Freeze();
            if (appId != null) _logos[appId] = bmp;
            target.Source = bmp;
        }
        catch { } // some apps have no logo; the card just goes without
    }

    void ClearAll()
    {
        try { Listener.ClearNotifications(); } catch (Exception e) { Log.Error("clear notifications", e); }
        _signature = null;
        RefreshNotifications();
    }

    static string TimeAgo(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
        if (age.TotalDays < 7) return time.LocalDateTime.ToString("ddd", CultureInfo.CurrentCulture);
        return time.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture);
    }

    // --- Shared bits (same look as the sound panel) -------------------------------------

    static FrameworkElement ChevronButton(Geometry glyph, Action onClick)
    {
        var chip = Ui.Chip(Glyphs.Box(14, Glyphs.Stroke(glyph, 2, 0.85)), new Thickness(5));
        chip.MouseLeftButtonUp += (_, _) => onClick();
        return chip;
    }

    static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Ui.SecondaryBrush,
        Margin = new Thickness(2, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Border Divider() => new() { Height = 1, Background = Ui.Brush("#3C3C3C"), Margin = new Thickness(0, 10, 0, 10) };

    static FrameworkElement Link(string text, Action onClick)
    {
        var chip = Ui.Chip(new TextBlock { Text = text, FontSize = 12, Foreground = Ui.AccentBrush }, new Thickness(6, 4, 6, 4));
        chip.MouseLeftButtonUp += (_, _) => onClick();
        return chip;
    }
}
