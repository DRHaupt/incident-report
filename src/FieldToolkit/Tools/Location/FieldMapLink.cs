using System.Diagnostics;
using Shared.Core.Geo;
using Shared.Core.Interop;

namespace FieldToolkit.Tools.Location;

/// <summary>
/// Hand-over to Field Map through the shared exchange folder (see docs/FIELDMAP-ARCHITECTURE.md): "Open in map" writes a
/// point to Field Map's inbox and starts Field Map (or brings it forward); points sent from Field Map become saved points.
/// </summary>
public static class FieldMapLink
{
    /// <summary>Returns a message for the user (opened, or queued until Field Map is started).</summary>
    public static string OpenInMap(string name, double lat, double lon, string note = "")
    {
        Exchange.Send(Exchange.Inbox(Exchange.ToFieldMap), new ExchangeMessage { Name = name, Lat = lat, Lon = lon, Note = note, From = "Field Toolkit" });
        var exe = FindFieldMap();
        if (exe == null)
            return $"\"{name}\" is waiting for Field Map – it opens when Field Map is started.";
        try
        {
            // A second launch just brings the running Field Map forward and makes it read its inbox.
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return $"Opening \"{name}\" in Field Map.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return $"\"{name}\" is waiting for Field Map (it could not be started: {ex.Message}).";
        }
    }

    /// <summary>FieldMap.exe next to the toolkit, in a sibling FieldMap folder, or in Program Files.</summary>
    public static string? FindFieldMap()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "FieldMap.exe"),
            Path.Combine(baseDir, "..", "FieldMap", "FieldMap.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FieldMap", "FieldMap.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FieldMap", "FieldMap.exe")
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>Points Field Map sent ("Send to Field Toolkit"), added to the saved points. Returns how many arrived.</summary>
    public static int ImportFromFieldMap()
    {
        var messages = Exchange.Receive(Exchange.Inbox(Exchange.ToFieldToolkit));
        foreach (var m in messages)
            AppServices.Points.Items.Add(new SavedPoint
            {
                Name = m.Name.Length > 0 ? m.Name : "Point from Field Map",
                Latitude = m.Lat,
                Longitude = m.Lon,
                Note = (m.Note.Length > 0 ? m.Note + " – " : "") + $"from {m.From} {m.SentUtc.ToLocalTime():HH:mm}"
            });
        if (messages.Count > 0)
            AppServices.Points.Save();
        return messages.Count;
    }
}
