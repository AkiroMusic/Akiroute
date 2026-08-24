using Akiroute.ViewModels;

namespace Akiroute.Tests.ViewModels;

/// <summary>
/// LogsViewModel tests.  All tests inject plain delegate fakes — no
/// DispatcherQueue or WinUI runtime needed since Refresh() is synchronous.
/// </summary>
public class LogsViewModelTests
{
    [Fact]
    public void Refresh_PullsBothSources_JoinsWithNewLines()
    {
        // Arrange: multi-line arrays from each fake source.
        var appLines = new[] { "app-1", "app-2", "app-3" };
        var engineLines = new[] { "eng-1", "eng-2" };
        var vm = new LogsViewModel(
            _ => appLines,
            () => engineLines);

        // Act
        vm.Refresh();

        // Assert: lines joined with Environment.NewLine.
        Assert.Equal(string.Join(Environment.NewLine, appLines), vm.AppLogText);
        Assert.Equal(string.Join(Environment.NewLine, engineLines), vm.EngineLogText);
    }

    [Fact]
    public void Refresh_EmptySources_ShowsPlaceholder()
    {
        // Arrange: both delegates return empty.
        var vm = new LogsViewModel(
            _ => Array.Empty<string>(),
            static () => Array.Empty<string>());

        // Act
        vm.Refresh();

        // Assert: both display strings show the placeholder.
        Assert.Equal("（暂无日志）", vm.AppLogText);
        Assert.Equal("（暂无日志）", vm.EngineLogText);
    }

    [Fact]
    public void Refresh_EngineOnly_AppSideShowsPlaceholder()
    {
        // Arrange: app returns empty, engine returns lines.
        var vm = new LogsViewModel(
            _ => Array.Empty<string>(),
            static () => new[] { "engine-line-1" });

        // Act
        vm.Refresh();

        // Assert: app side shows placeholder, engine side shows content.
        Assert.Equal("（暂无日志）", vm.AppLogText);
        Assert.Equal("engine-line-1", vm.EngineLogText);
    }

    [Fact]
    public void Refresh_UpdatesRefreshedAt()
    {
        // Arrange: a simple VM — RefreshedAt starts at default (Uninitialized).
        var vm = new LogsViewModel(
            _ => Array.Empty<string>(),
            static () => Array.Empty<string>());

        var before = vm.RefreshedAt;

        // Act
        vm.Refresh();

        // Assert: RefreshedAt moved from default and is close to now.
        Assert.NotEqual(before, vm.RefreshedAt);
        Assert.True(
            Math.Abs((vm.RefreshedAt - DateTimeOffset.Now).TotalSeconds) < 5,
            $"RefreshedAt {vm.RefreshedAt} should be within 5 s of now");
    }
}
