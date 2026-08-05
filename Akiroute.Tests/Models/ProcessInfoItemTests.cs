using Akiroute.Models;

namespace Akiroute.Tests.Models;

/// <summary>
/// ProcessInfoItem runtime-UI POCO tests. The item is never serialized and its
/// Icon is object-typed precisely so this test file never loads the WinUI runtime.
/// </summary>
public class ProcessInfoItemTests
{
    [Fact]
    public void NewItem_HasEmptyDisplayFields_AndProxyAction()
    {
        // Act: create a fresh process item via the parameterless ctor.
        var item = new ProcessInfoItem();

        // Assert: display fields are empty, Pid is 0, action is Proxy, not a system process.
        Assert.Equal("", item.ProcessName);
        Assert.Equal("", item.DisplayName);
        Assert.Equal(0, item.Pid);
        Assert.Equal("", item.Path);
        Assert.Null(item.Icon);
        Assert.Equal(ProcessAction.Proxy, item.Action);
        Assert.False(item.IsSystem);
    }

    [Fact]
    public void Item_Properties_AreSettable()
    {
        // Arrange: a populated process item; the icon is any object (no WinUI load).
        var icon = new object();
        var item = new ProcessInfoItem
        {
            ProcessName = "chrome.exe",
            DisplayName = "Google Chrome",
            Pid = 4242,
            Path = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            Icon = icon,
            Action = ProcessAction.Block,
            IsSystem = true,
        };

        // Assert: every value round-trips through the POCO surface.
        Assert.Equal("chrome.exe", item.ProcessName);
        Assert.Equal("Google Chrome", item.DisplayName);
        Assert.Equal(4242, item.Pid);
        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", item.Path);
        Assert.Same(icon, item.Icon);
        Assert.Equal(ProcessAction.Block, item.Action);
        Assert.True(item.IsSystem);
    }
}
