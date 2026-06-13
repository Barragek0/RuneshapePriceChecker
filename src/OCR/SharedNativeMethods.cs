using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.OCR;

internal static class SharedNativeMethods
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, IntPtr lpString, int nMaxCount);

    public static string GetWindowTitle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return string.Empty;
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            var len = GetWindowText(windowHandle, buffer, 1024);
            return len > 0 ? Marshal.PtrToStringUni(buffer, len) : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
