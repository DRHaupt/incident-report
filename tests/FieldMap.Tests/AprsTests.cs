using System.Net;
using System.Net.Sockets;
using System.Text;
using FieldMap.Core.Aprs;

namespace FieldMap.Tests;

public class AprsParserTests
{
    [Fact]
    public void UncompressedPositionWithoutTimestamp()
    {
        // APRS Protocol Reference 1.0.1, chapter 8 example.
        var p = AprsParser.Parse("N0CALL>APRS,WIDE1-1:!4903.50N/07201.75W-Test 001234");
        Assert.Null(p.Error);
        Assert.Equal(AprsPacketType.Position, p.Type);
        Assert.Equal("N0CALL", p.Source);
        Assert.Equal("APRS", p.Destination);
        Assert.Equal(new[] { "WIDE1-1" }, p.Path);
        Assert.Equal(49 + 3.5 / 60, p.Latitude!.Value, 6);
        Assert.Equal(-(72 + 1.75 / 60), p.Longitude!.Value, 6);
        Assert.Equal('/', p.SymbolTable);
        Assert.Equal('-', p.SymbolCode);
        Assert.Equal("Test 001234", p.Comment);
        Assert.Equal("House", AprsSymbols.Name(p.SymbolTable, p.SymbolCode));
    }

    [Fact]
    public void PositionWithTimestampCourseSpeedAndAltitude()
    {
        var p = AprsParser.Parse("VE7XYZ-9>APRS,TCPIP*,qAC,T2TEST:@092345z4903.50N/07201.75W>088/036/A=001234 Patrol 3");
        Assert.Equal(88, p.CourseDegrees);
        Assert.Equal(36, p.SpeedKnots);
        Assert.Equal(1234 * 0.3048, p.AltitudeMeters!.Value, 3);
        Assert.Equal("Patrol 3", p.Comment);
        Assert.Equal("Car", AprsSymbols.Name('/', p.SymbolCode));
    }

    [Fact]
    public void CompressedPosition()
    {
        // Spec example "/5L!!<*e7>7P[": 49°30'N 72°45'W, course 88°, speed 36.2 knots.
        var p = AprsParser.Parse("N0CALL>APRS:=/5L!!<*e7>7P[");
        Assert.Null(p.Error);
        Assert.Equal(49.5, p.Latitude!.Value, 4);
        Assert.Equal(-72.75, p.Longitude!.Value, 4);
        Assert.Equal('>', p.SymbolCode);
        Assert.Equal(88, p.CourseDegrees);
        Assert.Equal(36.2, p.SpeedKnots!.Value, 1);
    }

    [Fact]
    public void MicE()
    {
        // Destination S32UVT = 33°25.64'N, message "Returning", longitude offset +100, west. Longitude 112°07.74'W,
        // 36 knots, course 251°, car.
        var p = AprsParser.Parse("N0CALL-9>S32UVT,WIDE1-1:`(#foZO>/\"4T}Hello");
        Assert.Null(p.Error);
        Assert.Equal(AprsPacketType.MicE, p.Type);
        Assert.Equal(33 + 25.64 / 60, p.Latitude!.Value, 6);
        Assert.Equal(-(112 + 7.74 / 60), p.Longitude!.Value, 6);
        Assert.Equal(36, p.SpeedKnots);
        Assert.Equal(251, p.CourseDegrees);
        Assert.Equal('>', p.SymbolCode);
        Assert.Equal('/', p.SymbolTable);
        Assert.Equal("Returning", p.MicEMessage);
        Assert.False(p.Emergency);
        Assert.Equal(61, p.AltitudeMeters);
        Assert.Equal("Hello", p.Comment);
    }

    [Fact]
    public void MicEEmergency()
    {
        var p = AprsParser.Parse("N0CALL-9>332U6T:`(#foZO>/");
        Assert.Equal("EMERGENCY", p.MicEMessage);
        Assert.True(p.Emergency);
    }

