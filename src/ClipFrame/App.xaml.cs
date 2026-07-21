using System.IO;
using System.Windows;
using ClipFrame.Capture;
using ClipFrame.Core;
using ClipFrame.UI;

namespace ClipFrame;

public partial class App : Application
{
    private RegionManager? _region;
    private CaptureEngine? _capture;
    private FrameOverlayWindow? _overlay;
    private MirrorWindow? _mirror;
    private bool _sessionStateSaved;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = new SettingsStore();

        // Initial region: a 1280x720 rectangle placed near the top-left of the
        // primary monitor's work area (physical pixels). If the monitor layout
        // (screen count + geometry) is unchanged since the last exit, restore
        // the region/mirror rects the user left off with instead.
        var wa = Native.NativeMethods.GetPrimaryWorkArea();
        int w = Math.Min(1280, wa.Width - 200);
        int h = Math.Min(720, wa.Height - 200);
        var initial = new System.Drawing.Rectangle(wa.Left + 80, wa.Top + 80, w, h);

        var currentLayout = Native.NativeMethods.GetAllMonitors();
        System.Drawing.Rectangle? restoredMirrorRect = null;
        bool restoreCoverVisible = false;
        if (SameLayout(settings.Current.LastMonitorLayout, currentLayout))
        {
            if (settings.Current.LastRegion is { } lr)
                initial = new System.Drawing.Rectangle(lr.X, lr.Y, lr.Width, lr.Height);
            if (settings.Current.LastMirrorWindow is { } lm)
                restoredMirrorRect = new System.Drawing.Rectangle(lm.X, lm.Y, lm.Width, lm.Height);
            restoreCoverVisible = settings.Current.LastCoverVisible;
        }

        _region = new RegionManager(initial);

        _capture = new CaptureEngine();

        _mirror = new MirrorWindow(_region, _capture);
        if (restoredMirrorRect is { } rmr)
            _mirror.RestoreWindowRect(rmr);
        if (restoreCoverVisible)
            _mirror.RestoreCoverVisible(true);
        _overlay = new FrameOverlayWindow(_region, _capture, _mirror, settings);

        // Show mirror first so it can auto-place itself outside the region.
        _mirror.Show();
        _overlay.Show();

        // Start capturing the monitor that currently contains the region.
        _capture.Start(_region.CurrentRegion);

        // When the region moves to a different monitor, restart capture on it.
        _region.RegionCommitted += r => _capture!.UpdateRegion(r);
        _region.RegionChanging += () => _capture!.Freeze();
        _region.RegionCommitted += _ => _capture!.Unfreeze();

        // Persist the session state (region + mirror rect + monitor layout) as
        // soon as either window starts closing, while handles are still valid.
        _overlay.Closing += (_, _) => SaveSessionState(settings);
        _mirror.Closing += (_, _) => SaveSessionState(settings);

        _overlay.Closed += (_, _) => Shutdown();
        _mirror.Closed += (_, _) => Shutdown();

        // Optional headless self-test: CLIPFRAME_DIAG=<logpath> captures a few
        // frames, writes stats, and exits. Used only for validation.
        if (Environment.GetEnvironmentVariable("CLIPFRAME_DIAG") is { Length: > 0 } diagPath)
            RunDiagnostics(diagPath);

        // Optional preset persistence self-test.
        if (Environment.GetEnvironmentVariable("CLIPFRAME_DIAG_PRESET") is { Length: > 0 } presetLog)
            RunPresetDiagnostics(presetLog);

