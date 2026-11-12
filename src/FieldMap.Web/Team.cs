using System.Text.Json;

namespace FieldMap.Web;

/// <summary>
/// Team features for phones: live unit positions (opt-in, with the last hour's trail) and shared incident pins.
/// Phones identify themselves with a random device id and a name or callsign they choose; the trust boundary is the
/// server's access code, so these features are only on when an access code is set.
/// Positions live in memory only. Pins are saved to &lt;DataDir&gt;/shared/pins.json and kept until someone removes them.
/// </summary>
public sealed class Team
{
    public const int MaxNameLength = 40;
    public const int MaxLabelLength = 60;
    public const int MaxNoteLength = 500;
    public const int MaxPins = 500;
    public const int MaxUnits = 200;
    public const int MaxTrailPoints = 720;

    /// <summary>A unit's position greys out after this long without an update…</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    /// <summary>…and is dropped after this long.</summary>
    public static readonly TimeSpan DropAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TrailLength = TimeSpan.FromHours(1);

    /// <summary>Kinds of pin, shown with their own symbol and colour on the phones.</summary>
    public static readonly string[] PinKinds = { "hazard", "staging", "closure", "command", "medical", "note" };

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _lock = new();
    private readonly Dictionary<string, Unit> _units = new();
    private readonly List<Pin> _pins;
    private readonly string _pinsFile;
    private readonly Func<DateTime> _now;
    private long _revision = 1;

    public Team(WebOptions options) : this(options, () => DateTime.UtcNow) { }

    public Team(WebOptions options, Func<DateTime> now)
    {
        _now = now;
        _pinsFile = Path.Combine(options.DataDir, "shared", "pins.json");
        _pins = LoadPins(_pinsFile);
    }

    /// <summary>Changes whenever a position or pin changes, so phones can skip redrawing when nothing moved.</summary>
    public long Revision { get { lock (_lock) return _revision; } }

    // ------------------------------------------------------------------ positions

    public sealed class Unit
    {
        public required string Device { get; init; }
        public required string Name { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double? Accuracy { get; set; }
        public double? Heading { get; set; }
        public double? Speed { get; set; }
        public DateTime Time { get; set; }
        public List<TrailPoint> Trail { get; } = new();
    }

    public readonly record struct TrailPoint(double Lat, double Lon, DateTime Time);

    /// <summary>Records a unit's position. Returns null when accepted, or the reason it wasn't.</summary>
    public string? Report(string? device, string? name, double lat, double lon, double? accuracy, double? heading, double? speed)
    {
        if (CleanDevice(device) is not { } id) return "Missing device id.";
        if (CleanName(name) is not { } who) return "Set a name or callsign first.";
        if (!ValidPoint(lat, lon)) return "That position isn't valid.";
        var now = _now();
        lock (_lock)
        {
            Prune(now);
            if (!_units.TryGetValue(id, out var u))
            {
                if (_units.Count >= MaxUnits) return "Too many units are sharing their position.";
                u = new Unit { Device = id, Name = who };
                _units[id] = u;
            }
            u.Name = who;
            u.Lat = lat; u.Lon = lon; u.Time = now;
            u.Accuracy = Finite(accuracy); u.Heading = Finite(heading); u.Speed = Finite(speed);
            // A trail point every 15 m or 30 s, whichever comes first, so a parked unit doesn't fill its trail.
            var last = u.Trail.Count > 0 ? u.Trail[^1] : (TrailPoint?)null;
            if (last is not { } l || Meters(l.Lat, l.Lon, lat, lon) >= 15 || now - l.Time >= TimeSpan.FromSeconds(30))
            {
                u.Trail.Add(new TrailPoint(lat, lon, now));
                if (u.Trail.Count > MaxTrailPoints) u.Trail.RemoveRange(0, u.Trail.Count - MaxTrailPoints);
            }
            _revision++;
        }
        return null;
    }

