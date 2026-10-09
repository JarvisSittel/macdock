using System.Runtime.InteropServices;
using ManagedShell.Common.Helpers;

namespace MacDock;

/// <summary>
/// The blur behind the dock. Blur applies to a whole window, so it lives in its own click-through window the size
/// of the dock, with Windows 11's rounded corners (DWM clips blur to those, but not to custom window regions).
/// The dock window (owned by this one) sits on top and paints the tint and border. It's a plain Win32 window
/// because WPF's transparent windows can't take DWM corner rounding.
/// </summary>
public sealed class BlurWindow : IDisposable
{
    const string ClassName = "MacDockBackdrop";
    static readonly WndProc Proc = DefWindowProc; // kept alive for the lifetime of the class registration
    static bool _registered;

    int _x = int.MinValue, _y, _w, _h;

    public IntPtr Handle { get; }

    public BlurWindow()
    {
        var hInstance = GetModuleHandle(null);
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = hInstance,
                hbrBackground = GetStockObject(4), // BLACK_BRUSH: black has zero alpha, so the blur shows through
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _registered = true;
        }

        const uint WS_POPUP = 0x80000000;
        int exStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
        Handle = CreateWindowEx(exStyle, ClassName, "MacDock Backdrop", WS_POPUP, -200, -200, 10, 10,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        Native.UseRoundedCorners(Handle);
        Native.EnableBlurBehind(Handle);
        WindowHelper.ExcludeWindowFromPeek(Handle);
        ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
    }

    /// <summary>Position, in physical pixels. Corners come from Windows 11's own rounding.</summary>
    public void SetRect(int x, int y, int w, int h)
    {
        if (x == _x && y == _y && w == _w && h == _h) return;
        Native.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        (_x, _y, _w, _h) = (x, y, w, h);
    }

    public void Dispose() => DestroyWindow(Handle);

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