    [Fact]
    public void ObjectAndItem()
    {
        var o = AprsParser.Parse("VE7EOC>APRS:;LEADER   *092345z4903.50N/07201.75W>088/036");
        Assert.Equal(AprsPacketType.Object, o.Type);
        Assert.Equal("LEADER", o.ObjectName);
        Assert.Equal("LEADER", o.Key);
        Assert.True(o.Alive);
        Assert.Equal(88, o.CourseDegrees);

        var killed = AprsParser.Parse("VE7EOC>APRS:;LEADER   _092345z4903.50N/07201.75W>");
        Assert.False(killed.Alive);

        var i = AprsParser.Parse("VE7EOC>APRS:)AID #2!4903.50N/07201.75WA");
        Assert.Equal(AprsPacketType.Item, i.Type);
        Assert.Equal("AID #2", i.ObjectName);
        Assert.Equal('A', i.SymbolCode);
        Assert.Equal("Aid station", AprsSymbols.Name('/', 'A'));
    }

    [Fact]
    public void WeatherReport()
    {
        var p = AprsParser.Parse("FW0690>APRS,TCPXX*:@092345z4903.50N/07201.75W_220/004g005t077r000p000P000h50b09900wRSW");
        Assert.Equal(AprsPacketType.Weather, p.Type);
        Assert.NotNull(p.Weather);
        Assert.Equal(220, p.Weather!.WindDirection);
        Assert.Equal(4, p.Weather.WindSpeedMph);
        Assert.Equal(5, p.Weather.GustMph);
        Assert.Equal(25, p.Weather.TemperatureC!.Value, 1);
        Assert.Equal(50, p.Weather.HumidityPercent);
        Assert.Equal(990.0, p.Weather.PressureMbar!.Value, 1);
        Assert.Null(p.CourseDegrees);
        Assert.Equal("wRSW", p.Comment);
    }

    [Fact]
    public void StatusMessageAndThirdParty()
    {
        var s = AprsParser.Parse("N0CALL>APRS:>Net control at EOC");
        Assert.Equal(AprsPacketType.Status, s.Type);
        Assert.Equal("Net control at EOC", s.Status);

        var m = AprsParser.Parse("N0CALL>APRS::VE7ABC-9 :Hello there{001");
        Assert.Equal(AprsPacketType.Message, m.Type);
        Assert.Equal("VE7ABC-9", m.Addressee);
        Assert.Equal("Hello there", m.MessageText);
        Assert.Equal("001", m.MessageId);

        var t = AprsParser.Parse("N0CALL>APRS:}VE7ABC>APRS,TCPIP,N0CALL*:!4903.50N/07201.75W-");
        Assert.Equal("VE7ABC", t.Key);
        Assert.True(t.HasPosition);
    }

    [Fact]
    public void AmbiguityBlanksDigitsOnBothAxes()
    {
        var p = AprsParser.Parse("N0CALL>APRS:!4903.  N/07201.75W-");
        Assert.Equal(2, p.Ambiguity);
        Assert.Equal(49 + 3.05 / 60, p.Latitude!.Value, 6);
        Assert.Equal(-(72 + 1.05 / 60), p.Longitude!.Value, 6);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("N0CALL>APRS:!49XX.50N/07201.75W-")]
    [InlineData("N0CALL>APRS:!9903.50N/07201.75W-")]
    [InlineData("N0CALL>APRS:`")]
    public void BadPacketsReportAnErrorWithoutAPosition(string line)
    {
        var p = AprsParser.Parse(line);
        Assert.NotNull(p.Error);
        Assert.False(p.HasPosition);
    }

    [Fact]
    public void AlternateSymbolEmergency()
    {
        var p = AprsParser.Parse("N0CALL>APRS:!4903.50N\\07201.75W!Help");
        Assert.True(p.Emergency);
        Assert.Equal("EMERGENCY", AprsSymbols.Name('\\', '!'));
    }
}

public class Ax25KissTests
{
    [Fact]
    public void Ax25RoundTrip()
    {
        const string text = "VE7XYZ-9>APZFMP,WIDE1-1,WIDE2-1:!4903.50N/07201.75W>Patrol";
        var frame = Ax25.Encode(text);
        Assert.Equal((byte)('A' << 1), frame[0]);
        Assert.Equal(0xE0, frame[6]); // destination: reserved bits + command bit, SSID 0, not last
        Assert.Equal(text, Ax25.Decode(frame));
    }

