using Akiroute.Models;

namespace Akiroute.Tests.Models;

/// <summary>
/// ProcessRule POCO + ProcessAction enum tests.
/// </summary>
public class ProcessRuleTests
{
    [Fact]
    public void NewRule_DefaultsToProxyAction()
    {
        // Act: create a fresh rule.
        var rule = new ProcessRule();

        // Assert: default action is Proxy and no process is matched yet.
        Assert.Equal(ProcessAction.Proxy, rule.Action);
        Assert.Null(rule.ProcessName);
    }

    [Fact]
    public void ProcessAction_DefinesAllActions()
    {
        // Assert: the three routing actions exist as named enum members.
        Assert.True(Enum.IsDefined(typeof(ProcessAction), ProcessAction.Proxy));
        Assert.True(Enum.IsDefined(typeof(ProcessAction), ProcessAction.Direct));
        Assert.True(Enum.IsDefined(typeof(ProcessAction), ProcessAction.Block));
    }

    [Fact]
    public void Rule_Properties_AreSettable()
    {
        // Arrange: a rule that blocks chrome.
        var rule = new ProcessRule { ProcessName = "chrome.exe", Action = ProcessAction.Block };

        // Assert: values round-trip through the POCO surface.
        Assert.Equal("chrome.exe", rule.ProcessName);
        Assert.Equal(ProcessAction.Block, rule.Action);
    }
}
