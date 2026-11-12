using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FieldMap.Core.Geo;

namespace FieldMap.Web.Tests;

/// <summary>APRS-IS on the server: receive-only login with the regions' area filter, stations served to phones.</summary>
public sealed class AprsTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fx;

    public AprsTests(ServerFixture fx) => _fx = fx;

    [Fact]
    public void Area_filter_has_one_box_per_region_plus_extra_terms()
    {
        var bc = new GeoBounds(48.2, -139.1, 60.0, -114.0);
        var ab = new GeoBounds(48.9, -120.0, 60.0, -110.0);
        Assert.Equal("a/60/-139.1/48.2/-114 a/60/-120/48.9/-110 b/VE7*", AprsHub.AreaFilter(new[] { bc, ab }, " b/VE7* "));
        Assert.Equal("", AprsHub.AreaFilter(Array.Empty<GeoBounds>(), ""));
    }

    [Fact]
    public async Task Off_unless_enabled_in_the_settings()
    {
        var client = _fx.Default.CreateClient();
        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.False(info.GetProperty("aprs").GetBoolean());
        var aprs = await client.GetFromJsonAsync<JsonElement>("/api/aprs");
        Assert.False(aprs.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Logs_in_receive_only_with_the_area_filter_and_serves_the_stations_it_hears()
    {
        // A stand-in APRS-IS server: checks the login line, answers like a real one, then sends three packets.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? login = null;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("# aprsc 2.1.14 test");
            login = await reader.ReadLineAsync();
            await writer.WriteLineAsync("# logresp FMWEB1234 unverified, server T2TEST");
            await writer.WriteLineAsync("VE7ABC-9>APRS,TCPIP*,qAC,T2TEST:!5724.36N/12554.18W>090/030Moving to the fair");
            await writer.WriteLineAsync("VE7WX>APRS,TCPIP*:@120000z5724.00N/12554.00W_180/005g010t045Weather");
            await writer.WriteLineAsync("VE7XYZ>APRS,TCPIP*:>status only, no position");
            await Task.Delay(TimeSpan.FromSeconds(20)); // stay connected while the test reads
        });

        using var factory = _fx.Factory().WithWebHostBuilder(b =>
        {
            b.UseSetting("FieldMap:AprsIs:Enabled", "true");
            b.UseSetting("FieldMap:AprsIs:Server", "127.0.0.1");
            b.UseSetting("FieldMap:AprsIs:Port", port.ToString());
        });
        var client = factory.CreateClient();

        JsonElement aprs = default;
        for (var i = 0; i < 50; i++)
        {
            aprs = await client.GetFromJsonAsync<JsonElement>("/api/aprs");
            if (aprs.TryGetProperty("stations", out var s) && s.GetArrayLength() >= 2) break;
            await Task.Delay(200);
        }

        Assert.NotNull(login);
        Assert.StartsWith("user FMWEB", login);
        Assert.Contains(" pass -1 ", login);               // receive-only, never a real passcode
        Assert.Matches(@"filter a/[\d.]+/-[\d.]+/[\d.]+/-[\d.]+$", login); // the sample region's box

        Assert.True(aprs.GetProperty("enabled").GetBoolean());
        Assert.Equal("connected", aprs.GetProperty("status").GetProperty("state").GetString());
        var stations = aprs.GetProperty("stations").EnumerateArray().ToList();
        Assert.Equal(2, stations.Count); // the status-only station has no position
        var car = stations.Single(s => s.GetProperty("key").GetString() == "VE7ABC-9");
        Assert.Equal("vehicle", car.GetProperty("group").GetString());
        Assert.Equal(57.406, car.GetProperty("lat").GetDouble(), 3);
        Assert.Equal(56, car.GetProperty("speedKmh").GetDouble()); // 30 knots
        var wx = stations.Single(s => s.GetProperty("key").GetString() == "VE7WX");
        Assert.Equal("weather", wx.GetProperty("group").GetString());

        // Asking again with the version it has: nothing changed, so no station list.
        var version = aprs.GetProperty("version").GetInt32();
        var again = await client.GetFromJsonAsync<JsonElement>($"/api/aprs?v={version}");
        Assert.True(again.GetProperty("unchanged").GetBoolean());
        Assert.False(again.TryGetProperty("stations", out _));

        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.True(info.GetProperty("aprs").GetBoolean());
        listener.Stop();
    }
}