    [Fact]
    public void Ax25MarksTheLastRepeatedDigipeater()
    {
        var frame = Ax25.Encode("VE7XYZ-9>APZFMP,VE7RPT*,WIDE2-1:>Hello");
        Assert.Equal("VE7XYZ-9>APZFMP,VE7RPT*,WIDE2-1:>Hello", Ax25.Decode(frame));
    }

    [Theory]
    [InlineData("TOOLONGCALL>APRS:>x")]
    [InlineData("N0CALL-16>APRS:>x")]
    [InlineData("N0CALL>APRS,WIDE1-1,WIDE2-1,A,B,C,D,E,F,G:>x")]
    public void Ax25RejectsAddressesItCannotCarry(string text) => Assert.Throws<FormatException>(() => Ax25.Encode(text));

    [Fact]
    public void KissEscapesAndReassemblesAcrossReads()
    {
        var data = new byte[] { 1, KissDecoder.Fend, 2, KissDecoder.Fesc, 3 };
        var framed = KissDecoder.Encode(data);
        Assert.DoesNotContain(KissDecoder.Fend, framed[1..^1]);
        var decoder = new KissDecoder();
        var first = decoder.Push(framed.AsSpan(0, 4));
        Assert.Empty(first);
        var rest = decoder.Push(framed.AsSpan(4));
        var frame = Assert.Single(rest);
        Assert.Equal(0, frame.Port);
        Assert.Equal(data, frame.Data);
    }

    [Fact]
    public void KissIgnoresNonDataCommands()
    {
        var decoder = new KissDecoder();
        var frames = decoder.Push(new byte[] { KissDecoder.Fend, 0x01, 50, KissDecoder.Fend, KissDecoder.Fend, 0x10, 7, 8, KissDecoder.Fend });
        var f = Assert.Single(frames);
        Assert.Equal(1, f.Port);
        Assert.Equal(new byte[] { 7, 8 }, f.Data);
    }
}

public class AprsBeaconTests
{
    [Fact]
    public void FormatsCoordinates()
    {
        Assert.Equal("4903.50N", AprsBeacon.Latitude(49 + 3.5 / 60));
        Assert.Equal("07201.75W", AprsBeacon.Longitude(-(72 + 1.75 / 60)));
        Assert.Equal("5000.00N", AprsBeacon.Latitude(49.999999));
        Assert.Equal("3325.64S", AprsBeacon.Latitude(-(33 + 25.64 / 60)));
    }

    [Fact]
    public void PositionRoundTripsThroughTheParser()
    {
        var info = AprsBeacon.Position(53.5461, -113.4938, '/', 'a', 251, 66.7, 668, "Medic 12 | test");
        var p = AprsParser.Parse(AprsBeacon.Packet("ve6abc-9", "WIDE1-1, WIDE2-1", info));
        Assert.Null(p.Error);
        Assert.Equal("VE6ABC-9", p.Source);
        Assert.Equal(AprsBeacon.ToCall, p.Destination);
        Assert.Equal(new[] { "WIDE1-1", "WIDE2-1" }, p.Path);
        Assert.Equal(53.5461, p.Latitude!.Value, 3);
        Assert.Equal(-113.4938, p.Longitude!.Value, 3);
        Assert.Equal(251, p.CourseDegrees);
        Assert.Equal(36, p.SpeedKnots);
        Assert.Equal(668, p.AltitudeMeters!.Value, 0);
        Assert.Equal("Medic 12  test", p.Comment);
        Assert.Equal("Ambulance", AprsSymbols.Name(p.SymbolTable, p.SymbolCode));
    }

    [Fact]
    public void StationaryBeaconHasNoCourseSpeed()
    {
        var info = AprsBeacon.Position(49.2827, -123.1207, '/', '-', 90, 0, null, "");
        Assert.Equal("!4916.96N/12307.24W-", info);
    }

