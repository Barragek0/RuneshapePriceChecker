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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll")]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    /// <summary>Finds PIDs of all processes matching the given executable name, using
    /// Toolhelp API instead of System.Diagnostics to avoid CLR metadata overhead.</summary>
    public static int[] FindProcessIdsByName(string processName)
    {
        var results = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return [];

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
                return [];

            do
            {
                if (string.Equals(entry.szExeFile, processName + ".exe", StringComparison.OrdinalIgnoreCase))
                    results.Add((int)entry.th32ProcessID);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return [.. results];
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
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, IntPtr lpString, int nMaxCount);

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