        // Optional frame-rate measurement: counts distinct frames over ~2s.
        if (Environment.GetEnvironmentVariable("CLIPFRAME_DIAG_FPS") is { Length: > 0 } fpsLog)
            RunFpsDiagnostics(fpsLog);
    }

    private void RunFpsDiagnostics(string logPath)
    {
        // Measure producer-side frame rate under forced screen churn: a window
        // inside the region repaints every composition tick (~monitor refresh).
        // Phase A runs at 60fps (throttle ~off), phase B at 15fps, so the
        // throttle's capping effect is visible.
        var region = _region!.CurrentRegion;

        var churn = new Window
        {
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = System.Windows.Media.Brushes.Black,
            Width = 200,
            Height = 120,
        };
        var churnText = new System.Windows.Controls.TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 40,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        churn.Content = churnText;
        churn.Show();
        // Place inside the region (physical px) via SetWindowPos.
        var churnHwnd = new System.Windows.Interop.WindowInteropHelper(churn).Handle;
        Native.NativeMethods.SetWindowPos(churnHwnd, Native.NativeMethods.HWND_TOPMOST,
            region.Left + region.Width / 2 - 100, region.Top + region.Height / 2 - 60, 200, 120,
            Native.NativeMethods.SWP_NOACTIVATE);

        int counter = 0;
        void Churn(object? s, EventArgs e) { churnText.Text = (counter++).ToString(); }
        System.Windows.Media.CompositionTarget.Rendering += Churn;

        var results = new System.Text.StringBuilder();

        void RunPhase(int fps, Action done)
        {
            _capture!.TargetFps = fps;
            long start = _capture.FramesProduced;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            t.Tick += (_, _) =>
            {
                if (sw.Elapsed.TotalSeconds >= 2.0)
                {
                    t.Stop();
                    long produced = _capture.FramesProduced - start;
                    double measured = produced / sw.Elapsed.TotalSeconds;
                    results.AppendLine($"targetFps={fps} produced={produced} seconds={sw.Elapsed.TotalSeconds:F2} measuredFps={measured:F1}");
                    done();
                }
            };
            t.Start();
        }

        RunPhase(60, () => RunPhase(15, () =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= Churn;
            churn.Close();
            File.WriteAllText(logPath, results.ToString());
            Shutdown();
        }));
    }

    private void RunPresetDiagnostics(string logPath)
    {
        try
        {
            var rect = new System.Drawing.Rectangle(11, 22, 640, 480);
            var storeA = new Core.PresetStore();
            var saved = storeA.Save("diag-test", rect);
            // Reload from disk in a fresh instance to prove persistence.
            var storeB = new Core.PresetStore();
            var found = storeB.Presets.FirstOrDefault(p => p.Name == "diag-test");
            bool ok = found != null && found.Rect == rect;
            File.WriteAllText(logPath,
                $"saved={saved.Name} reloaded={(found != null)} rectMatch={(found?.Rect == rect)} count={storeB.Presets.Count}\n");
            // Clean up the test preset.
            if (found != null) storeB.Remove(found);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, "ERROR: " + ex);
        }
        finally
        {
            Shutdown();
        }
    }

    private void RunDiagnostics(string logPath)
    {
        // Confirm capture via the producer counter (the mirror's render loop is
        // the real consumer, so we can't rely on TryGetFrame here).
        int ticks = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        timer.Tick += (_, _) =>
        {
            ticks++;
            long produced = _capture!.FramesProduced;
            if (produced > 0 || ticks >= 15)
            {
                File.WriteAllText(logPath,
                    $"framesProduced={produced} capturing={(produced > 0)} ticks={ticks}\n");
                timer.Stop();
                Shutdown();
            }
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _capture?.Dispose();
        base.OnExit(e);
    }

    /// <summary>True when both layouts have the same monitor count and geometry, in order.</summary>
    private static bool SameLayout(List<RectInfo>? saved, List<System.Drawing.Rectangle> current)
    {
        if (saved == null || saved.Count != current.Count) return false;
        for (int i = 0; i < saved.Count; i++)
        {
            var s = saved[i];
            var c = current[i];
            if (s.X != c.X || s.Y != c.Y || s.Width != c.Width || s.Height != c.Height)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Captures the current region + mirror rect + cover visibility + monitor
    /// layout so the next launch can restore them if the monitor layout
    /// hasn't changed. Called from Closing (not OnExit) so window handles are
    /// still valid.
    /// </summary>
    private void SaveSessionState(SettingsStore settings)
    {
        if (_sessionStateSaved || _region == null) return;
        _sessionStateSaved = true;

        var r = _region.CurrentRegion;
        settings.Current.LastRegion = new RectInfo(r.X, r.Y, r.Width, r.Height);

        if (_mirror != null && _mirror.TryGetPhysicalRect(out var mr))
            settings.Current.LastMirrorWindow = new RectInfo(mr.X, mr.Y, mr.Width, mr.Height);

        settings.Current.LastCoverVisible = _mirror?.IsCoverVisible ?? false;

        settings.Current.LastMonitorLayout = Native.NativeMethods.GetAllMonitors()
            .Select(m => new RectInfo(m.X, m.Y, m.Width, m.Height))
            .ToList();

        settings.Save();
    }
}