    [Fact]
    public void ObjectRoundTrips()
    {
        var info = AprsBeacon.Object("Staging A", new DateTime(2026, 10, 3, 14, 5, 0, DateTimeKind.Utc), 50.6745, -120.3273, '/', 'c', "Incident staging");
        var p = AprsParser.Parse("VE7EOC>APZFMP:" + info);
        Assert.Equal(AprsPacketType.Object, p.Type);
        Assert.Equal("Staging A", p.ObjectName);
        Assert.Equal("Incident staging", p.Comment);
        Assert.StartsWith(";Staging A*031405z", info);
    }

    [Theory]
    [InlineData("VE7ABC", true)]
    [InlineData("ve6xyz-9", true)]
    [InlineData("VA7AB-15", true)]
    [InlineData("VE7ABC-16", false)]
    [InlineData("NOCALL", false)]
    [InlineData("123456", false)]
    [InlineData("", false)]
    public void ValidatesCallsigns(string call, bool ok) => Assert.Equal(ok, AprsBeacon.IsValidCallsign(call));
}

public class AprsTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MergesDuplicatesHeardByRadioAndInternet()
    {
        var t = new AprsStationTracker();
        const string line = "VE7XYZ-9>APRS,WIDE1-1:!4903.50N/07201.75W>Patrol";
        t.Process(AprsParser.Parse(line), AprsVia.Radio, T0);
        var s = t.Process(AprsParser.Parse("VE7XYZ-9>APRS,WIDE1-1,qAR,VE7IG:!4903.50N/07201.75W>Patrol"), AprsVia.Internet, T0.AddSeconds(2));
        Assert.NotNull(s);
        Assert.Equal(AprsVia.Radio | AprsVia.Internet, s!.Via);
        Assert.Equal(1, s.Packets);
        Assert.Single(s.Track);
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void BuildsATrackAndKeepsTheLatestState()
    {
        var t = new AprsStationTracker();
        t.Process(AprsParser.Parse("VE7XYZ-9>APRS:!4903.50N/07201.75W>000/000"), AprsVia.Internet, T0);
        t.Process(AprsParser.Parse("VE7XYZ-9>APRS:!4903.60N/07201.75W>360/020"), AprsVia.Internet, T0.AddMinutes(1));
        t.Process(AprsParser.Parse("VE7XYZ-9>APRS:!4903.60N/07201.75W>360/020"), AprsVia.Internet, T0.AddMinutes(2)); // same spot, new packet
        t.Process(AprsParser.Parse("VE7XYZ-9>APRS:>Returning to station"), AprsVia.Internet, T0.AddMinutes(3));
        var s = t.Find("VE7XYZ-9")!;
        Assert.Equal(2, s.Track.Count);
        Assert.Equal(0, s.CourseDegrees);
        Assert.Equal(20, s.SpeedKnots);
        Assert.Equal("Returning to station", s.Status);
        Assert.Equal(T0.AddMinutes(3), s.LastHeardUtc);
    }

    [Fact]
    public void KilledObjectsAreRemovedAndOldStationsPurged()
    {
        var t = new AprsStationTracker();
        t.Process(AprsParser.Parse("VE7EOC>APRS:;LEADER   *092345z4903.50N/07201.75W>"), AprsVia.Internet, T0);
        t.Process(AprsParser.Parse("VE7ABC>APRS:!4903.50N/07201.75W-"), AprsVia.Internet, T0);
        Assert.Equal(2, t.Count);
        t.Process(AprsParser.Parse("VE7EOC>APRS:;LEADER   _092345z4903.50N/07201.75W>"), AprsVia.Internet, T0.AddMinutes(1));
        Assert.Null(t.Find("LEADER"));
        Assert.Equal(1, t.Purge(T0.AddHours(3), TimeSpan.FromHours(2), TimeSpan.FromHours(1)));
        Assert.Equal(0, t.Count);
        Assert.Contains(t.LogSnapshot(), e => e.Summary.Contains("LEADER"));
    }

    [Fact]
    public void SnapshotsAreIndependentCopies()
    {
        var t = new AprsStationTracker();
        t.Process(AprsParser.Parse("VE7XYZ-9>APRS:!4903.50N/07201.75W>"), AprsVia.Radio, T0);
        var snap = t.Snapshot();
        t.Process(AprsParser.Parse("VE7XYZ-9>APRS:!4904.50N/07201.75W>"), AprsVia.Radio, T0.AddMinutes(1));
        Assert.Single(snap[0].Track);
        Assert.Equal(2, t.Find("VE7XYZ-9")!.Track.Count);
    }
}

