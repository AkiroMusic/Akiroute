namespace Akiroute.Models;

/// <summary>
/// A process shown in the per-process routing UI. Runtime-only: never serialized,
/// so it is intentionally absent from <see cref="AppJsonSerializerContext"/>.
/// </summary>
public class ProcessInfoItem
{
    /// <summary>Creates an empty process item with the default (Proxy) action.</summary>
    public ProcessInfoItem()
    {
    }

    /// <summary>Executable file name of the process (e.g. "chrome.exe").</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Friendly name for display (falls back to the process name).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>OS process id.</summary>
    public int Pid { get; set; }

    /// <summary>Full executable path.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Optional icon, held as a WinUI BitmapImage at runtime. Typed as object so
    /// this model stays loadable in unit tests that never load the WinUI runtime.
    /// </summary>
    public object? Icon { get; set; }

    /// <summary>Routing action applied to this process.</summary>
    public ProcessAction Action { get; set; } = ProcessAction.Proxy;

    /// <summary>True for system processes, which are drawn dimmed in the list.</summary>
    public bool IsSystem { get; set; }
}
