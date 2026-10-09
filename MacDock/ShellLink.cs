using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MacDock;

/// <summary>Reads a .lnk's target, arguments and AppUserModelID so pinned shortcuts can be matched to running windows.</summary>
public static class ShellLink
{
    public record Info(string TargetPath, string Arguments, string AppId);

    public static Info Read(string lnkPath)
    {
        object link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var shellLink = (IShellLinkW)link;

            var sb = new StringBuilder(1024);
            shellLink.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            string target = sb.ToString();
            sb.Clear();
            shellLink.GetArguments(sb, sb.Capacity);
            string args = sb.ToString();

            string appId = null;
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (((IPropertyStore)link).GetValue(ref key, out var pv) == 0)
            {
                if (pv.vt == 31 /* VT_LPWSTR */) appId = Marshal.PtrToStringUni(pv.p);
                PropVariantClear(ref pv);
            }
            return new Info(string.IsNullOrEmpty(target) ? null : target, args, string.IsNullOrEmpty(appId) ? null : appId);
        }
        catch (Exception e)
        {
            Log.Error($"read lnk {lnkPath}", e);
            return new Info(null, null, null);
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
}
