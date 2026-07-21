using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using ClipFrame.Native;
using static ClipFrame.Native.NativeMethods;

namespace ClipFrame.UI;

/// <summary>
/// Plain window kept above the mirror at the same position/size, so the mirror
/// is never visible inside the shared region (prevents hall-of-mirrors even
/// when the mirror overlaps the region). Intentionally NOT excluded from
/// capture: the shared feed shows this neutral surface instead of the mirror.
/// Owned by the mirror window, so the OS keeps it above the mirror.
/// </summary>
public partial class CoverWindow : Window
{
    /// <summary>Raised when the user clicks the "remove cover" button.</summary>
    public event Action? HideRequested;

    private IntPtr _hwnd;
    private Rectangle? _pendingRestoreRect;

    public CoverWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd = src.Handle;

        // Keep the cover out of Alt+Tab (and share pickers) — sharing the
        // cover by mistake instead of the mirror must not happen.
        IntPtr ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE,
            new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW));

        src.AddHook(WndProc);

        // Placement happens here (not Loaded) for the same reentrancy reason
        // as MirrorWindow: Loaded can fire before _hwnd above is assigned.
        if (_pendingRestoreRect is { } r)
            NativeMethods.ApplyPhysicalRect(_hwnd, r);
    }

    /// <summary>
    /// Requests that the window be placed at <paramref name="rect"/> (physical
    /// px) once loaded. Must be called before the window is shown for the
    /// first time; use <see cref="SetPhysicalRect"/> once it already has a handle.
    /// </summary>
    public void RestoreWindowRect(Rectangle rect) => _pendingRestoreRect = rect;

    /// <summary>Moves+resizes the window to an exact physical-px rect right now.</summary>
    public void SetPhysicalRect(Rectangle rect)
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.ApplyPhysicalRect(_hwnd, rect);
    }

    /// <summary>Reads the window's current rect (physical px). False if the window has no handle yet.</summary>
    public bool TryGetPhysicalRect(out Rectangle rect)
    {
        if (_hwnd == IntPtr.Zero)
        {
            rect = default;
            return false;
        }
        if (!GetWindowRect(_hwnd, out RECT wr))
        {
            rect = default;
            return false;
        }
        rect = new Rectangle(wr.Left, wr.Top, wr.Width, wr.Height);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Block minimize/maximize: minimizing would expose the mirror,
        // maximizing would desync the paired rects.
        if (msg == WM_SYSCOMMAND)
        {
            int cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd == SC_MINIMIZE || cmd == SC_MAXIMIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
