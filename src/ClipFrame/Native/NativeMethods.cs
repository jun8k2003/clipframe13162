using System.Drawing;
using System.Runtime.InteropServices;

namespace ClipFrame.Native;

/// <summary>
/// Win32 interop used across the app: capture affinity, window regions,
/// hit-testing, DPI, monitor lookup and the minimize-guard system menu.
/// </summary>
internal static class NativeMethods
{
    // ---- Window messages ----
    public const int WM_MOVE = 0x0003;
    public const int WM_SIZE = 0x0005;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCRBUTTONUP = 0x00A5;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_SIZING = 0x0214;
    public const int WM_MOVING = 0x0216;

    // ---- SC_ (system commands) ----
    public const int SC_MINIMIZE = 0xF020;
    public const int SC_MAXIMIZE = 0xF030;

    // ---- WM_NCHITTEST return codes ----
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
    public const int HTTRANSPARENT = -1;

    // ---- WM_SIZING wParam edges ----
    public const int WMSZ_LEFT = 1;
    public const int WMSZ_RIGHT = 2;
    public const int WMSZ_TOP = 3;
    public const int WMSZ_TOPLEFT = 4;
    public const int WMSZ_TOPRIGHT = 5;
    public const int WMSZ_BOTTOM = 6;
    public const int WMSZ_BOTTOMLEFT = 7;
    public const int WMSZ_BOTTOMRIGHT = 8;

    // ---- SetWindowDisplayAffinity ----
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    // ---- Window regions ----
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    // nCombineMode: RGN_OR=2, RGN_DIFF=4
    public const int RGN_OR = 2;
    public const int RGN_DIFF = 4;

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // ---- Window rect / positioning ----
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // ---- DPI ----
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // ---- Monitor lookup ----
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("Shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    public const int MDT_EFFECTIVE_DPI = 0;

    // ---- Keyboard state (Shift for aspect lock, Alt to bypass snapping) ----
    public const int VK_SHIFT = 0x10;
    public const int VK_MENU = 0x12; // Alt

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    public static bool IsShiftDown() => (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    public static bool IsAltDown() => (GetKeyState(VK_MENU) & 0x8000) != 0;

    // ---- Window styles ----
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_THICKFRAME = 0x00040000; // sizing border — required for NCHITTEST resize
    public const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- MINMAXINFO for enforcing a minimum size ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // ---- Helpers ----

    /// <summary>Returns the primary monitor's work area in physical pixels.</summary>
    public static Rectangle GetPrimaryWorkArea()
    {
        var pt = new POINT(0, 0);
        IntPtr hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfo(hmon, ref mi))
        {
            return new Rectangle(mi.rcWork.Left, mi.rcWork.Top,
                mi.rcWork.Width, mi.rcWork.Height);
        }
        return new Rectangle(0, 0, 1920, 1080);
    }

    /// <summary>Monitor handle + monitor rect (physical px) containing a region.</summary>
    public static (IntPtr Handle, Rectangle MonitorRect) GetMonitorForRegion(Rectangle region)
    {
        var rc = new RECT
        {
            Left = region.Left,
            Top = region.Top,
            Right = region.Right,
            Bottom = region.Bottom
        };
        IntPtr hmon = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfo(hmon, ref mi))
        {
            return (hmon, new Rectangle(mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Width, mi.rcMonitor.Height));
        }
        return (hmon, new Rectangle(0, 0, 1920, 1080));
    }

    /// <summary>
    /// Moves+resizes a window to an exact physical-px rect. If this crosses a
    /// DPI boundary (e.g. restoring onto a monitor with different scaling
    /// than the one the window currently sits on), Windows fires
    /// WM_DPICHANGED synchronously inside the SetWindowPos call and silently
    /// rescales the size just requested — the first call can undershoot.
    /// Re-issuing the same call once the window has settled on the new DPI
    /// corrects it.
    /// </summary>
    public static void ApplyPhysicalRect(IntPtr hwnd, Rectangle target)
    {
        SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
        GetWindowRect(hwnd, out RECT actual);
        if (actual.Width != target.Width || actual.Height != target.Height)
        {
            SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    // ---- Monitor enumeration (layout fingerprint for session-state restore) ----

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>
    /// All monitor rects (physical px), in a stable left-then-top order so two
    /// calls can be compared to detect whether the physical monitor
    /// arrangement (count + geometry) has changed between app launches.
    /// </summary>
    public static List<Rectangle> GetAllMonitors()
    {
        var list = new List<Rectangle>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr _, IntPtr _, ref RECT rect, IntPtr _) =>
            {
                list.Add(new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height));
                return true;
            }, IntPtr.Zero);
        list.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        return list;
    }
}
