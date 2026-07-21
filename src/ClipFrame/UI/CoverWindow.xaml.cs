using System.Windows;
using System.Windows.Interop;
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

    public CoverWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource)PresentationSource.FromVisual(this)!;

        // Keep the cover out of Alt+Tab (and share pickers) — sharing the
        // cover by mistake instead of the mirror must not happen.
        IntPtr ex = GetWindowLongPtr(src.Handle, GWL_EXSTYLE);
        SetWindowLongPtr(src.Handle, GWL_EXSTYLE,
            new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW));

        src.AddHook(WndProc);
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
