# Emergency restore: brings back the Windows taskbar if MacDock was killed without exiting cleanly.
# Usage:  powershell -ExecutionPolicy Bypass -File restore-taskbar.ps1
param(
    # Taskbar state to restore: 1 = auto-hide (what this PC had before MacDock), 2 = always visible.
    [int]$State = 1
)

Get-Process MacDock -ErrorAction SilentlyContinue | Stop-Process -Force

Add-Type -Namespace MacDockRestore -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public int l, t, r, b; public IntPtr lParam; }
[DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
'@

$abd = New-Object MacDockRestore.Native+APPBARDATA
$abd.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($abd)
$abd.lParam = [IntPtr]$State
[void][MacDockRestore.Native]::SHAppBarMessage(10, [ref]$abd)   # ABM_SETSTATE

$primary = [MacDockRestore.Native]::FindWindow("Shell_TrayWnd", $null)
[void][MacDockRestore.Native]::ShowWindow($primary, 5)          # SW_SHOW
$secondary = [IntPtr]::Zero
while (($secondary = [MacDockRestore.Native]::FindWindowEx([IntPtr]::Zero, $secondary, "Shell_SecondaryTrayWnd", $null)) -ne [IntPtr]::Zero) {
    [void][MacDockRestore.Native]::ShowWindow($secondary, 5)
}
Write-Host "Taskbar restored."
