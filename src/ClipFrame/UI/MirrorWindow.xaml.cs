using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipFrame.Capture;
using ClipFrame.Core;
using ClipFrame.Native;
using static ClipFrame.Native.NativeMethods;

namespace ClipFrame.UI;

public partial class MirrorWindow : Window
{
    /// <summary>Custom caption height (DIP). Must match the XAML row/CaptionHeight.</summary>
    private const double TitleBarDip = 30;

    private readonly RegionManager _region;
    private readonly CaptureEngine _capture;

    private bool _showOverlapWarning = true;

    private CoverWindow? _cover;
    private bool _syncingRect;

    private IntPtr _hwnd;
    private WriteableBitmap? _bitmap;
    private byte[]? _buffer;
    private int _bmpWidth;
    private int _bmpHeight;

    public MirrorWindow(RegionManager region, CaptureEngine capture)
    {
        _region = region;
        _capture = capture;
        InitializeComponent();

        _region.RegionCommitted += OnRegionCommitted;
        _region.RegionChanging += () => Dispatcher.BeginInvoke(UpdatePausedIndicator);
        _region.RegionCommitted += _ => Dispatcher.BeginInvoke(UpdatePausedIndicator);

        Loaded += OnLoaded;
        LocationChanged += (_, _) => { UpdateOverlapWarning(); SyncCoverToMirror(); };
        SizeChanged += (_, _) => { UpdateOverlapWarning(); SyncCoverToMirror(); };
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd = src.Handle;
        src.AddHook(WndProc);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AutoPlaceOutsideRegion(_region.CurrentRegion);
        UpdateOverlapWarning();
    }

    // ---- Frame pump (pull model) ----

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_capture.TryGetFrame(ref _buffer, out int w, out int h) || _buffer == null)
            return;
        if (w <= 0 || h <= 0) return;

        if (_bitmap == null || _bmpWidth != w || _bmpHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _bmpWidth = w;
            _bmpHeight = h;
            MirrorImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _buffer, w * 4, 0);
    }

    // ---- Auto-placement outside the shared region (spec §5) ----

    private void AutoPlaceOutsideRegion(Rectangle region)
    {
        var (_, monRect) = NativeMethods.GetMonitorForRegion(region);
        // Approximate mirror size in physical px from its current DIP size.
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        int mw = (int)Math.Round(Width * scale);
        int mh = (int)Math.Round(Height * scale);

        // Candidate anchors: right, below, left, above of the region.
        var candidates = new[]
        {
            new Rectangle(region.Right + 16, region.Top, mw, mh),
            new Rectangle(region.Left, region.Bottom + 16, mw, mh),
            new Rectangle(region.Left - mw - 16, region.Top, mw, mh),
            new Rectangle(region.Left, region.Top - mh - 16, mw, mh),
        };

        foreach (var c in candidates)
        {
            if (monRect.Contains(c) && !c.IntersectsWith(region))
            {
                MoveTo(c.Left, c.Top);
                return;
            }
        }

        // Fallback: bottom-right of the work area.
        MoveTo(monRect.Right - mw - 24, monRect.Bottom - mh - 24);
    }

    private void MoveTo(int x, int y)
    {
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ---- Region follow (aspect ratio) ----

    private void OnRegionCommitted(Rectangle region)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (region.Width > 0 && region.Height > 0)
            {
                double aspect = (double)region.Width / region.Height;
                // Keep current width, adjust height to match the region aspect.
                // The caption row is part of the client area now, so add it on
                // top of the video height to keep the image letterbox-free.
                Height = Math.Max(MinHeight, TitleBarDip + Width / aspect);
            }
            UpdateOverlapWarning();
        });
    }

    // ---- Self-reflection warning ----

    /// <summary>
    /// When false, the "mirror overlaps the shared region" warning is never shown,
    /// regardless of overlap state (spec §5). Persisted via <see cref="Core.SettingsStore"/>.
    /// </summary>
    public bool ShowOverlapWarning
    {
        get => _showOverlapWarning;
        set
        {
            if (_showOverlapWarning == value) return;
            _showOverlapWarning = value;
            UpdateOverlapWarning();
        }
    }

    private void UpdateOverlapWarning()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (!_showOverlapWarning)
        {
            OverlapWarning.Visibility = Visibility.Collapsed;
            return;
        }

        GetWindowRect(_hwnd, out RECT wr);
        var mirror = new Rectangle(wr.Left, wr.Top, wr.Width, wr.Height);
        bool overlaps = mirror.IntersectsWith(_region.CurrentRegion);
        OverlapWarning.Visibility = overlaps ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePausedIndicator()
    {
        // Placeholder hook for pause state visualization; wired via capture freeze.
    }

    // ---- Cover window: hides the mirror so it may safely overlap the region ----

    private void OnShowCoverClick(object sender, RoutedEventArgs e) => ShowCover();

    private void ShowCover()
    {
        if (_cover == null)
        {
            // Owned window → the OS keeps the cover above the mirror; it also
            // closes automatically when the mirror closes.
            _cover = new CoverWindow { Owner = this };
            _cover.LocationChanged += (_, _) => SyncMirrorToCover();
            _cover.SizeChanged += (_, _) => SyncMirrorToCover();
            _cover.HideRequested += () => _cover.Hide();
        }

        // Place exactly over the mirror before showing.
        _syncingRect = true;
        _cover.Left = Left;
        _cover.Top = Top;
        _cover.Width = Width;
        _cover.Height = Height;
        _syncingRect = false;

        _cover.Show();
    }

    /// <summary>The cover is the drag/resize handle while the mirror is buried.</summary>
    private void SyncMirrorToCover()
    {
        if (_cover == null || _syncingRect) return;
        _syncingRect = true;
        Left = _cover.Left;
        Top = _cover.Top;
        Width = _cover.Width;
        Height = _cover.Height;
        _syncingRect = false;
    }

    /// <summary>Keeps the cover glued to the mirror (e.g. aspect-follow resize).</summary>
    private void SyncCoverToMirror()
    {
        if (_cover == null || !_cover.IsVisible || _syncingRect) return;
        _syncingRect = true;
        _cover.Left = Left;
        _cover.Top = Top;
        _cover.Width = Width;
        _cover.Height = Height;
        _syncingRect = false;
    }

    // ---- Minimize guard (spec §5): dodge to a corner instead of minimizing ----

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND)
        {
            int cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd == SC_MINIMIZE)
            {
                DodgeToCorner();
                handled = true;
                return IntPtr.Zero;
            }
            // Block maximize (incl. caption double-click): a full-screen mirror
            // is never useful and would desync the paired cover window.
            if (cmd == SC_MAXIMIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    // ---- Title-bar buttons ----

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDodgeClick(object sender, RoutedEventArgs e) => DodgeToCorner();

    private void DodgeToCorner()
    {
        GetWindowRect(_hwnd, out RECT wr);
        var region = _region.CurrentRegion;
        var (_, monRect) = NativeMethods.GetMonitorForRegion(region);

        // Move to whichever bottom corner does not cover the region.
        int x = monRect.Right - wr.Width - 8;
        int y = monRect.Bottom - wr.Height - 8;
        var target = new Rectangle(x, y, wr.Width, wr.Height);
        if (target.IntersectsWith(region))
        {
            x = monRect.Left + 8; // try bottom-left instead
        }
        MoveTo(x, y);
    }
}
