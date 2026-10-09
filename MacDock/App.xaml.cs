using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.Common.Enums;

namespace MacDock;

public partial class App : Application
{
    public static ShellManager Shell { get; private set; }
    public static DockSettings Settings { get; private set; }

    const string QuitEventName = "MacDock.Quit";
    static Mutex _instanceMutex;
    static bool _taskbarHidden;
    DockWindow _dock;
    BlurWindow _blur;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, "MacDock.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // `MacDock --quit` asks the running dock to exit cleanly (restoring the taskbar).
            if (e.Args.Contains("--quit") && EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
                quit.Set();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        if (e.Args.Contains("--quit"))
        {
            Shutdown();
            return;
        }
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        new Thread(() =>
        {
            quitEvent.WaitOne();
            Dispatcher.BeginInvoke(Quit);
        }) { IsBackground = true }.Start();

        Log.Start();
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            Log.Error("unhandled", a.ExceptionObject as Exception);
            RestoreTaskbar();
        };
        SessionEnding += (_, _) => RestoreTaskbar();

        Settings = DockSettings.Load();

        var config = ShellManager.DefaultShellConfig;
        config.EnableTasksService = true;
        config.AutoStartTasksService = true;
        config.TaskIconSize = IconSize.Large;
        config.EnableTrayService = Settings.ShowTray;
        config.AutoStartTrayService = Settings.ShowTray;
        Shell = new ShellManager(config);
        Shell.Tasks.Initialize(false);

        if (Settings.HideWindowsTaskbar && !e.Args.Contains("--keep-taskbar"))
        {
            Shell.ExplorerHelper.HideExplorerTaskbar = true;
            _taskbarHidden = true;
        }

        if (Settings.Blur) _blur = new BlurWindow();
        _dock = new DockWindow(_blur);
        // Owned windows always stay above their owner, so the dock never slips under its own blur.
        if (_blur != null) new WindowInteropHelper(_dock).Owner = _blur.Handle;
        _dock.Show();
        Log.Write("started");
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the dock alive through bugs in individual features; the log has the details.
        Log.Error("dispatcher", e.Exception);
        e.Handled = true;
    }

    public static void RestoreTaskbar()
    {
        if (!_taskbarHidden) return;
        try { Shell.ExplorerHelper.HideExplorerTaskbar = false; } catch { }
        _taskbarHidden = false;
    }

    public static void SetTaskbarHidden(bool hide)
    {
        if (hide == _taskbarHidden) return;
        Shell.ExplorerHelper.HideExplorerTaskbar = hide;
        _taskbarHidden = hide;
    }

    public static void Quit()
    {
        Current.Shutdown();
    }

    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        Current.Exit += (_, _) => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _dock?.Shutdown();
            _blur?.Dispose();
            RestoreTaskbar();
            Shell?.Dispose();
        }
        catch (Exception ex) { Log.Error("exit", ex); }
        _instanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
