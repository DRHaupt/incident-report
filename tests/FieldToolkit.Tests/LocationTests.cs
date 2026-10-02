using Shared.Core.Geo;
using FieldToolkit.Core.Radio;
using Shared.Core.Radio;

namespace FieldToolkit.Tests;

public class LocationTests
{
    [Fact]
    public void Utm_of_origin_matches_reference()
    {
        var u = Utm.FromLatLon(0, 0);
        Assert.Equal(31, u.Zone);
        Assert.Equal('N', u.Band);
        Assert.Equal(166021.443, u.Easting, 0.01);
        Assert.Equal(0, u.Northing, 0.01);
    }

    [Fact]
    public void Utm_central_meridian_is_false_easting()
    {
        var u = Utm.FromLatLon(0, 3);
        Assert.Equal(500000, u.Easting, 0.001);
    }

    [Fact]
    public void Utm_cn_tower_reference()
    {
        // Worked example from the Universal Transverse Mercator article: CN Tower = 17T 630084 4833438.
        var u = Utm.FromLatLon(43.642567, -79.387139);
        Assert.Equal("17T", $"{u.Zone}{u.Band}");
        Assert.Equal(630084, u.Easting, 1.0);
        Assert.Equal(4833438, u.Northing, 1.0);
    }

    [Theory]
    [InlineData(60, 5, 32)]     // Norway exception
    [InlineData(78, 10, 33)]    // Svalbard
    [InlineData(78, 8, 31)]
    [InlineData(51.5, -0.12, 30)]
    [InlineData(-33.87, 151.21, 56)]
    public void Zone_exceptions(double lat, double lon, int zone) => Assert.Equal(zone, Utm.ZoneFor(lat, lon));

    public static IEnumerable<object[]> Points() => new[]
    {
        new object[] { 51.50735, -0.12776 }, new object[] { -33.8688, 151.2093 }, new object[] { 40.7128, -74.0060 },
        new object[] { -54.8019, -68.3030 }, new object[] { 64.1466, -21.9426 }, new object[] { 0.0001, 179.9 },
        new object[] { 83.5, 20 }, new object[] { -79.9, -120 }, new object[] { 60.5, 7.5 }, new object[] { 1.3521, 103.8198 }
    };

    [Theory]
    [MemberData(nameof(Points))]
    public void Utm_round_trips(double lat, double lon)
    {
        var u = Utm.FromLatLon(lat, lon);
        var (la, lo) = Utm.ToLatLon(u.Zone, u.North, u.Easting, u.Northing);
        Assert.Equal(lat, la, 1e-7);
        Assert.Equal(lon, lo, 1e-7);
    }

    [Fact]
    public void Mgrs_of_origin_matches_reference() => Assert.Equal("31N AA 66021 00000", Mgrs.FromLatLon(0, 0));

    [Theory]
    [MemberData(nameof(Points))]
    public void Mgrs_round_trips_within_two_metres(double lat, double lon)
    {
        var m = Mgrs.FromLatLon(lat, lon);
        Assert.True(Mgrs.TryParse(m, out var la, out var lo), m);
        Assert.True(GeoMath.Distance(lat, lon, la, lo) < 2, $"{m}: {GeoMath.Distance(lat, lon, la, lo):0.0} m");
        // Also without spaces and at lower precision.
        Assert.True(Mgrs.TryParse(m.Replace(" ", ""), out la, out lo));
        Assert.True(Mgrs.TryParse(Mgrs.FromLatLon(lat, lon, 3), out la, out lo));
        Assert.True(GeoMath.Distance(lat, lon, la, lo) < 120);
    }

    [Fact]
    public void Dms_and_ddm_formatting()
    {
        Assert.Equal("51°30′26.5″N 0°07′39.9″W", CoordinateFormat.Dms(51.50735, -0.127758));
        Assert.Equal("51°30.441′N 0°07.665′W", CoordinateFormat.Ddm(51.50735, -0.127758));
        Assert.Equal("51.50735, -0.12776", CoordinateFormat.Decimal(51.50735, -0.127758));
        Assert.Equal("33°52′07.7″S 151°12′33.5″E", CoordinateFormat.Dms(-33.8688, 151.2093));
    }

