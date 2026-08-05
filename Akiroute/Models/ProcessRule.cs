using System.Text.Json.Serialization;

namespace Akiroute.Models;

/// <summary>Routing action applied to a matched process.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessAction>))]
public enum ProcessAction
{
    /// <summary>Route the process through the proxy.</summary>
    Proxy,

    /// <summary>Connect the process directly, bypassing the proxy.</summary>
    Direct,

    /// <summary>Block the process from network access.</summary>
    Block,
}

/// <summary>A process-matching rule for per-application routing.</summary>
public class ProcessRule
{
    /// <summary>Executable file name (e.g. "chrome.exe") the rule applies to.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Routing action for the matched process.</summary>
    public ProcessAction Action { get; set; }
}