    /// <summary>A unit stopped sharing: removes its position and trail straight away.</summary>
    public void StopSharing(string? device)
    {
        if (CleanDevice(device) is not { } id) return;
        lock (_lock)
            if (_units.Remove(id)) _revision++;
    }

    public List<object> Units()
    {
        var now = _now();
        lock (_lock)
        {
            Prune(now);
            return _units.Values.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(u => (object)new
            {
                device = u.Device, name = u.Name, lat = u.Lat, lon = u.Lon, accuracy = u.Accuracy, heading = u.Heading, speed = u.Speed,
                time = u.Time, ageSeconds = Math.Round((now - u.Time).TotalSeconds), stale = now - u.Time > StaleAfter,
                trail = u.Trail.Select(p => new[] { Math.Round(p.Lon, 6), Math.Round(p.Lat, 6) }).ToArray()
            }).ToList();
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var id in _units.Where(kv => now - kv.Value.Time > DropAfter).Select(kv => kv.Key).ToList())
        {
            _units.Remove(id);
            _revision++;
        }
        foreach (var u in _units.Values)
            u.Trail.RemoveAll(p => now - p.Time > TrailLength);
    }

    // ------------------------------------------------------------------ SOS alerts

    /// <summary>An SOS stays up for this long unless the sender cancels it (or sends it again).</summary>
    public static readonly TimeSpan AlertLasts = TimeSpan.FromHours(1);
    public const int MaxMessageLength = 140;

    private sealed class Alert
    {
        public required string Device { get; init; }
        public required string Name { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double? Accuracy { get; set; }
        public string Message { get; set; } = "";
        public DateTime StartedUtc { get; init; }
        public DateTime UpdatedUtc { get; set; }
    }

    private readonly Dictionary<string, Alert> _alerts = new();

    /// <summary>Raises an SOS, or updates its position while it's active. Returns null when accepted, or why not.</summary>
    public string? RaiseAlert(string? device, string? name, double lat, double lon, double? accuracy, string? message)
    {
        if (CleanDevice(device) is not { } id) return "Missing device id.";
        if (CleanName(name) is not { } who) return "Set a name or callsign first.";
        if (!ValidPoint(lat, lon)) return "That position isn't valid.";
        var now = _now();
        lock (_lock)
        {
            PruneAlerts(now);
            if (!_alerts.TryGetValue(id, out var a))
            {
                if (_alerts.Count >= MaxUnits) return "Too many alerts.";
                a = new Alert { Device = id, Name = who, StartedUtc = now };
                _alerts[id] = a;
            }
            a.Name = who;
            a.Lat = Math.Round(lat, 6); a.Lon = Math.Round(lon, 6); a.Accuracy = Finite(accuracy);
            if (message != null) a.Message = Clean(message.Replace('\n', ' '), MaxMessageLength);
            a.UpdatedUtc = now;
            _revision++;
        }
        return null;
    }

    public bool CancelAlert(string? device)
    {
        if (CleanDevice(device) is not { } id) return false;
        lock (_lock)
        {
            if (!_alerts.Remove(id)) return false;
            _revision++;
            return true;
        }
    }

    public List<object> Alerts()
    {
        var now = _now();
        lock (_lock)
        {
            PruneAlerts(now);
            return _alerts.Values.OrderBy(a => a.StartedUtc).Select(a => (object)new
            {
                device = a.Device, name = a.Name, lat = a.Lat, lon = a.Lon, accuracy = a.Accuracy, message = a.Message,
                started = a.StartedUtc, updated = a.UpdatedUtc, ageSeconds = Math.Round((now - a.StartedUtc).TotalSeconds)
            }).ToList();
        }
    }

    private void PruneAlerts(DateTime now)
    {
        foreach (var id in _alerts.Where(kv => now - kv.Value.UpdatedUtc > AlertLasts).Select(kv => kv.Key).ToList())
        {
            _alerts.Remove(id);
            _revision++;
        }
    }

    // ------------------------------------------------------------------ pins

