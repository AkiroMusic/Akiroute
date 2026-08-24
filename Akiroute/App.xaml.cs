using System;
using System.Runtime.InteropServices;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;
using Microsoft.Graphics.Display;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Akiroute;

/// <summary>
/// Application entry point (plan Task 5.2/5.3): binds the UI dispatcher, composes
/// the service graph and the main view model, launches the three-panel main
/// window with the system tray attached, and hooks global unhandled-exception
/// capture. Closing the window minimizes it to the tray; the tray's 退出 item
/// performs a real exit.
/// </summary>
public partial class App : Application
{
    /// <summary>Reference to the main window, used by tray/view-model code.</summary>
    public static Window? MainWindow { get; private set; }

    private TrayIconService? _tray;
    private XrayService? _xray;
    private System.Threading.Mutex? _singleInstanceMutex;
    private AppSettings? _settings;
    private bool _allowExit;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string? lpText, string? lpCaption, uint uType);

    public App()
    {
        InitializeComponent();

        // Bind the app's UI dispatcher so the child view models' background events
        // (xray log lines, ping completions, traffic samples) marshal onto the UI
        // thread instead of crashing the bound collections.
        DispatcherHelper.Initialize(DispatcherQueue.GetForCurrentThread());

        UnhandledException += (_, e) =>
        {
            // Never swallow silently: record to the debug output so it can be
            // captured by the log service.
            System.Diagnostics.Debug.WriteLine($"[FATAL] {e.Exception}");
            AppLogger.Error($"[FATAL] {e.Exception}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // --- Single-instance guard: show native message and exit if already running. ---
        _singleInstanceMutex = new System.Threading.Mutex(true, "Local\\Akiroute.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBoxW(IntPtr.Zero, "Akiroute 已在运行\nAkiroute is already running", "Akiroute", 0x00000040 /* MB_ICONINFORMATION */);
            Environment.Exit(0);
            return;
        }

        // --- Startup orphan cleanup: kill leftover xray from a previous crash ---
        XrayService.KillOrphanedEngine(Helpers.AppPaths.XrayExe);

        // --- Compose the service graph and the main view model. ---
        var settings = SettingsService.Load();
        _xray = new XrayService();
        var ping = new PingService(new TcpProxyTester());
        var monitor = new ProcessMonitorService();

        _settings = settings;
        var vm = new MainViewModel(settings, _xray, ping, monitor);

        // Persist the shared settings whenever nodes are imported or process
        // routing rules change (the settings panel persists via its own Save).
        vm.Nodes.NodesImported += (_, _) => vm.SaveSettings();
        vm.Processes.RulesChanged += (_, _) => vm.SaveSettings();

        // --- Launch the main window. ---
        var window = new Views.MainWindow(vm);
        MainWindow = window;

        ThemeHelper.ApplyThemeSafe(window, settings.Theme);

        // Window icon (title bar + taskbar). Unpackaged WinUI 3 needs an
        // absolute path; the .ico ships next to the engine assets.
        try
        {
            window.AppWindow.SetIcon(TrayIconPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] SetIcon failed: {ex.Message}");
        }

        // Close-to-tray: cancel window closes and hide unless an explicit exit.
        window.AppWindow.Closing += OnWindowClosing;

        _tray = new TrayIconService(
            window.DispatcherQueue,
            TrayIconPath,
            isProxyRunning: () => vm.IsProxyRunning,
            currentNodeName: () => vm.Nodes.SelectedNode?.Name ?? Loc.Get("Status.NoNodeSelected"),
            toggleProxy: () => _ = RunToggleAsync(vm),
            switchNode: () => CycleNode(vm),
            showWindow: ShowMainWindow,
            exitApp: ExitApp);
        _tray.Start();

        window.Activate();

        // Restore saved window bounds (physical pixels, matching the original Resize pattern).
        // Multi-monitor: enumerate all displays, pick the one with the largest overlap
        // area against the saved rect, and clamp within that display's OuterBounds.
        if (settings.WindowX is int wx && settings.WindowY is int wy
            && settings.WindowWidth is int ww && settings.WindowHeight is int wh
            && ww > 0 && wh > 0)
        {
            var displays = DisplayArea.FindAll();
            var savedRight = wx + ww;
            var savedBottom = wy + wh;

            // Find the display whose OuterBounds has the largest intersection area
            // with the saved window rect (0 if they don't overlap at all).
            // NOTE: iterate by INDEX, not foreach — the WinRT interop for the
            // returned collection's IEnumerable projection throws
            // InvalidCastException on enumeration (observed at runtime), while
            // the indexer path works.
            DisplayArea? bestDisplay = null;
            var bestArea = 0;

            for (var i = 0; i < displays.Count; i++)
            {
                var display = displays[i];
                var db = display.OuterBounds;
                // Compute intersection rectangle (clamped to zero width/height when disjoint).
                var ix = Math.Max(wx, db.X);
                var iy = Math.Max(wy, db.Y);
                var ixr = Math.Min(savedRight, db.X + db.Width);
                var iyb = Math.Min(savedBottom, db.Y + db.Height);
                var area = Math.Max(0, ixr - ix) * Math.Max(0, iyb - iy);

                if (area > bestArea)
                {
                    bestArea = area;
                    bestDisplay = display;
                }
            }

            if (bestDisplay is not null && bestArea > 0)
            {
                // Clamp within the chosen display's full work area.
                var screen = bestDisplay.OuterBounds;
                var x = Math.Clamp(wx, screen.X, screen.X + screen.Width - Math.Min(ww, screen.Width));
                var y = Math.Clamp(wy, screen.Y, screen.Y + screen.Height - Math.Min(wh, screen.Height));
                window.AppWindow.MoveAndResize(new RectInt32(x, y, ww, wh));
            }
            else
            {
                // Saved bounds have zero overlap with every display — fall back to defaults.
                window.AppWindow.Resize(new SizeInt32(1080, 720));
            }
        }
        else
        {
            window.AppWindow.Resize(new SizeInt32(1080, 720));
        }

        // --- Startup auto-connect (settings-driven). ---
        if (settings.AutoConnect && !string.IsNullOrEmpty(settings.SelectedNodeId))
        {
            _ = RunToggleAsync(vm);
        }

        // --- Subscription auto-update scheduler: 60s sweep, per-entry interval. ---
        StartSubscriptionTimer(vm);

        AppLogger.Info("Akiroute started");
    }

    /// <summary>60s subscription sweep timer; each entry updates only when due.</summary>
    private DispatcherQueueTimer? _subscriptionTimer;

    /// <summary>0 = idle, 1 = a sweep is running (prevents overlapping sweeps).</summary>
    private int _sweepInFlight;

    /// <summary>
    /// Starts the 60-second DispatcherQueueTimer that drives scheduled
    /// maintenance on the UI thread: the subscription sweep (entries whose
    /// effective interval elapsed) and the auto latency-test (when enabled).
    /// List iteration needs no locking — everything runs on this thread.
    /// </summary>
    private void StartSubscriptionTimer(MainViewModel vm)
    {
        _subscriptionTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _subscriptionTimer.Interval = TimeSpan.FromMinutes(1);
        _subscriptionTimer.Tick += (_, _) => _ = RunSubscriptionSweepAsync(vm);
        _subscriptionTimer.Tick += (_, _) => _ = RunAutoPingTickAsync(vm);
        _subscriptionTimer.Start();
    }

    /// <summary>
    /// Timer tick half that refreshes node latency badges when
    /// 自动测速 is enabled and its interval has elapsed. IsPinging overlap
    /// (manual sweep in flight) is guarded inside RunAutoPingIfDueAsync.
    /// </summary>
    private async Task RunAutoPingTickAsync(MainViewModel vm)
    {
        try
        {
            await vm.Nodes.RunAutoPingIfDueAsync(DateTimeOffset.Now).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[App] Auto-ping failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One sweep: for every entry whose effective interval has elapsed, run an
    /// update. Entries are processed sequentially; a failing entry is logged and
    /// skipped without aborting the rest (fault isolation per entry).
    /// </summary>
    private async Task RunSubscriptionSweepAsync(MainViewModel vm)
    {
        if (Interlocked.CompareExchange(ref _sweepInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            foreach (var entry in _settings?.Subscriptions?.ToList() ?? [])
            {
                var interval = vm.Nodes.GetEffectiveIntervalMinutes(entry);
                if (!NodeListViewModel.ShouldRunUpdate(now, entry.LastUpdated, interval))
                {
                    continue;
                }

                try
                {
                    await vm.Nodes.UpdateSubscriptionAsync(entry).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[App] Subscription update threw url={entry.Url}: {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _sweepInFlight, 0);
        }
    }

    /// <summary>Absolute path of the tray icon, next to the engine/rules assets in the output directory.</summary>
    private static string TrayIconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "Akiroute.ico");

    /// <summary>Minimize-to-tray: cancel the close and hide the window instead.</summary>
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Persist window bounds on both close-to-tray and real exit.
        SaveWindowBounds();

        if (_allowExit)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>Restores and foregrounds the (possibly hidden) main window.</summary>
    private void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null)
        {
            return;
        }

        window.AppWindow.Show();
        window.Activate();
    }

    /// <summary>
    /// Tray 退出: real exit. Order matters — stop the subscription timer FIRST
    /// (no new sweeps may start mid-teardown), then dispose the tray so it stops
    /// posting callbacks onto this (UI) thread while we tear down; then dispose
    /// xray (kills the engine tree). Every step is exception-guarded so a failure
    /// in one can never skip window close / process exit and leave a zombie.
    /// </summary>
    private void ExitApp()
    {
        try
        {
            _allowExit = true;
            _subscriptionTimer?.Stop();
            _subscriptionTimer = null;
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Tray dispose failed: {ex.Message}");
            AppLogger.Warn($"[App] Tray dispose failed: {ex.Message}");
        }
        finally
        {
            _tray = null;

            try
            {
                _xray?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Xray dispose failed: {ex.Message}");
                AppLogger.Warn($"[App] Xray dispose failed: {ex.Message}");
            }
            finally
            {
                _xray = null;
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
                MainWindow?.Close();
            }
        }
    }

    /// <summary>Persists the current window position and size to AppSettings.</summary>
    private void SaveWindowBounds()
    {
        if (_settings is null || MainWindow is null)
        {
            return;
        }

        var pos = MainWindow.AppWindow.Position;
        var size = MainWindow.AppWindow.Size;
        _settings.WindowX = pos.X;
        _settings.WindowY = pos.Y;
        _settings.WindowWidth = size.Width;
        _settings.WindowHeight = size.Height;
        SettingsService.Save(_settings);
    }

    /// <summary>Cycles the selected node forward through the visible node list.</summary>
    private static void CycleNode(MainViewModel vm)
    {
        var nodes = vm.Nodes.Nodes;
        if (nodes.Count == 0)
        {
            return;
        }

        var current = vm.Nodes.SelectedNode;
        int index = current is null ? -1 : nodes.IndexOf(current);
        vm.Nodes.SelectNode(nodes[(index + 1) % nodes.Count]);
    }

    /// <summary>Runs the proxy toggle and swallows expected cancellations; the proxy
    /// view model reports real failures through <see cref="ViewModels.ProxyStatusViewModel.LastError"/>.</summary>
    private static async System.Threading.Tasks.Task RunToggleAsync(MainViewModel vm)
    {
        try
        {
            await vm.ToggleProxyAsync();
        }
        catch (OperationCanceledException)
        {
            // User cancelled mid-transition; state is already consistent.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Toggle proxy failed: {ex.Message}");
        }
    }
}
