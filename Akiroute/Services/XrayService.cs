using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akiroute.Helpers;
using Akiroute.Models;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Akiroute.Tests")]

namespace Akiroute.Services;

/// <summary>Lifecycle state of the xray subprocess.</summary>
public enum XrayServiceState
{
    /// <summary>The xray process is not running.</summary>
    Stopped,

    /// <summary>A start was requested and the process is being launched.</summary>
    Starting,

    /// <summary>The xray process is running and the local inbound is listening.</summary>
    Running,

    /// <summary>The last start attempt failed or the process crashed.</summary>
    Failed,
}

/// <summary>
/// Manages the xray-core subprocess lifecycle: launching, stdout/stderr log
/// capture, crash detection with a tail-20 log extraction, and local inbound
/// port resolution (plan §7.1/§7.2). Pure .NET by design — no WinUI or
/// DispatcherQueue — so it can be consumed from the x64 test host; view models
/// (Phase 5) subscribe to <see cref="LogReceived"/> / <see cref="StateChanged"/>
/// and marshal to the UI thread themselves.
/// </summary>
public sealed class XrayService : IDisposable
{
    /// <summary>Maximum number of recent log lines kept for the crash tail.</summary>
    private const int LogRingCapacity = 20;

    /// <summary>Hard ceiling for the startup readiness probe.</summary>
    private const int ReadinessTimeoutMs = 3000;

    /// <summary>Poll interval of the readiness probe.</summary>
    private const int ReadinessPollMs = 300;

    /// <summary>Per-attempt connect timeout used by the readiness probe.</summary>
    private const int ProbeConnectTimeoutMs = 200;

    /// <summary>Serializes start/stop so concurrent lifecycle calls never race.</summary>
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    /// <summary>Guards the mutable state: ring buffer, process reference, flags.</summary>
    private readonly object _gate = new();

    /// <summary>Ring buffer holding the last <see cref="LogRingCapacity"/> log lines.</summary>
    private readonly Queue<string> _recentLogs = new(LogRingCapacity);

    private readonly string _xrayExePath;
    private readonly string _configTempPath;
    private readonly string _rulesDir;

    private Process? _process;
    private bool _stopping;
    private bool _disposed;

    /// <summary>Raised for every stdout/stderr line emitted by the xray process.</summary>
    public event EventHandler<string>? LogReceived;

    /// <summary>Raised whenever the service transitions between lifecycle states.</summary>
    public event EventHandler<XrayServiceState>? StateChanged;

    /// <summary>True while the xray process is running and considered healthy.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The loopback port the SOCKS inbound is bound to (0 before a successful start).</summary>
    public int LocalPort { get; private set; }

    /// <summary>The last <see cref="LogRingCapacity"/> log lines, in arrival order.</summary>
    public IReadOnlyList<string> RecentLogs => SnapshotLogs();

    /// <summary>Exit code of the last xray process that ran (0 when none has exited).</summary>
    public int ExitCode { get; private set; }

    /// <summary>
    /// Tail-20 log excerpt (or a failure reason) set when the process crashes or
    /// a start attempt fails; consumed by the UI crash dialog.
    /// </summary>
    public string? LastCrashSummary { get; private set; }

    /// <summary>
    /// Creates a new xray process manager.
    /// </summary>
    /// <param name="xrayExePath">Path to xray.exe; defaults to <see cref="AppPaths.XrayExe"/>.</param>
    /// <param name="configTempPath">Path to write the per-start config file; defaults to <see cref="AppPaths.XrayConfigTemp"/>.</param>
    public XrayService(string? xrayExePath = null, string? configTempPath = null)
    {
        _xrayExePath = xrayExePath ?? AppPaths.XrayExe;
        _configTempPath = configTempPath ?? AppPaths.XrayConfigTemp;
        _rulesDir = AppPaths.RulesDir;
    }

