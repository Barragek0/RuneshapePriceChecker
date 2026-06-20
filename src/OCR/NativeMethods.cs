using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.OCR;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

    public static uint GetProcessIdForWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return 0;
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId;
    }

    public static bool AreWindowFamilyRelated(IntPtr candidateWindowHandle, IntPtr foregroundWindowHandle)
    {
        if (candidateWindowHandle == IntPtr.Zero || foregroundWindowHandle == IntPtr.Zero)
            return false;

        if (candidateWindowHandle == foregroundWindowHandle)
            return true;

        if (IsChild(candidateWindowHandle, foregroundWindowHandle) || IsChild(foregroundWindowHandle, candidateWindowHandle))
            return true;

        const uint gaRoot = 2;
        var candidateRoot = GetAncestor(candidateWindowHandle, gaRoot);
        var foregroundRoot = GetAncestor(foregroundWindowHandle, gaRoot);

        return candidateRoot != IntPtr.Zero && candidateRoot == foregroundRoot;
    }
}