    public sealed class Pin
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "note";
        public string Label { get; set; } = "";
        public string Note { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string UpdatedBy { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed record PinInput(string? Device, string? Name, string? Kind, string? Label, string? Note, double? Lat, double? Lon);

    public List<Pin> Pins()
    {
        lock (_lock)
            return _pins.Select(Copy).ToList();
    }

    /// <summary>Adds a pin. Returns the pin, or the reason it wasn't added.</summary>
    public (Pin? Pin, string? Error) AddPin(PinInput input)
    {
        if (CleanName(input.Name) is not { } who) return (null, "Set a name or callsign first.");
        if (input.Lat is not { } lat || input.Lon is not { } lon || !ValidPoint(lat, lon)) return (null, "That position isn't valid.");
        var kind = CleanKind(input.Kind);
        var label = Clean(input.Label, MaxLabelLength);
        if (label.Length == 0) return (null, "Give the pin a label.");
        var now = _now();
        lock (_lock)
        {
            if (_pins.Count >= MaxPins) return (null, $"There are already {MaxPins} shared pins. Remove some first.");
            var pin = new Pin
            {
                Id = Guid.NewGuid().ToString("N")[..12], Kind = kind, Label = label, Note = Clean(input.Note, MaxNoteLength),
                Lat = Math.Round(lat, 6), Lon = Math.Round(lon, 6), CreatedBy = who, CreatedUtc = now, UpdatedBy = who, UpdatedUtc = now
            };
            _pins.Add(pin);
            SaveLocked();
            return (Copy(pin), null);
        }
    }

    /// <summary>Changes a pin; anyone signed in may. Fields left out stay as they are.</summary>
    public (Pin? Pin, string? Error) UpdatePin(string id, PinInput input)
    {
        if (CleanName(input.Name) is not { } who) return (null, "Set a name or callsign first.");
        lock (_lock)
        {
            var pin = _pins.FirstOrDefault(p => p.Id == id);
            if (pin == null) return (null, null);
            if (input.Lat is { } lat && input.Lon is { } lon)
            {
                if (!ValidPoint(lat, lon)) return (null, "That position isn't valid.");
                pin.Lat = Math.Round(lat, 6); pin.Lon = Math.Round(lon, 6);
            }
            if (input.Kind != null) pin.Kind = CleanKind(input.Kind);
            if (input.Label != null)
            {
                var label = Clean(input.Label, MaxLabelLength);
                if (label.Length == 0) return (null, "Give the pin a label.");
                pin.Label = label;
            }
            if (input.Note != null) pin.Note = Clean(input.Note, MaxNoteLength);
            pin.UpdatedBy = who;
            pin.UpdatedUtc = _now();
            SaveLocked();
            return (Copy(pin), null);
        }
    }

    public bool RemovePin(string id)
    {
        lock (_lock)
        {
            if (_pins.RemoveAll(p => p.Id == id) == 0) return false;
            SaveLocked();
            return true;
        }
    }

    /// <summary>Removes every pin (after an incident). Returns how many there were.</summary>
    public int ClearPins()
    {
        lock (_lock)
        {
            var n = _pins.Count;
            _pins.Clear();
            SaveLocked();
            return n;
        }
    }

    private void SaveLocked()
    {
        _revision++;
        Directory.CreateDirectory(Path.GetDirectoryName(_pinsFile)!);
        // Write a new file and swap it in, so a crash mid-write never leaves half a file.
        var temp = _pinsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_pins, FileJson));
        File.Move(temp, _pinsFile, overwrite: true);
    }

    private static List<Pin> LoadPins(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<Pin>>(File.ReadAllText(file), FileJson) ?? new() : new();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for someone to look at; start with no pins rather than refusing to start.
            try { File.Move(file, file + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true); } catch (IOException) { }
            return new();
        }
    }