    [Theory]
    [InlineData("51.50735, -0.12776")]
    [InlineData("51.50735 -0.12776")]
    [InlineData("51.50735N 0.12776W")]
    [InlineData("N51 30.441 W0 07.666")]
    [InlineData("51°30′26.5″N 0°07′40.0″W")]
    [InlineData("51 30 26.5 N, 0 7 40 W")]
    [InlineData("0°07′40″W 51°30′26.5″N")]
    [InlineData("30U XC 99319 10158")]
    public void Parses_common_formats(string text)
    {
        Assert.True(CoordinateFormat.TryParse(text, out var lat, out var lon), text);
        // MGRS reference above was produced by this library for the same point (~1 m squares).
        Assert.True(GeoMath.Distance(51.50735, -0.12776, lat, lon) < 30, $"{text} -> {lat}, {lon}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("95, 10")]
    [InlineData("51 75 00 N 0 07 W")]
    public void Rejects_invalid(string text) => Assert.False(CoordinateFormat.TryParse(text, out _, out _));

    [Fact]
    public void Distance_and_bearing_london_to_paris()
    {
        var d = GeoMath.Distance(51.5074, -0.1278, 48.8566, 2.3522);
        Assert.InRange(d, 342000, 345500);
        var b = GeoMath.Bearing(51.5074, -0.1278, 48.8566, 2.3522);
        Assert.InRange(b, 145, 152);
        Assert.Equal("SSE", GeoMath.CompassPoint(b));
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(359, "N")]
    [InlineData(45, "NE")]
    [InlineData(200, "SSW")]
    [InlineData(270, "W")]
    public void Compass_points(double bearing, string point) => Assert.Equal(point, GeoMath.CompassPoint(bearing));

    [Fact]
    public void Distance_formatting() => Assert.Equal("850 m (0.53 mi)", GeoMath.FormatDistance(850));

    [Fact]
    public void Spoken_grid_reference_uses_radio_figures()
    {
        Assert.Equal("TREE WUN NOVEMBER / ALFA ALFA / SIX SIX ZE-RO TOO WUN / ZE-RO ZE-RO ZE-RO ZE-RO ZE-RO",
            CoordinateFormat.SpokenMgrs("31N AA 66021 00000"));
    }

    [Fact]
    public void Nmea_gga_and_rmc_reference_sentences()
    {
        var reader = new NmeaReader();
        var now = DateTime.UtcNow;
        var gga = reader.Feed("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47", now);
        Assert.NotNull(gga);
        Assert.Equal(48.1173, gga!.Latitude, 1e-4);
        Assert.Equal(11.516667, gga.Longitude, 1e-5);
        Assert.Equal(8, gga.Satellites);
        Assert.Equal(0.9, gga.Hdop);
        Assert.Equal(545.4, gga.AltitudeMeters);

        var rmc = reader.Feed("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A", now);
        Assert.NotNull(rmc);
        Assert.Equal(84.4, rmc!.CourseDegrees);
        Assert.Equal(22.4 * 0.514444, rmc.SpeedMetersPerSecond!.Value, 1e-3);
    }

    [Theory]
    [InlineData("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*48")] // bad checksum
    [InlineData("$GPGGA,123519,4807.038,N,01131.000,E,0,00,,,M,,M,,")]                // no fix
    [InlineData("$GPRMC,123519,V,4807.038,N,01131.000,E,,,230394,,")]                 // void
    [InlineData("garbage")]
    public void Nmea_rejects_invalid(string sentence) => Assert.Null(new NmeaReader().Feed(sentence, DateTime.UtcNow));

    [Fact]
    public void Nmea_south_west_and_gn_talker()
    {
        var body = "GNRMC,010203,A,3352.128,S,15112.558,E,0.0,0.0,010126,,";
        var sentence = $"${body}*{NmeaReader.Checksum(body):X2}";
        var fix = new NmeaReader().Feed(sentence, DateTime.UtcNow);
        Assert.NotNull(fix);
        Assert.Equal(-33.8688, fix!.Latitude, 1e-4);
        Assert.Equal(151.2093, fix.Longitude, 1e-4);
    }
}

public class PhoneticTests
{
    [Fact]
    public void Alphabet_is_complete() => Assert.Equal(26, Phonetic.Letters.Select(l => l.Character).Distinct().Count());

    [Theory]
    [InlineData("AB12", "Alfa Bravo One Two")]
    [InlineData("rally 7", "Romeo Alfa Lima Lima Yankee / Seven")]
    [InlineData("X-9", "X-ray Nine")]
    public void Spells_text(string text, string expected) => Assert.Equal(expected, Phonetic.Spell(text));

    [Fact]
    public void Radio_figures() => Assert.Equal("NIN-ER FIFE TREE FOW-ER", Phonetic.Figures1By1("9534"));
}
