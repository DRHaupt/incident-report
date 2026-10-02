namespace FieldToolkit.Framework;

/// <summary>Everything the launcher, window manager and session restore need to know about a tool.</summary>
public sealed class ToolDescriptor
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>Base name of the embedded icon files (e.g. "firstaid" → firstaid.ico, firstaid-48.png).</summary>
    public required string IconName { get; init; }

    /// <summary>Creates a window for the tool. The argument is a saved state string, or null for a fresh window.</summary>
    public required Func<string?, ToolWindow> Create { get; init; }

    /// <summary>Only one window of this tool may be open; opening it again brings the existing one forward.</summary>
    public bool SingleInstance { get; init; }

    /// <summary>Default client size for a new window (before touch-mode scaling).</summary>
    public Size DefaultSize { get; init; } = new(760, 640);
}
