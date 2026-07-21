using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ClipFrame.Capture;
using ClipFrame.Core;
using ClipFrame.Native;
using static ClipFrame.Native.NativeMethods;

namespace ClipFrame.UI;

public partial class FrameOverlayWindow : Window
{
    // Layout constants (device-independent pixels).
    private const double BorderDip = 6;      // frame ring width (also the grab band)
    private const double TagWidthDip = 150;
    private const double TagHeightDip = 26;
    private const double CornerGrabDip = 14;  // enlarged corner grab zone
    private const double MinRegionWidthDip = 160;
    private const double MinRegionHeightDip = 120;

    private readonly RegionManager _region;
    private readonly CaptureEngine _capture;
    private readonly MirrorWindow _mirror;
    private readonly SettingsStore _settings;

    private IntPtr _hwnd;
    private double _scale = 1.0;
    private int _borderPx = 6;
    private double _tagOffsetDip = 8;        // tag slide position along the top edge
    private double _aspectRatio = 16.0 / 9.0;
    private bool _paused;
    private bool _snapEnabled = true;

    private readonly PresetStore _presets = new();

    // Grip-drag state.
    private bool _gripDragging;
    private System.Windows.Point _gripStartScreen;
    private double _gripStartOffset;

    public FrameOverlayWindow(RegionManager region, CaptureEngine capture,
        MirrorWindow mirror, SettingsStore settings)
    {
        _region = region;
        _capture = capture;
        _mirror = mirror;
        _settings = settings;
        InitializeComponent();

        // Apply the persisted overlap-warning preference to the mirror.
        _mirror.ShowOverlapWarning = _settings.Current.ShowOverlapWarning;

        var r = region.CurrentRegion;
        _aspectRatio = r.Height > 0 ? (double)r.Width / r.Height : 16.0 / 9.0;

        Grip.MouseLeftButtonDown += Grip_MouseLeftButtonDown;
        Grip.MouseMove += Grip_MouseMove;
        Grip.MouseLeftButtonUp += Grip_MouseLeftButtonUp;

        BuildContextMenu();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd = src.Handle;
        src.AddHook(WndProc);

        // Keep the overlay out of Alt+Tab.
        IntPtr ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE,
            new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW));

        // Add a sizing border so the OS honours our WM_NCHITTEST resize codes.
        // WM_NCCALCSIZE (below) then removes the non-client margin so the client
        // area still fills the whole window and our region/layout stay intact.
        IntPtr style = GetWindowLongPtr(_hwnd, GWL_STYLE);
        SetWindowLongPtr(_hwnd, GWL_STYLE,
            new IntPtr(style.ToInt64() | WS_THICKFRAME));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Exclude the whole frame overlay from capture (spec §4.1).
        SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

        ApplyRegionToWindow(_region.CurrentRegion);
    }

    // ---- Geometry ----

    /// <summary>Positions the window around the region and cuts the interior hole.</summary>
    private void ApplyRegionToWindow(Rectangle region)
    {
        _scale = GetDpiForWindow(_hwnd) / 96.0;
        if (_scale <= 0) _scale = 1.0;
        _borderPx = (int)Math.Round(BorderDip * _scale);

        int winX = region.Left - _borderPx;
        int winY = region.Top - _borderPx;
        int winW = region.Width + 2 * _borderPx;
        int winH = region.Height + 2 * _borderPx;

        SetWindowPos(_hwnd, HWND_TOPMOST, winX, winY, winW, winH,
            SWP_NOACTIVATE);

        RecutAndLayout(winW, winH);
        UpdateBadge(region);
    }

    /// <summary>Rebuilds the window region (ring + tag) and lays out the tag.</summary>
    private void RecutAndLayout(int winWpx, int winHpx)
    {
        int b = _borderPx;
        int tagWpx = (int)Math.Round(TagWidthDip * _scale);
        int tagHpx = (int)Math.Round(TagHeightDip * _scale);

        // Clamp tag horizontal offset so it stays within the region span.
        double maxOffsetDip = Math.Max(0, (winWpx - 2 * b) / _scale - TagWidthDip);
        _tagOffsetDip = Math.Clamp(_tagOffsetDip, 0, maxOffsetDip);
        int tagOffsetPx = (int)Math.Round(_tagOffsetDip * _scale);

        // Ring = full rect minus interior hole; then add the tag back.
        IntPtr full = CreateRectRgn(0, 0, winWpx, winHpx);
        IntPtr hole = CreateRectRgn(b, b, winWpx - b, winHpx - b);
        CombineRgn(full, full, hole, RGN_DIFF);

        IntPtr tag = CreateRectRgn(
            b + tagOffsetPx, b,
            b + tagOffsetPx + tagWpx, b + tagHpx);
        CombineRgn(full, full, tag, RGN_OR);

        SetWindowRgn(_hwnd, full, true); // OS owns 'full' after this call.
        DeleteObject(hole);
        DeleteObject(tag);

        // Lay out the WPF tag element in DIP to match the physical tag rect.
        Canvas.SetLeft(TagBar, BorderDip + _tagOffsetDip);
        Canvas.SetTop(TagBar, BorderDip);
        TagBar.Width = TagWidthDip;
        TagBar.Height = TagHeightDip;
    }

    private void UpdateBadge(Rectangle region)
    {
        ResBadge.Text = $"{region.Width} × {region.Height}";
    }

    /// <summary>Reads the current window rect and derives the region (physical px).</summary>
    private Rectangle RegionFromWindow()
    {
        GetWindowRect(_hwnd, out RECT wr);
        int b = _borderPx;
        return new Rectangle(wr.Left + b, wr.Top + b,
            Math.Max(1, wr.Width - 2 * b),
            Math.Max(1, wr.Height - 2 * b));
    }

    // ---- Win32 message hook ----

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE:
                // Client area = entire window (no non-client border from WS_THICKFRAME).
                if (wParam != IntPtr.Zero)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;

            case WM_NCHITTEST:
                handled = true;
                return new IntPtr(HitTest(lParam));

            case WM_NCRBUTTONUP:
                // Tag right-click (HTCAPTION) → show our context menu (spec §4.4).
                if (wParam.ToInt32() == HTCAPTION && TagBar.ContextMenu != null)
                {
                    TagBar.ContextMenu.PlacementTarget = TagBar;
                    TagBar.ContextMenu.IsOpen = true;
                    handled = true;
                }
                break;

            case WM_SYSCOMMAND:
                // Block maximize (double-click on the caption/tag) — it would break layout.
                if ((wParam.ToInt32() & 0xFFF0) == SC_MAXIMIZE)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;

            case WM_ENTERSIZEMOVE:
                _region.BeginChange();
                var r0 = RegionFromWindow();
                _aspectRatio = r0.Height > 0 ? (double)r0.Width / r0.Height : _aspectRatio;
                break;

            case WM_SIZING:
                ConstrainSizing(wParam.ToInt32(), lParam);
                handled = true;
                return new IntPtr(1);

            case WM_SIZE:
            {
                GetWindowRect(_hwnd, out RECT wr);
                RecutAndLayout(wr.Width, wr.Height);
                var r = RegionFromWindow();
                UpdateBadge(r);
                _region.ReportLive(r);
                break;
            }

            case WM_MOVE:
            {
                var r = RegionFromWindow();
                UpdateBadge(r);
                _region.ReportLive(r);
                break;
            }

            case WM_EXITSIZEMOVE:
                _region.Commit(RegionFromWindow());
                break;

            case WM_GETMINMAXINFO:
                SetMinTrackSize(lParam);
                break;

            case 0x02E0: // WM_DPICHANGED
                ApplyRegionToWindow(RegionFromWindow());
                break;
        }
        return IntPtr.Zero;
    }

    private int HitTest(IntPtr lParam)
    {
        int sx = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int sy = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

        GetWindowRect(_hwnd, out RECT wr);
        int lx = sx - wr.Left;
        int ly = sy - wr.Top;
        int w = wr.Width;
        int h = wr.Height;
        int b = _borderPx;

        // Tag zone (grip slides the tag; the rest moves the region).
        int tagWpx = (int)Math.Round(TagWidthDip * _scale);
        int tagHpx = (int)Math.Round(TagHeightDip * _scale);
        int gripWpx = (int)Math.Round(18 * _scale);
        int tagLeft = b + (int)Math.Round(_tagOffsetDip * _scale);
        if (lx >= tagLeft && lx < tagLeft + tagWpx && ly >= b && ly < b + tagHpx)
        {
            if (lx < tagLeft + gripWpx)
                return HTCLIENT;   // grip: handled by WPF mouse events
            return HTCAPTION;      // rest of tag: move the region
        }

        int corner = Math.Max(b, (int)Math.Round(CornerGrabDip * _scale));
        bool left = lx < b;
        bool right = lx >= w - b;
        bool top = ly < b;
        bool bottom = ly >= h - b;
        bool nearLeft = lx < corner;
        bool nearRight = lx >= w - corner;
        bool nearTop = ly < corner;
        bool nearBottom = ly >= h - corner;

        if (top && nearLeft) return HTTOPLEFT;
        if (top && nearRight) return HTTOPRIGHT;
        if (bottom && nearLeft) return HTBOTTOMLEFT;
        if (bottom && nearRight) return HTBOTTOMRIGHT;
        if (left && nearTop) return HTTOPLEFT;
        if (left && nearBottom) return HTBOTTOMLEFT;
        if (right && nearTop) return HTTOPRIGHT;
        if (right && nearBottom) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;

        return HTCLIENT;
    }

    // Common target resolutions for snapping (spec §4.2 / §9).
    private static readonly (int W, int H)[] CommonResolutions =
    {
        (640, 360), (854, 480), (960, 540), (1024, 576), (1280, 720),
        (1366, 768), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160),
        (800, 600), (1024, 768), (1280, 800), (1440, 900), (1680, 1050),
    };

    private void ConstrainSizing(int edge, IntPtr lParam)
    {
        RECT r = Marshal.PtrToStructure<RECT>(lParam);
        int b = _borderPx;

        int regW = r.Width - 2 * b;
        int regH = r.Height - 2 * b;

        int minRegW = (int)Math.Round(MinRegionWidthDip * _scale);
        int minRegH = (int)Math.Round(MinRegionHeightDip * _scale);
        regW = Math.Max(regW, minRegW);
        regH = Math.Max(regH, minRegH);

        bool shift = IsShiftDown();
        bool horizontalDrag = edge is WMSZ_LEFT or WMSZ_RIGHT;
        bool verticalDrag = edge is WMSZ_TOP or WMSZ_BOTTOM;
        bool changeW = !verticalDrag;
        bool changeH = !horizontalDrag;

        // Aspect lock while Shift is held (spec §4.2).
        if (shift && _aspectRatio > 0)
        {
            if (verticalDrag) regW = (int)Math.Round(regH * _aspectRatio);
            else regH = (int)Math.Round(regW / _aspectRatio);
        }

        // Resolution snap (default on; hold Alt for free resize — spec §4.2/§9).
        if (_snapEnabled && !IsAltDown())
            SnapResolution(shift, verticalDrag, changeW, changeH, ref regW, ref regH);

        regW = Math.Max(regW, minRegW);
        regH = Math.Max(regH, minRegH);

        int newW = regW + 2 * b;
        int newH = regH + 2 * b;

        if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT)
            r.Left = r.Right - newW;
        else
            r.Right = r.Left + newW;

        if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT)
            r.Top = r.Bottom - newH;
        else
            r.Bottom = r.Top + newH;

        Marshal.StructureToPtr(r, lParam, false);
    }

    private void SnapResolution(bool aspectLocked, bool verticalDrag,
        bool changeW, bool changeH, ref int regW, ref int regH)
    {
        int thr = (int)Math.Round(16 * _scale); // snap window, physical px

        if (aspectLocked && _aspectRatio > 0)
        {
            // Snap the driving dimension to a clean value, derive the other via aspect.
            if (verticalDrag)
            {
                int h = SnapValue(regH, DistinctHeights, thr);
                if (h != regH) { regH = h; regW = (int)Math.Round(h * _aspectRatio); }
            }
            else
            {
                int w = SnapValue(regW, DistinctWidths, thr);
                if (w != regW) { regW = w; regH = (int)Math.Round(w / _aspectRatio); }
            }
            return;
        }

        // Free resize: snap each dragged dimension independently.
        if (changeW) regW = SnapValue(regW, DistinctWidths, thr);
        if (changeH) regH = SnapValue(regH, DistinctHeights, thr);
    }

    private static readonly int[] DistinctWidths =
        CommonResolutions.Select(r => r.W).Distinct().OrderBy(x => x).ToArray();
    private static readonly int[] DistinctHeights =
        CommonResolutions.Select(r => r.H).Distinct().OrderBy(x => x).ToArray();

    private static int SnapValue(int value, int[] targets, int threshold)
    {
        int best = value, bestDist = threshold + 1;
        foreach (int t in targets)
        {
            int d = Math.Abs(t - value);
            if (d <= threshold && d < bestDist) { best = t; bestDist = d; }
        }
        return best;
    }

    private void SetMinTrackSize(IntPtr lParam)
    {
        MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        int b = _borderPx;
        mmi.ptMinTrackSize.X = (int)Math.Round(MinRegionWidthDip * _scale) + 2 * b;
        mmi.ptMinTrackSize.Y = (int)Math.Round(MinRegionHeightDip * _scale) + 2 * b;
        Marshal.StructureToPtr(mmi, lParam, false);
    }

    // ---- Tag slide (grip) ----

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _gripDragging = true;
        _gripStartScreen = PointToScreen(e.GetPosition(this));
        _gripStartOffset = _tagOffsetDip;
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_gripDragging) return;
        var now = PointToScreen(e.GetPosition(this));
        double dxDip = (now.X - _gripStartScreen.X) / _scale;
        _tagOffsetDip = _gripStartOffset + dxDip;

        GetWindowRect(_hwnd, out RECT wr);
        RecutAndLayout(wr.Width, wr.Height);
    }

    private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _gripDragging = false;
        Grip.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---- Context menu (spec §4.4) ----

    private MenuItem _loadPresetItem = null!;

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var pause = new MenuItem { Header = "共有の一時停止" };
        pause.Click += (_, _) =>
        {
            _paused = !_paused;
            if (_paused) { _capture.Freeze(); pause.Header = "共有の再開"; }
            else { _capture.Unfreeze(); pause.Header = "共有の一時停止"; }
        };
        menu.Items.Add(pause);

        var snap = new MenuItem
        {
            Header = "解像度スナップ (Altで一時無効)",
            IsCheckable = true,
            IsChecked = _snapEnabled,
        };
        snap.Click += (_, _) => _snapEnabled = snap.IsChecked;
        menu.Items.Add(snap);

        var overlapWarn = new MenuItem
        {
            Header = "ミラー重なり警告を表示",
            IsCheckable = true,
            IsChecked = _settings.Current.ShowOverlapWarning,
        };
        overlapWarn.Click += (_, _) =>
        {
            _mirror.ShowOverlapWarning = overlapWarn.IsChecked;
            _settings.Current.ShowOverlapWarning = overlapWarn.IsChecked;
            _settings.Save();
        };
        menu.Items.Add(overlapWarn);

        var fpsMenu = new MenuItem { Header = "フレームレート" };
        foreach (int fps in new[] { 10, 15, 20, 30 })
        {
            var item = new MenuItem
            {
                Header = $"{fps} fps",
                IsCheckable = true,
                IsChecked = _capture.TargetFps == fps,
            };
            int chosen = fps;
            item.Click += (_, _) =>
            {
                _capture.TargetFps = chosen;
                foreach (MenuItem mi in fpsMenu.Items)
                    mi.IsChecked = ReferenceEquals(mi, item);
            };
            fpsMenu.Items.Add(item);
        }
        menu.Items.Add(fpsMenu);

        menu.Items.Add(new Separator());

        var savePreset = new MenuItem { Header = "現在の領域をプリセット保存…" };
        savePreset.Click += (_, _) => SaveCurrentPreset();
        menu.Items.Add(savePreset);

        _loadPresetItem = new MenuItem { Header = "領域プリセットの呼び出し" };
        menu.Items.Add(_loadPresetItem);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "終了" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);

        // Rebuild the preset submenu each time the menu opens.
        menu.Opened += (_, _) => RebuildPresetSubmenu();

        TagBar.ContextMenu = menu;
    }

    private void RebuildPresetSubmenu()
    {
        _loadPresetItem.Items.Clear();
        var list = _presets.Presets;

        if (list.Count == 0)
        {
            _loadPresetItem.Items.Add(new MenuItem { Header = "(保存されたプリセットはありません)", IsEnabled = false });
            return;
        }

        foreach (var preset in list)
        {
            var apply = new MenuItem { Header = $"{preset.Name}   ({preset.Width}×{preset.Height})" };
            var captured = preset;
            apply.Click += (_, _) => ApplyPreset(captured.Rect);

            var delete = new MenuItem { Header = "削除" };
            apply.Items.Add(delete);
            var toDelete = preset;
            delete.Click += (_, _) => _presets.Remove(toDelete);

            _loadPresetItem.Items.Add(apply);
        }
    }

    private void SaveCurrentPreset()
    {
        var r = RegionFromWindow();
        string suggested = $"{r.Width}×{r.Height}";
        string? name = InputDialog.Prompt(this,
            "プリセットの保存", "このプリセットの名前を入力してください:", suggested);
        if (name == null) return; // cancelled
        _presets.Save(name, r);
    }

    /// <summary>Moves/resizes the region to a saved preset; capture and mirror follow.</summary>
    private void ApplyPreset(Rectangle rect)
    {
        // Clamp so at least part of the region stays on a visible monitor.
        var (_, monRect) = NativeMethods.GetMonitorForRegion(rect);
        int x = Math.Clamp(rect.X, monRect.Left, Math.Max(monRect.Left, monRect.Right - rect.Width));
        int y = Math.Clamp(rect.Y, monRect.Top, Math.Max(monRect.Top, monRect.Bottom - rect.Height));
        var target = new Rectangle(x, y, rect.Width, rect.Height);

        ApplyRegionToWindow(target);   // reposition/resize the overlay window
        _aspectRatio = target.Height > 0 ? (double)target.Width / target.Height : _aspectRatio;
        _region.Commit(target);        // capture + mirror follow
    }
}
