namespace FieldToolkit.Tests;

internal static class TestPaths
{
    public static string Content(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "Content" }.Concat(parts).ToArray());

    public static string TempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fieldtoolkit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