    private static Pin Copy(Pin p) => new()
    {
        Id = p.Id, Kind = p.Kind, Label = p.Label, Note = p.Note, Lat = p.Lat, Lon = p.Lon,
        CreatedBy = p.CreatedBy, CreatedUtc = p.CreatedUtc, UpdatedBy = p.UpdatedBy, UpdatedUtc = p.UpdatedUtc
    };

    // ------------------------------------------------------------------ helpers

    private static string Clean(string? s, int max)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    internal static string? CleanName(string? s) => Clean(s?.Replace('\n', ' '), MaxNameLength) is { Length: > 0 } n ? n : null;

    // Device ids are random strings made by the phone; only letters, digits and dashes, 8–64 long.
    private static string? CleanDevice(string? s) =>
        s is { Length: >= 8 and <= 64 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? s : null;

    private static string CleanKind(string? k) => PinKinds.Contains(k) ? k! : "note";

    private static bool ValidPoint(double lat, double lon) => double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    private static double? Finite(double? v) => v is { } d && double.IsFinite(d) ? Math.Round(d, 1) : null;

    private static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        var k = Math.Cos(lat1 * Math.PI / 180);
        return Math.Sqrt(Math.Pow((lat2 - lat1) * 111_195, 2) + Math.Pow((lon2 - lon1) * 111_195 * k, 2));
    }
}

/// <summary>The team endpoints. Off (403 "team-off") unless the server has an access code.</summary>
public static class TeamApi
{
    public sealed record PositionInput(string? Device, string? Name, double Lat, double Lon, double? Accuracy, double? Heading, double? Speed);
    public sealed record AlertInput(string? Device, string? Name, double Lat, double Lon, double? Accuracy, string? Message);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/team").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<AccessGate>().Enabled
                ? await next(ctx)
                : Results.Json(new { error = "team-off", message = "Team features need an access code on the server (AccessCode in /etc/fieldmap-web/appsettings.json)." }, statusCode: 403));

        // Everything a phone needs in one call, polled every few seconds while the app is open.
        group.MapGet("", (Team team) => Results.Json(new
        {
            revision = team.Revision,
            staleSeconds = Team.StaleAfter.TotalSeconds,
            kinds = Team.PinKinds,
            units = team.Units(),
            alerts = team.Alerts(),
            pins = team.Pins()
        }));

        group.MapPost("/position", (PositionInput p, Team team) =>
            team.Report(p.Device, p.Name, p.Lat, p.Lon, p.Accuracy, p.Heading, p.Speed) is { } error
                ? Results.Json(new { error }, statusCode: 400)
                : Results.Json(new { ok = true }));

        group.MapDelete("/position/{device}", (string device, Team team) =>
        {
            team.StopSharing(device);
            return Results.Json(new { ok = true });
        });

        // SOS: every team phone shows a banner and buzzes until the sender cancels it (or an hour passes).
        group.MapPost("/sos", (AlertInput a, Team team) =>
            team.RaiseAlert(a.Device, a.Name, a.Lat, a.Lon, a.Accuracy, a.Message) is { } error
                ? Results.Json(new { error }, statusCode: 400)
                : Results.Json(new { ok = true }));

        group.MapDelete("/sos/{device}", (string device, Team team) => Results.Json(new { ok = team.CancelAlert(device) }));

        group.MapPost("/pins", (Team.PinInput input, Team team) =>
        {
            var (pin, error) = team.AddPin(input);
            return pin != null ? Results.Json(pin) : Results.Json(new { error }, statusCode: 400);
        });

        group.MapPut("/pins/{id}", (string id, Team.PinInput input, Team team) =>
        {
            var (pin, error) = team.UpdatePin(id, input);
            return pin != null ? Results.Json(pin) : error != null ? Results.Json(new { error }, statusCode: 400) : Results.NotFound();
        });

        group.MapDelete("/pins/{id}", (string id, Team team) => team.RemovePin(id) ? Results.Json(new { ok = true }) : Results.NotFound());

        group.MapDelete("/pins", (Team team) => Results.Json(new { removed = team.ClearPins() }));
    }
}
