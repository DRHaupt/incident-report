namespace FieldMap.Framework;

/// <summary>Everything the launcher, window manager and session restore need to know about a window type.</summary>
public sealed class ToolDescriptor
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string IconName { get; init; }

    /// <summary>Creates a window. The argument is a saved state string, or null for a fresh window.</summary>
    public required Func<string?, ToolWindow> Create { get; init; }

    public bool SingleInstance { get; init; }

    public Size DefaultSize { get; init; } = new(1000, 720);
}
