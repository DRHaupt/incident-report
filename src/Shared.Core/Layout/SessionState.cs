namespace Shared.Core.Layout;

/// <summary>The tool windows that were open when the toolkit last closed, for optional restore.</summary>
public sealed class SessionState
{
    public DateTime SavedUtc { get; set; }
    public List<WindowSession> Windows { get; set; } = new();
}

public sealed class WindowSession
{
    public string ToolId { get; set; } = "";

    /// <summary>Tool-specific state, e.g. the open first-aid topic and step.</summary>
    public string State { get; set; } = "";

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public bool Minimized { get; set; }
    public bool Pinned { get; set; }
}