    /// <summary>
    /// Starts the xray subprocess for the given node: resolves a free local port,
    /// builds and atomically writes the config, launches the process, and probes
    /// until the SOCKS inbound listens (bounded by a hard 3s timeout and the
    /// cancellation token). Idempotent: returns false when already running.
    /// </summary>
    /// <returns>True when the process is running and the inbound is reachable.</returns>
    public async Task<bool> StartAsync(
        ProxyNode node,
        IReadOnlyList<ProcessRule> processRules,
        ProxyMode mode,
        int preferredPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(processRules);

        try
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            return await StartCoreAsync(node, processRules, mode, preferredPort, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops the xray subprocess if one is running. Kills the process tree, waits
    /// for it to exit, and transitions to <see cref="XrayServiceState.Stopped"/>.
    /// A no-op when nothing is running. Cancellation still performs the kill and
    /// dispose and never leaks a throw.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Acquire with CancellationToken.None so a pre-cancelled token cannot
        // prevent a running process from being killed and disposed.
        await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Kills a running process and releases its resources.</summary>
    public void Dispose()
    {
        _lifecycleLock.Wait();
        try
        {
            Process? process;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _stopping = true;
                process = _process;
            }

            if (process is null)
            {
                return;
            }

            TryKill(process);
            try
            {
                process.WaitForExit(2000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The process was already reaped; nothing more to wait for.
            }

            CleanupProcess();
            IsRunning = false;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Appends a line to the recent-log ring buffer and raises
    /// <see cref="LogReceived"/>. Internal so tests can drive the buffer directly
    /// without spawning a process; the buffer itself stays out of the public API.
    /// </summary>
    internal void AppendLogLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_gate)
        {
            _recentLogs.Enqueue(line);
            while (_recentLogs.Count > LogRingCapacity)
            {
                _recentLogs.Dequeue();
            }
        }

        LogReceived?.Invoke(this, line);
    }

    private async Task<bool> StartCoreAsync(
        ProxyNode node,
        IReadOnlyList<ProcessRule> processRules,
        ProxyMode mode,
        int preferredPort,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
        }

        if (IsRunning)
        {
            return false;
        }

        AppPaths.EnsureDirectories();

        // Resolve the local inbound port (3333..3360 window by default, plan §7.1).
        var localPort = PortFinder.FindFreePort(preferredPort, 28);
        if (localPort == 0)
        {
            SetFailed("No free port", 0);
            return false;
        }

        // Build the config document (JsonObject serializes natively, AOT-safe).
        JsonObject config;
        try
        {
            config = XrayConfigBuilder.Build(node, processRules, localPort, mode);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            SetFailed($"Config build failed: {ex.Message}", 0);
            return false;
        }

        // Atomically persist the config (tmp file + move, same pattern as SettingsService).
        try
        {
            WriteConfigAtomically(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetFailed($"Failed to write config: {ex.Message}", 0);
            return false;
        }

        if (!File.Exists(_xrayExePath))
        {
            SetFailed("xray.exe not found", 0);
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _xrayExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_xrayExePath) ?? string.Empty,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(_configTempPath);
        startInfo.Environment["XRAY_LOCATION_ASSET"] = _rulesDir;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnOutputDataReceived;

        lock (_gate)
        {
            _process = process;
            _stopping = false;
        }
        SetState(XrayServiceState.Starting);

        try
        {
            if (!process.Start())
            {
                CleanupProcess();
                SetFailed("Failed to start xray process", 0);
                return false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            CleanupProcess();
            SetFailed($"Failed to start xray process: {ex.Message}", 0);
            return false;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.Exited += OnProcessExited;

        var ready = await ProbeReadinessAsync(process, localPort, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            if (cancelled)
            {
                lock (_gate)
                {
                    _stopping = true;
                }
            }

            if (!HasExitedSafely(process))
            {
                TryKill(process);
                await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            }

            if (cancelled)
            {
                IsRunning = false;
                SetState(XrayServiceState.Stopped);
            }
            else if (string.IsNullOrEmpty(LastCrashSummary))
            {
                // The Exited handler extracts the real tail-20 when it fires;
                // this generic message covers the process-still-alive case.
                LastCrashSummary = "xray did not become ready within the timeout";
                SetState(XrayServiceState.Failed);
            }

            return false;
        }

        LocalPort = localPort;
        IsRunning = true;
        LastCrashSummary = null;
        SetState(XrayServiceState.Running);
        return true;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_gate)
        {
            if (_process is null)
            {
                if (IsRunning)
                {
                    IsRunning = false;
                    SetState(XrayServiceState.Stopped);
                }
                return;
            }
            _stopping = true;
            process = _process;
        }

        TryKill(process);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            // Cancellation, or the Exited handler already reaped the process;
            // either way the process was killed above and is cleaned up below.
        }

        CleanupProcess();
        IsRunning = false;
        LocalPort = 0;
        SetState(XrayServiceState.Stopped);
    }

    /// <summary>
    /// Peeks at the process state while the process object may be reaped by the
    /// Exited handler concurrently.
    /// </summary>
    private static bool HasExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Polls the process for up to <see cref="ReadinessTimeoutMs"/>: the probe
    /// succeeds as soon as a TCP connect to the SOCKS inbound succeeds, and fails
    /// immediately when the process exits. Cancellation and the hard timeout both
    /// return false without hanging.
    /// </summary>
    private async Task<bool> ProbeReadinessAsync(Process process, int localPort, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ReadinessTimeoutMs);

        while (!HasExitedSafely(process))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (await CanConnectAsync(localPort, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            try
            {
                await Task.Delay(ReadinessPollMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Attempts a bounded loopback TCP connect to the SOCKS inbound port.</summary>
    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeConnectTimeoutMs);
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Handles process exit: extracts the exit code and, unless the stop
    /// was user-requested, records the tail-20 log excerpt as a crash summary.</summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = (Process?)sender;
        if (process is null)
        {
            return;
        }

        bool wasStopping;
        lock (_gate)
        {
            wasStopping = _stopping;
            if (!ReferenceEquals(_process, process))
            {
                // An older process exited after a newer one was started; ignore it.
                return;
            }
        }

        // Drain the async output readers so the crash tail is complete.
        try
        {
            process.WaitForExit(150);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        var exitCode = ReadExitCode(process);
        lock (_gate)
        {
            ExitCode = exitCode;
            IsRunning = false;
        }

        if (wasStopping)
        {
            CleanupProcess();
            SetState(XrayServiceState.Stopped);
        }
        else
        {
            var tail = string.Join(Environment.NewLine, SnapshotLogs());
            LastCrashSummary = string.IsNullOrWhiteSpace(tail)
                ? $"xray exited unexpectedly (code {exitCode})"
                : tail;
            CleanupProcess();
            SetState(XrayServiceState.Failed);
        }
    }

    private static int ReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return -1;
        }
    }

    private void OnOutputDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            AppendLogLine(e.Data);
        }
    }

    /// <summary>
    /// Atomically writes the built config to <see cref="_configTempPath"/>: the
    /// JSON goes to "&lt;path&gt;.tmp" first, then is moved over the target. The
    /// parent directory is intentionally not created — a missing directory is a
    /// real failure surfaced to the caller (and a deterministic test seam).
    /// </summary>
    private void WriteConfigAtomically(JsonObject config)
    {
        var fullPath = Path.GetFullPath(_configTempPath);
        if (Path.GetDirectoryName(fullPath) is null)
        {
            throw new ArgumentException($"Config path has no directory: {_configTempPath}", nameof(_configTempPath));
        }

        var tmpPath = fullPath + ".tmp";
        var json = JsonSerializer.Serialize(config);

        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, fullPath, overwrite: true);
    }

    /// <summary>Kills the process tree, tolerating an already-exited process.</summary>
    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or Win32Exception or NotSupportedException)
        {
            // The process already exited or cannot be killed; nothing more to do.
        }
    }

    /// <summary>Disposes the process object and drops the reference, if any.</summary>
    private void CleanupProcess()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        process?.Dispose();
    }

    private void SetFailed(string summary, int exitCode)
    {
        lock (_gate)
        {
            IsRunning = false;
            LastCrashSummary = summary;
            ExitCode = exitCode;
        }
        SetState(XrayServiceState.Failed);
    }

    private void SetState(XrayServiceState state)
    {
        StateChanged?.Invoke(this, state);
    }

    private string[] SnapshotLogs()
    {
        lock (_gate)
        {
            return _recentLogs.ToArray();
        }
    }
}
