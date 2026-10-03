using FieldMap.Core.Geo;

namespace FieldMap.Core.Aprs;

public readonly record struct AprsTrackPoint(DateTime TimeUtc, double Lat, double Lon);

/// <summary>A station, object or item last heard on APRS, with its recent track.</summary>
public sealed class AprsStation
{
    public required string Key { get; init; }

    /// <summary>The transmitting station (an object's owner).</summary>
    public string Source { get; set; } = "";
    public bool IsObject { get; set; }
    public char SymbolTable { get; set; } = '/';
    public char SymbolCode { get; set; } = '/';
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public int Ambiguity { get; set; }
    public DateTime? PositionUtc { get; set; }
    public DateTime LastHeardUtc { get; set; }
    public AprsVia Via { get; set; }
    public AprsVia LastVia { get; set; }
    public string Path { get; set; } = "";
    public string Comment { get; set; } = "";
    public string? Status { get; set; }
    public double? CourseDegrees { get; set; }
    public double? SpeedKnots { get; set; }
    public double? AltitudeMeters { get; set; }
    public AprsWeather? Weather { get; set; }
    public string? MicEMessage { get; set; }
    public bool Emergency { get; set; }
    public int Packets { get; set; }
    public string LastRaw { get; set; } = "";
    public List<AprsTrackPoint> Track { get; } = new();

    public bool HasPosition => Lat is not null && Lon is not null;
    public GeoPoint? Point => HasPosition ? new GeoPoint(Lat!.Value, Lon!.Value) : null;
    public double? SpeedKmh => SpeedKnots * 1.852;
    public bool Moving => (SpeedKnots ?? 0) >= 2;
    public string SymbolName => AprsSymbols.Name(SymbolTable, SymbolCode);

    public string ViaText => Via switch
    {
        AprsVia.Radio => "radio",
        AprsVia.Internet => "internet",
        AprsVia.Radio | AprsVia.Internet => "radio and internet",
        _ => "–"
    };
}

public sealed record AprsLogEntry(DateTime TimeUtc, AprsVia Via, string Raw, string Summary, bool Error);

