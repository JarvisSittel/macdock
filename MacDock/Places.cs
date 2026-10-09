using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;

namespace MacDock;

/// <summary>Recycle Bin: shows full/empty, takes dropped files, empties with confirmation.</summary>
public class TrashItem : IconItem
{
    readonly ImageSource _empty, _full;
    readonly DispatcherTimer _timer;
    bool _isFull;

    public TrashItem(DockWindow dock) : base(dock)
    {
        _empty = IconLoader.Override("trash") ?? IconLoader.RecycleBin(false);
        _full = IconLoader.Override("trash-full") ?? IconLoader.RecycleBin(true);
        Icon.Source = _empty;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public override string Label => "Recycle Bin";

    async void Refresh()
    {
        long count = await Task.Run(Native.RecycleBinCount);
        _isFull = count > 0;
        Icon.Source = _isFull ? _full : _empty;
    }

    public override void OnClick(MouseButtonEventArgs e) => Ui.Open("explorer.exe", "shell:RecycleBinFolder");

    public override ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Open", () => Ui.Open("explorer.exe", "shell:RecycleBinFolder")));
        menu.Items.Add(new Separator());
        // Windows shows its own "are you sure" dialog.
        menu.Items.Add(Ui.Item("Empty Recycle Bin…", () => RunSta(() => Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0)), enabled: _isFull));
        return menu;
    }

    public override DragDropEffects DropEffect(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;

    public override string DropLabel(IDataObject data) => "Move to Recycle Bin";

    public override void Drop(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return;
        RunSta(() =>
        {
            foreach (var f in files) ShellHelper.SendToRecycleBin(f);
        });
    }

    /// <summary>Shell file operations show UI, so run them on their own STA thread.</summary>
    void RunSta(Action action)
    {
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { Log.Error("trash op", e); }
            Dock.Dispatcher.BeginInvoke(Refresh);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    public override void Dispose() => _timer.Stop();
}
