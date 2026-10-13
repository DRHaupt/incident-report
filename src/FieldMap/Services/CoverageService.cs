using FieldMap.Core.Radio;
using FieldMap.Core.Terrain;

namespace FieldMap.Services;

/// <summary>Predicted coverage of repeaters, computed on request over the installed elevation data and shown on the map.</summary>
public sealed class CoverageService
{
    /// <summary>Used when a repeater has no antenna height or power on record.</summary>
    public const double DefaultAntennaHeightM = 15;
    public const double DefaultErpWatts = 50;

    public sealed record Item(Repeater Repeater, bool Vehicle, CoverageResult Result);

    private readonly List<Item> _items = new();

    public IReadOnlyList<Item> Items => _items;
    public IReadOnlyList<CoverageResult> Results => _items.Select(i => i.Result).ToList();
    public bool Busy { get; private set; }

    public event EventHandler? Changed;

    public Item? For(string repeaterId) => _items.FirstOrDefault(i => i.Repeater.Id == repeaterId);

    public static CoverageRequest RequestFor(Repeater r, bool vehicle, double radiusKm)
    {
        var baseReq = vehicle ? CoverageRequest.Vehicle(r.Lat, r.Lon) : CoverageRequest.Handheld(r.Lat, r.Lon);
        return baseReq with
        {
            FrequencyMHz = r.OutputMHz,
            AntennaHeightM = r.AntennaHeightM ?? DefaultAntennaHeightM,
            ErpWatts = r.ErpWatts ?? DefaultErpWatts,
            RadiusKm = Math.Clamp(radiusKm, 5, 150),
            // Digital modes need a few dB more than analogue FM for a usable signal.
            SensitivityDbm = r.Mode.ToUpperInvariant() is "DMR" or "P25" or "NXDN" or "D-STAR" or "FUSION" ? -113 : -116
        };
    }

    /// <summary>Computes (or recomputes) a repeater's coverage. Returns a message for the user.</summary>
    public async Task<string> ComputeAsync(Repeater r, bool vehicle, double radiusKm, TerrainGrid? terrain)
    {
        if (terrain == null)
            return "No elevation data covers this repeater. Run Update map data (Data & updates) – it now includes elevation for BC and Alberta.";
        if (r.OutputMHz is < 30 or > 3000)
            return $"Coverage can be predicted for 30–3000 MHz; this repeater is on {r.OutputMHz:0.###} MHz.";
        Busy = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var req = RequestFor(r, vehicle, radiusKm);
            var result = await Task.Run(() => RadioCoverage.Compute(terrain, req));
            _items.RemoveAll(i => i.Repeater.Id == r.Id);
            _items.Add(new Item(r, vehicle, result));
            return $"{r.Label}: likely {result.LikelyFraction:P0}, marginal {result.MarginalFraction:P0} of the {req.RadiusKm:0} km circle ({(vehicle ? "vehicle radio" : "handheld")}).";
        }
        finally
        {
            Busy = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Remove(string repeaterId)
    {
        if (_items.RemoveAll(i => i.Repeater.Id == repeaterId) > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_items.Count == 0)
            return;
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