/// <summary>
/// Keeps the latest state of every station, object and item heard, merging duplicates heard by radio and internet.
/// Thread-safe: packets arrive on network threads, the map reads snapshots on the UI thread.
/// </summary>
public sealed class AprsStationTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AprsStation> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly LinkedList<AprsLogEntry> _log = new();
    private int _version;

    public int MaxStations { get; set; } = 5000;
    public int MaxTrackPoints { get; set; } = 1000;
    public int MaxLog { get; set; } = 1000;
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromSeconds(30);
    public double MinTrackStepMeters { get; set; } = 15;

    /// <summary>Changes whenever anything changes, so the map knows when to redraw.</summary>
    public int Version
    {
        get { lock (_lock) return _version; }
    }

    public int Count
    {
        get { lock (_lock) return _stations.Count; }
    }

    /// <summary>Records a received packet. Returns the station it updated, or null (duplicate, message, undecodable…).</summary>
    public AprsStation? Process(AprsPacket p, AprsVia via, DateTime nowUtc)
    {
        lock (_lock)
        {
            _version++;
            var dupKey = p.Source + "\u0001" + p.Information;
            var duplicate = _recent.TryGetValue(dupKey, out var seen) && nowUtc - seen < DuplicateWindow;
            if (!duplicate)
            {
                _recent[dupKey] = nowUtc;
                if (_recent.Count > 4000)
                    foreach (var k in _recent.Where(kv => nowUtc - kv.Value > DuplicateWindow).Select(kv => kv.Key).ToList())
                        _recent.Remove(k);
            }
            Log(new AprsLogEntry(nowUtc, via, p.Raw, Summary(p) + (duplicate ? " (duplicate)" : ""), p.Error != null));

            if (p.Error != null && !p.HasPosition && p.Status == null)
                return null;
            if (p.Type is AprsPacketType.Object or AprsPacketType.Item && !p.Alive)
            {
                _stations.Remove(p.Key);
                return null;
            }
            if (!p.HasPosition && p.Type != AprsPacketType.Status && p.Weather == null)
            {
                // Messages, telemetry etc.: the sender was heard.
                if (_stations.TryGetValue(p.Source, out var heard))
                {
                    heard.LastHeardUtc = nowUtc;
                    heard.Via |= via;
                }
                return null;
            }

            if (!_stations.TryGetValue(p.Key, out var s))
            {
                if (_stations.Count >= MaxStations)
                    RemoveOldest();
                s = new AprsStation { Key = p.Key };
                _stations[p.Key] = s;
            }
            s.Via |= via;
            s.LastVia = via;
            s.LastHeardUtc = nowUtc;
            if (duplicate)
                return s;

            s.Packets++;
            s.Source = p.Source;
            s.IsObject = p.ObjectName != null;
            s.LastRaw = p.Raw;
            s.Path = string.Join(",", p.Path);
            if (p.Type == AprsPacketType.Status)
            {
                s.Status = p.Status;
                return s;
            }
            if (p.HasPosition)
            {
                s.SymbolTable = p.SymbolTable;
                s.SymbolCode = p.SymbolCode;
                var moved = !s.HasPosition || new GeoPoint(s.Lat!.Value, s.Lon!.Value).DistanceTo(new GeoPoint(p.Latitude!.Value, p.Longitude!.Value)) >= MinTrackStepMeters;
                s.Lat = p.Latitude;
                s.Lon = p.Longitude;
                s.Ambiguity = p.Ambiguity;
                s.PositionUtc = nowUtc;
                s.CourseDegrees = p.CourseDegrees;
                s.SpeedKnots = p.SpeedKnots;
                if (p.AltitudeMeters != null)
                    s.AltitudeMeters = p.AltitudeMeters;
                if (moved && p.Ambiguity == 0)
                {
                    s.Track.Add(new AprsTrackPoint(nowUtc, p.Latitude!.Value, p.Longitude!.Value));
                    if (s.Track.Count > MaxTrackPoints)
                        s.Track.RemoveRange(0, s.Track.Count - MaxTrackPoints);
                }
            }
            if (p.Comment.Length > 0)
                s.Comment = p.Comment;
            if (p.Weather != null)
                s.Weather = p.Weather;
            if (p.MicEMessage != null)
                s.MicEMessage = p.MicEMessage;
            s.Emergency = p.Emergency;
            return s;
        }
    }

    private void RemoveOldest()
    {
        var oldest = _stations.Values.OrderBy(s => s.LastHeardUtc).First();
        _stations.Remove(oldest.Key);
    }

    private void Log(AprsLogEntry e)
    {
        _log.AddLast(e);
        while (_log.Count > MaxLog)
            _log.RemoveFirst();
    }

    /// <summary>Drops stations not heard for <paramref name="maxAge"/> and track points older than <paramref name="trackAge"/>.</summary>
    public int Purge(DateTime nowUtc, TimeSpan maxAge, TimeSpan trackAge)
    {
        lock (_lock)
        {
            var old = _stations.Values.Where(s => nowUtc - s.LastHeardUtc > maxAge).Select(s => s.Key).ToList();
            foreach (var k in old)
                _stations.Remove(k);
            foreach (var s in _stations.Values)
                s.Track.RemoveAll(t => nowUtc - t.TimeUtc > trackAge);
            if (old.Count > 0)
                _version++;
            return old.Count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _stations.Clear();
            _recent.Clear();
            _version++;
        }
    }

    /// <summary>Copies of the stations (tracks copied too), safe to use on another thread.</summary>
    public List<AprsStation> Snapshot()
    {
        lock (_lock)
        {
            return _stations.Values.Select(Copy).ToList();
        }
    }

    public AprsStation? Find(string key)
    {
        lock (_lock)
            return _stations.TryGetValue(key, out var s) ? Copy(s) : null;
    }

    private static AprsStation Copy(AprsStation s)
    {
        var c = new AprsStation
        {
            Key = s.Key, Source = s.Source, IsObject = s.IsObject, SymbolTable = s.SymbolTable, SymbolCode = s.SymbolCode, Lat = s.Lat, Lon = s.Lon,
            Ambiguity = s.Ambiguity, PositionUtc = s.PositionUtc, LastHeardUtc = s.LastHeardUtc, Via = s.Via, LastVia = s.LastVia, Path = s.Path,
            Comment = s.Comment, Status = s.Status, CourseDegrees = s.CourseDegrees, SpeedKnots = s.SpeedKnots, AltitudeMeters = s.AltitudeMeters,
            Weather = s.Weather, MicEMessage = s.MicEMessage, Emergency = s.Emergency, Packets = s.Packets, LastRaw = s.LastRaw
        };
        c.Track.AddRange(s.Track);
        return c;
    }

    public List<AprsLogEntry> LogSnapshot()
    {
        lock (_lock)
            return _log.ToList();
    }

    /// <summary>One-line description for the packet log.</summary>
    public static string Summary(AprsPacket p)
    {
        if (p.Error != null)
            return p.Error;
        var who = p.ObjectName != null ? $"{p.ObjectName} (by {p.Source})" : p.Source;
        return p.Type switch
        {
            AprsPacketType.Position or AprsPacketType.MicE or AprsPacketType.Object or AprsPacketType.Item or AprsPacketType.Weather when p.HasPosition =>
                $"{who}: {AprsSymbols.Name(p.SymbolTable, p.SymbolCode)} at {p.Latitude:0.0000}, {p.Longitude:0.0000}" +
                (p.MicEMessage != null ? $" [{p.MicEMessage}]" : "") + (p.Weather != null ? $" · {p.Weather}" : ""),
            AprsPacketType.Weather => $"{who}: weather {p.Weather}",
            AprsPacketType.Status => $"{who}: status \"{p.Status}\"",
            AprsPacketType.Message => $"{who} → {p.Addressee}: {p.MessageText}",
            AprsPacketType.Telemetry => $"{who}: telemetry",
            _ => $"{who}: {p.Information}"
        };
    }
}
