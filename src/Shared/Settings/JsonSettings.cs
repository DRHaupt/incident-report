using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shared.Settings;

/// <summary>Per-app data folder and JSON settings files under %LOCALAPPDATA%.</summary>
public static class JsonSettings
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>%LOCALAPPDATA%\<paramref name="appFolderName"/>, created if missing.</summary>
    public static string AppDataFolder(string appFolderName)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appFolderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Reads a settings file; a missing or corrupt file gives defaults rather than failing to start.</summary>
    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception)
        {
            // Corrupt settings file: fall back to defaults.
        }
        return new T();
    }

    /// <summary>Writes via a temporary file so a crash or power loss mid-write can't leave a truncated file.</summary>
    public static void Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}