public class AprsLinkTests
{
    [Fact]
    public async Task AprsIsClientLogsInReceivesPacketsAndUpdatesTheFilter()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var loginLine = new TaskCompletionSource<string>();
        var filterLine = new TaskCompletionSource<string>();
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            using var s = c.GetStream();
            using var r = new StreamReader(s);
            var w = new StreamWriter(s) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("# aprsc 2.1.14");
            loginLine.SetResult(await r.ReadLineAsync() ?? "");
            await w.WriteLineAsync("# logresp N0CALL unverified, server TEST1");
            await w.WriteLineAsync("VE7XYZ-9>APRS,TCPIP*,qAC,TEST1:!4903.50N/07201.75W>Patrol");
            filterLine.SetResult(await r.ReadLineAsync() ?? "");
            await Task.Delay(500);
        });

        using var client = new AprsIsClient("127.0.0.1", port, "N0CALL", "", AprsIsClient.RangeFilter(49.05, -72.03, 100), "FieldMap", "0.1");
        var got = new TaskCompletionSource<string>();
        client.PacketReceived += (_, line) => got.TrySetResult(line);
        client.Start();
        var login = await loginLine.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("user N0CALL pass -1 vers FieldMap 0.1 filter r/49.05/-72.03/100", login);
        var packet = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("VE7XYZ-9>", packet);
        Assert.False(client.Verified);
        Assert.False(client.CanTransmit);
        Assert.Contains("receive only", client.StatusText);
        await client.SetFilterAsync("r/50/-120/50");
        Assert.Equal("#filter r/50/-120/50", await filterLine.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("N0CALL>APZFMP:>x"));
        client.Stop();
        listener.Stop();
    }

    [Fact]
    public async Task KissTcpClientReceivesAndTransmitsFrames()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fromClient = new TaskCompletionSource<string?>();
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            using var s = c.GetStream();
            var frame = KissDecoder.Encode(Ax25.Encode("VE7XYZ-9>APRS,WIDE1-1:!4903.50N/07201.75W>Patrol"));
            // Noise and a non-APRS frame first.
            await s.WriteAsync(new byte[] { 0x55, KissDecoder.Fend, 0x00, 1, 2, 3, KissDecoder.Fend });
            await s.WriteAsync(frame);
            var decoder = new KissDecoder();
            var buf = new byte[1024];
            while (true)
            {
                var n = await s.ReadAsync(buf);
                if (n == 0)
                    break;
                foreach (var (_, data) in decoder.Push(buf.AsSpan(0, n)))
                {
                    fromClient.TrySetResult(Ax25.Decode(data));
                    return;
                }
            }
        });

        using var tnc = new KissTncClient("Test TNC", KissTncClient.Tcp("127.0.0.1", port));
        var got = new TaskCompletionSource<string>();
        tnc.PacketReceived += (_, line) => got.TrySetResult(line);
        tnc.Start();
        Assert.Equal("VE7XYZ-9>APRS,WIDE1-1:!4903.50N/07201.75W>Patrol", await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, tnc.IgnoredFrames);
        Assert.True(tnc.CanTransmit);
        var beacon = AprsBeacon.Packet("VE7ABC-9", "WIDE1-1,WIDE2-1", AprsBeacon.Position(49.2827, -123.1207, '/', '>', null, null, null, "Test"));
        await tnc.SendAsync(beacon);
        Assert.Equal(beacon, await fromClient.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        tnc.Stop();
        listener.Stop();
    }

    [Fact]
    public async Task LinkReportsFailureAndRetries()
    {
        var attempts = 0;
        using var tnc = new KissTncClient("Missing TNC", _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new IOException("The port 'COM9' does not exist.");
        }) { RetryDelays = new[] { TimeSpan.FromMilliseconds(50) } };
        tnc.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref attempts) < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(attempts >= 3);
        Assert.Contains("COM9", tnc.StatusText);
        tnc.Stop();
        Assert.Equal(AprsLinkState.Off, tnc.State);
    }
}
