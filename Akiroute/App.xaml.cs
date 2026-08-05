using System;
using Akiroute.Helpers;
using Akiroute.Services;
using Akiroute.ViewModels;
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
    private bool _allowExit;

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
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // --- Compose the service graph and the main view model. ---
        var settings = SettingsService.Load();
        var xray = new XrayService();
        var ping = new PingService(new TcpProxyTester());
        var monitor = new ProcessMonitorService();

        var vm = new MainViewModel(settings, xray, ping, monitor);

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
            currentNodeName: () => vm.Nodes.SelectedNode?.Name ?? "未选择",
            toggleProxy: () => _ = RunToggleAsync(vm),
            switchNode: () => CycleNode(vm),
            showWindow: ShowMainWindow,
            exitApp: ExitApp);
        _tray.Start();

        window.Activate();
        window.AppWindow.Resize(new SizeInt32(1080, 720));

        // --- Startup auto-connect (settings-driven). ---
        if (settings.AutoConnect && !string.IsNullOrEmpty(settings.SelectedNodeId))
        {
            _ = RunToggleAsync(vm);
        }
    }

    /// <summary>Absolute path of the tray icon, next to the engine/rules assets in the output directory.</summary>
    private static string TrayIconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "Akiroute.ico");

    /// <summary>Minimize-to-tray: cancel the close and hide the window instead.</summary>
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
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

    /// <summary>Tray 退出: release the tray and close the window for real.</summary>
    private void ExitApp()
    {
        _allowExit = true;
        _tray?.Dispose();
        _tray = null;
        MainWindow?.Close();
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
