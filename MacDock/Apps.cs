using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using IOPath = System.IO.Path;

namespace MacDock;

/// <summary>A pinned app, a running app, or both.</summary>
public class AppItem : IconItem
{
    readonly AppTracker _tracker;
    readonly Image _badge = new() { Visibility = Visibility.Collapsed };
    readonly Border _progressTrack = new() { Background = Ui.Brush("#99000000"), CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed };
    readonly Border _progressFill = new() { Background = Ui.Brush("#FF0A84FF"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    double _progress;
    int _lastWindowCount;
    string _name;

    public PinnedApp Pinned { get; set; }
    public ShellLink.Info Link { get; private set; }
    public string GroupKey { get; set; }
    public List<ApplicationWindow> Windows { get; } = new();

    public AppItem(DockWindow dock, AppTracker tracker, PinnedApp pinned, string groupKey) : base(dock)
    {
        _tracker = tracker;
        Pinned = pinned;
        GroupKey = groupKey;
        if (pinned?.Path != null && pinned.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            Link = ShellLink.Read(pinned.Path);
        _progressTrack.Child = _progressFill;
        RenderOptions.SetBitmapScalingMode(_badge, BitmapScalingMode.HighQuality);
        View.Children.Add(_progressTrack);
        View.Children.Add(_badge);
    }

    public bool IsRunning => Windows.Count > 0;
    public override string Label => _name;

    /// <summary>The AppUserModelID this item claims; windows with it always belong here.</summary>
    public string MatchAppId => Pinned?.AppId ?? Link?.AppId;

    /// <summary>The exe this item launches; windows of it belong here when no AppUserModelID says otherwise.</summary>
    public string MatchExe
    {
        get
        {
            if (Link?.TargetPath != null) return Link.TargetPath;
            var p = Pinned?.Path;
            return p != null && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    string ExePath => MatchExe ?? Windows.FirstOrDefault()?.WinFileName;

    /// <summary>What "Hide from Dock" records: the exe name, or the AppUserModelID for Store apps.</summary>
    public string HideKey
    {
        get
        {
            var exe = ExePath;
            if (exe != null) return IOPath.GetFileNameWithoutExtension(exe);
            return MatchAppId ?? Windows.FirstOrDefault()?.AppUserModelID;
        }
    }

    public void RefreshIdentity()
    {
        var w = Windows.FirstOrDefault();
        string exe = ExePath;
        string appId = MatchAppId ?? (w?.IsUWP == true ? w.AppUserModelID : null);

        _name = Pinned?.Name
            ?? (w?.IsUWP == true ? IconLoader.StoreAppName(w.AppUserModelID) : null)
            ?? NullIfEmpty(w?.WinFileDescription)
            ?? (exe != null ? IOPath.GetFileNameWithoutExtension(exe) : null)
            ?? w?.Title;

        ImageSource icon = IconLoader.Override(exe != null ? IOPath.GetFileNameWithoutExtension(exe) : null, Pinned?.Name, appId);
        if (icon == null)
        {
            if (Pinned?.Path != null) icon = FileIcon(Packaged.Current(Pinned.Path));
            else if (appId != null) icon = IconLoader.ForAppId(appId);
            else if (exe != null) icon = FileIcon(exe);
        }
        Icon.Source = icon ?? w?.Icon;
    }

    /// <summary>Packaged apps (e.g. Arc) use their package logo; their exe may have no icon at all.</summary>
    static ImageSource FileIcon(string path) => IconLoader.ForAppId(Packaged.AppIdForExe(path)) ?? IconLoader.ForPath(path);

    public void RefreshState()
    {
        SetIndicator(IsRunning, Windows.Any(w => w.State == ApplicationWindow.WindowState.Active));

        if (Windows.Count > _lastWindowCount) Dock.StopBounce(this);
        _lastWindowCount = Windows.Count;

        bool attention = Windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);
        if (attention != Attention) Dock.SetAttention(this, attention);

        var pw = Windows.FirstOrDefault(w => (int)w.ProgressState != 0);
        if (pw != null)
        {
            int state = (int)pw.ProgressState; // 1 indeterminate, 2 normal, 4 error, 8 paused
            _progress = state == 1 ? 1 : Math.Clamp(pw.ProgressValue / 100.0, 0.02, 1);
            _progressFill.Background = Ui.Brush(state == 4 ? "#FFFF453A" : state == 8 ? "#FFFFD60A" : "#FF0A84FF");
            _progressTrack.Visibility = Visibility.Visible;
        }
        else _progressTrack.Visibility = Visibility.Collapsed;

        var overlay = Windows.Select(w => w.OverlayIcon).FirstOrDefault(i => i != null);
        _badge.Source = overlay;
        _badge.Visibility = overlay != null ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void LayoutExtras(DockMetrics m, double width, double size, double top)
    {
        double left = (width - size) / 2;
        if (_badge.Visibility == Visibility.Visible)
        {
            double b = size * 0.42;
            _badge.Width = _badge.Height = b;
            Canvas.SetLeft(_badge, left + size - b * 0.85);
            Canvas.SetTop(_badge, top - b * 0.15);
        }
        if (_progressTrack.Visibility == Visibility.Visible)
        {
            double w = size * 0.7;
            _progressTrack.Width = w;
            _progressTrack.Height = Math.Max(4, size * 0.08);
            _progressFill.Width = w * _progress;
            Canvas.SetLeft(_progressTrack, left + (size - w) / 2);
            Canvas.SetTop(_progressTrack, top + size * 0.82);
        }
    }

    // --- Actions ------------------------------------------------------------------

    public override void OnClick(MouseButtonEventArgs e)
    {
        if (!IsRunning || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Launch();
            return;
        }

        var wins = _tracker.ByRecency(Windows);
        var fg = Native.GetForegroundWindow();
        var active = wins.FirstOrDefault(w => w.Handle == fg);
        if (active == null)
            Focus(wins[0]);
        else if (wins.Count == 1)
        {
            if (active.CanMinimize) active.Minimize();
        }
        else
            Focus(wins[^1]); // cycle through the app's windows
    }

    public override void OnMiddleClick() => Launch();

    static void Focus(ApplicationWindow w)
    {
        if (w.IsMinimized) w.Restore();
        w.BringToFront();
    }

    public void Launch(string extraArgs = null)
    {
        Dock.StartBounce(this, 6);
        try
        {
            if (Pinned?.Path != null)
                Start(Packaged.Current(Pinned.Path), string.Join(" ", new[] { Pinned.Args, extraArgs }.Where(a => !string.IsNullOrEmpty(a))));
            else if (Pinned?.AppId != null)
                ShellHelper.ActivateApplication(Pinned.AppId, extraArgs ?? "");
            else
            {
                var w = Windows.FirstOrDefault();
                if (w?.IsUWP == true) ShellHelper.ActivateApplication(w.AppUserModelID, extraArgs ?? "");
                else if (w?.WinFileName != null) Start(w.WinFileName, extraArgs);
                else Dock.StopBounce(this);
            }
        }
        catch (Exception e)
        {
            Log.Error("launch " + _name, e);
            Dock.StopBounce(this);
        }
    }

    static void Start(string path, string args)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true, Arguments = args ?? "" };
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) psi.WorkingDirectory = IOPath.GetDirectoryName(path);
        Process.Start(psi);
    }

    public override ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        if (IsRunning)
        {
            var fg = Native.GetForegroundWindow();
            foreach (var w in Windows)
            {
                var title = string.IsNullOrWhiteSpace(w.Title) ? _name : w.Title;
                if (title.Length > 60) title = title[..57] + "…";
                var target = w;
                menu.Items.Add(Ui.Item(title, () => Focus(target), w.Handle == fg));
            }
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Ui.Item("Keep in Dock", () =>
        {
            if (Pinned != null) _tracker.Unpin(this);
            else _tracker.Pin(this);
        }, Pinned != null));
        if (HideKey != null)
            menu.Items.Add(Ui.Item("Hide from Dock", () => _tracker.Hide(this)));

        var exe = ExePath;
        if (exe != null && File.Exists(exe))
            menu.Items.Add(Ui.Item("Show in File Explorer", () => Ui.RevealInExplorer(Pinned?.Path ?? exe)));
        menu.Items.Add(Ui.Item(IsRunning ? "New Window" : "Open", () => Launch()));

        if (IsRunning)
        {
            menu.Items.Add(new Separator());
            var wins = Windows.ToList();
            menu.Items.Add(Ui.Item(wins.Count > 1 ? $"Quit ({wins.Count} windows)" : "Quit", () =>
            {
                foreach (var w in wins) w.Close();
            }));
        }
        return menu;
    }

    public override DragDropEffects DropEffect(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    public override string DropLabel(IDataObject data) => $"Open with {_name}";

    public override void Drop(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            Launch(string.Join(" ", files.Select(f => $"\"{f}\"")));
    }

    static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Merges pinned apps with the windows ManagedShell reports, in dock order.</summary>
public class AppTracker
{
    readonly DockWindow _dock;
    readonly ICollectionView _view;
    readonly Dictionary<PinnedApp, AppItem> _pinnedItems = new();
    readonly Dictionary<string, AppItem> _groupItems = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _groupOrder = new();
    readonly Dictionary<ApplicationWindow, ApplicationWindow.GetButtonRectEventHandler> _subscribed = new();
    readonly Dictionary<IntPtr, AppItem> _owner = new();
    readonly Dictionary<IntPtr, long> _recency = new();
    long _tick;
    bool _rebuildPending, _paused, _rebuildWhilePaused;

    public List<AppItem> Items { get; private set; } = new();
    public event Action StructureChanged;

    public AppTracker(DockWindow dock)
    {
        _dock = dock;
        _view = App.Shell.Tasks.GroupedWindows;
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => ScheduleRebuild();
        App.Shell.TasksService.WindowActivated += (_, e) =>
        {
            if (e.Window != null) _recency[e.Window.Handle] = ++_tick;
            foreach (var item in Items) item.RefreshState();
        };
        App.Shell.TasksService.DesktopActivated += (_, _) =>
        {
            foreach (var item in Items) item.RefreshState();
        };
        Rebuild();
    }

    public List<ApplicationWindow> ByRecency(IEnumerable<ApplicationWindow> windows) =>
        windows.OrderByDescending(w => _recency.TryGetValue(w.Handle, out var t) ? t : 0).ToList();

    /// <summary>While paused (during a drag), window changes are queued instead of reshuffling the dock.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value && _rebuildWhilePaused)
            {
                _rebuildWhilePaused = false;
                ScheduleRebuild();
            }
        }
    }

    void ScheduleRebuild()
    {
        if (_paused)
        {
            _rebuildWhilePaused = true;
            return;
        }
        if (_rebuildPending) return;
        _rebuildPending = true;
        _dock.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _rebuildPending = false;
            Rebuild();
        });
    }

    public void Rebuild()
    {
        var windows = _view.Cast<ApplicationWindow>().Where(w => w.ShowInTaskbar && !IsHidden(w)).ToList();
        SyncSubscriptions(windows);

        var pinnedItems = new List<AppItem>();
        foreach (var p in App.Settings.Pinned)
        {
            if (!_pinnedItems.TryGetValue(p, out var item))
                _pinnedItems[p] = item = new AppItem(_dock, this, p, null);
            pinnedItems.Add(item);
        }
        foreach (var stale in _pinnedItems.Keys.Where(p => !App.Settings.Pinned.Contains(p)).ToList())
            _pinnedItems.Remove(stale);

        foreach (var item in pinnedItems) item.Windows.Clear();
        foreach (var item in _groupItems.Values) item.Windows.Clear();

        var groups = new Dictionary<string, List<ApplicationWindow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
        {
            var owner = MatchPinned(w, pinnedItems);
            if (owner != null) { owner.Windows.Add(w); continue; }
            var key = GroupKey(w);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(w);
        }

        foreach (var key in _groupOrder.Where(k => !groups.ContainsKey(k)).ToList())
        {
            _groupItems.Remove(key);
            _groupOrder.Remove(key);
        }
        foreach (var (key, list) in groups)
        {
            if (!_groupItems.TryGetValue(key, out var item))
            {
                _groupItems[key] = item = new AppItem(_dock, this, null, key);
                _groupOrder.Add(key);
            }
            item.Windows.AddRange(list);
        }

        var newItems = pinnedItems.Concat(_groupOrder.Select(k => _groupItems[k])).ToList();
        _owner.Clear();
        foreach (var item in newItems)
        {
            foreach (var w in item.Windows) _owner[w.Handle] = item;
            item.RefreshIdentity();
            item.RefreshState();
        }

        bool changed = !newItems.SequenceEqual(Items);
        Items = newItems;
        if (changed) StructureChanged?.Invoke();
    }

    void SyncSubscriptions(List<ApplicationWindow> windows)
    {
        foreach (var gone in _subscribed.Keys.Except(windows).ToList())
        {
            gone.PropertyChanged -= OnWindowChanged;
            gone.GetButtonRect -= _subscribed[gone];
            _subscribed.Remove(gone);
            _recency.Remove(gone.Handle);
        }
        foreach (var w in windows.Where(w => !_subscribed.ContainsKey(w)))
        {
            var win = w;
            // Tells Windows where to animate minimize/restore: the dock icon.
            ApplicationWindow.GetButtonRectEventHandler handler = (ref NativeMethods.ShortRect rect) =>
            {
                if (_owner.TryGetValue(win.Handle, out var item) && _dock.ItemScreenRect(item) is { } r)
                    rect = new NativeMethods.ShortRect((short)r.Left, (short)r.Top, (short)r.Right, (short)r.Bottom);
            };
            w.PropertyChanged += OnWindowChanged;
            w.GetButtonRect += handler;
            _subscribed[w] = handler;
        }
    }

    void OnWindowChanged(object sender, PropertyChangedEventArgs e)
    {
        var w = (ApplicationWindow)sender;
        switch (e.PropertyName)
        {
            case nameof(ApplicationWindow.ShowInTaskbar):
            case nameof(ApplicationWindow.WinFileName):
            case nameof(ApplicationWindow.AppUserModelID):
                ScheduleRebuild();
                break;
            default:
                if (_owner.TryGetValue(w.Handle, out var item)) item.RefreshState();
                break;
        }
    }

    static bool LooksLikeAppId(string id) => !string.IsNullOrEmpty(id) && !id.Contains('\\');

    /// <summary>
    /// Apps the user chose to hide. Needed because some apps (e.g. Overwolf/CurseForge) remove themselves from the
    /// Windows taskbar via ITaskbarList, which only Explorer's taskbar is told about.
    /// </summary>
    static bool IsHidden(ApplicationWindow w)
    {
        var hidden = App.Settings.HiddenApps;
        if (hidden.Count == 0) return false;
        string exe = w.WinFileName != null ? IOPath.GetFileNameWithoutExtension(w.WinFileName) : null;
        return hidden.Any(h => string.Equals(h, exe, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(h, w.AppUserModelID, StringComparison.OrdinalIgnoreCase));
    }

    public void Hide(AppItem item)
    {
        var key = item.HideKey;
        if (key == null) return;
        if (item.Pinned != null)
        {
            App.Settings.Pinned.Remove(item.Pinned);
            _pinnedItems.Remove(item.Pinned);
        }
        if (!App.Settings.HiddenApps.Contains(key, StringComparer.OrdinalIgnoreCase)) App.Settings.HiddenApps.Add(key);
        App.Settings.Save();
        Rebuild();
    }

    public void Unhide(string key)
    {
        App.Settings.HiddenApps.RemoveAll(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase));
        App.Settings.Save();
        Rebuild();
    }

    static readonly string ExplorerExe = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// The window's AppUserModelID, filling in what Windows assigns internally but doesn't report: File Explorer
    /// windows belong to "Microsoft.Windows.Explorer" (the ID on the pinned File Explorer shortcut, which has no exe path).
    /// </summary>
    static string AppIdOf(ApplicationWindow w)
    {
        if (LooksLikeAppId(w.AppUserModelID)) return w.AppUserModelID;
        if (string.Equals(w.WinFileName, ExplorerExe, StringComparison.OrdinalIgnoreCase)) return "Microsoft.Windows.Explorer";
        return w.AppUserModelID;
    }

    static string GroupKey(ApplicationWindow w) =>
        LooksLikeAppId(AppIdOf(w)) ? AppIdOf(w) : (w.WinFileName ?? w.Handle.ToString());

    static AppItem MatchPinned(ApplicationWindow w, List<AppItem> pinned)
    {
        string aumid = AppIdOf(w), exe = w.WinFileName;
        foreach (var p in pinned)
            if (p.MatchAppId != null && string.Equals(p.MatchAppId, aumid, StringComparison.OrdinalIgnoreCase))
                return p;

        // Chrome/Edge web apps and Store apps share an exe with their host but have their own identity.
        bool subApp = aumid != null && (aumid.Contains("_crx_") || aumid.Contains('!'));
        if (subApp || exe == null) return null;
        foreach (var p in pinned)
            if ((string.Equals(p.MatchExe, exe, StringComparison.OrdinalIgnoreCase) || Packaged.SameApp(p.MatchExe, exe) || LaunchedVia(p, exe))
                && (p.MatchAppId == null || !LooksLikeAppId(aumid)))
                return p;
        return null;
    }

    /// <summary>
    /// Shortcuts that start an app through a launcher, e.g. Discord's "Update.exe --processStart Discord.exe",
    /// which runs Discord\app-1.0.x\Discord.exe. The window belongs to the pin if its exe lives under the launcher's
    /// folder and the shortcut's arguments name it. (Windows matches these by an app ID such apps set on their
    /// process, which other programs can't read.)
    /// </summary>
    static bool LaunchedVia(AppItem pin, string exe)
    {
        var link = pin.Link;
        if (link?.TargetPath == null || string.IsNullOrEmpty(link.Arguments)) return false;
        var launcherDir = IOPath.GetDirectoryName(link.TargetPath);
        return launcherDir != null
            && exe.StartsWith(launcherDir + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && link.Arguments.Contains(IOPath.GetFileName(exe), StringComparison.OrdinalIgnoreCase);
    }

    // --- Pinning and ordering ------------------------------------------------------

    PinnedApp CreatePin(AppItem item)
    {
        var w = item.Windows.FirstOrDefault();
        if (w == null) return null;
        var p = new PinnedApp { Name = item.Label };
        if (w.IsUWP) p.AppId = w.AppUserModelID;
        else
        {
            p.Path = w.WinFileName;
            if (LooksLikeAppId(w.AppUserModelID)) p.AppId = w.AppUserModelID;
        }
        return p;
    }

    void MakePinned(AppItem item, PinnedApp p)
    {
        _groupItems.Remove(item.GroupKey);
        _groupOrder.Remove(item.GroupKey);
        item.GroupKey = null;
        item.Pinned = p;
        _pinnedItems[p] = item;
    }

    public void Pin(AppItem item)
    {
        if (item.Pinned != null) return;
        var p = CreatePin(item);
        if (p == null) return;
        MakePinned(item, p);
        App.Settings.Pinned.Add(p);
        App.Settings.Save();
        Rebuild();
    }

    public void Unpin(AppItem item)
    {
        if (item.Pinned == null) return;
        App.Settings.Pinned.Remove(item.Pinned);
        _pinnedItems.Remove(item.Pinned);
        App.Settings.Save();
        Rebuild();
    }

    /// <summary>Live reorder while dragging; the dock re-reads <see cref="Items"/> itself. Saved by <see cref="CommitOrder"/>.</summary>
    public void Move(AppItem item, int index)
    {
        var list = Items.ToList();
        list.Remove(item);
        list.Insert(Math.Clamp(index, 0, list.Count), item);
        Items = list;
    }

    /// <summary>
    /// Saves the dragged order. A running app dropped among the pinned ones is kept in the Dock;
    /// dropped anywhere else it just returns to its place.
    /// </summary>
    public void CommitOrder(AppItem dragged)
    {
        int pinnedCount = Items.Count(i => i.Pinned != null && i != dragged);
        if (dragged.Pinned == null && Items.IndexOf(dragged) < pinnedCount && CreatePin(dragged) is { } p)
            MakePinned(dragged, p);
        App.Settings.Pinned = Items.Where(i => i.Pinned != null).Select(i => i.Pinned).ToList();
        App.Settings.Save();
        Rebuild();
    }
}
