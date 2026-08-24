using Akiroute.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Akiroute.ViewModels;

/// <summary>
/// View model for the 日志 viewer panel: exposes the app logger tail and the
/// xray engine log as display-ready strings, and supports on-demand refresh.
///
/// 轻量级 ViewModel，不依赖 DispatcherQueue — Refresh() 是同步的，
/// UI 线程调度留给消费方。
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    private readonly Func<int, string[]> _readAppTail;
    private readonly Func<IReadOnlyList<string>> _readEngineLogs;

    /// <summary>
    /// Empty-log placeholder resolved via <see cref="Loc.Get"/> at static init.
    /// Falls back to the Chinese literal when the WinUI resource loader is
    /// unavailable (e.g. in unit tests without a WinUI runtime).
    /// </summary>
    private static readonly string s_emptyPlaceholder = GetPlaceholder();

    private static string GetPlaceholder()
    {
        try
        {
            var value = Loc.Get("Logs.EmptyPlaceholder");
            // Loc.Get returns the key itself when the loader is unavailable;
            // treat that as a miss and return the Chinese fallback.
            return value == "Logs.EmptyPlaceholder" ? "（暂无日志）" : value;
        }
        catch
        {
            return "（暂无日志）";
        }
    }

    private string _appLogText = s_emptyPlaceholder;
    private string _engineLogText = s_emptyPlaceholder;
    private DateTimeOffset _refreshedAt;

    /// <summary>App logger tail formatted for display.</summary>
    public string AppLogText
    {
        get => _appLogText;
        set => SetProperty(ref _appLogText, value);
    }

    /// <summary>Engine (xray) log formatted for display.</summary>
    public string EngineLogText
    {
        get => _engineLogText;
        set => SetProperty(ref _engineLogText, value);
    }

    /// <summary>Timestamp of the last successful refresh.</summary>
    public DateTimeOffset RefreshedAt
    {
        get => _refreshedAt;
        set => SetProperty(ref _refreshedAt, value);
    }

    /// <summary>
    /// Creates a new <see cref="LogsViewModel"/>.
    /// </summary>
    /// <param name="readAppTail">
    /// Delegate that reads the last N lines of the app log
    /// (typically <see cref="Helpers.AppLogger.ReadTail"/>).
    /// </param>
    /// <param name="readEngineLogs">
    /// Delegate that returns the current engine log ring buffer
    /// (typically <c>xrayService.RecentLogs</c>).
    /// </param>
    public LogsViewModel(Func<int, string[]> readAppTail, Func<IReadOnlyList<string>> readEngineLogs)
    {
        _readAppTail = readAppTail ?? throw new ArgumentNullException(nameof(readAppTail));
        _readEngineLogs = readEngineLogs ?? throw new ArgumentNullException(nameof(readEngineLogs));
    }

    /// <summary>Pulls both log sources and updates display properties.</summary>
    public void Refresh()
    {
        var appLines = _readAppTail(300);
        var engineLines = _readEngineLogs();

        AppLogText = appLines.Length == 0
            ? s_emptyPlaceholder
            : string.Join(Environment.NewLine, appLines);

        EngineLogText = engineLines.Count == 0
            ? s_emptyPlaceholder
            : string.Join(Environment.NewLine, engineLines);

        RefreshedAt = DateTimeOffset.Now;
    }
}
