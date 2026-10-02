using FieldToolkit.Core;
using FieldToolkit.Core.Radio;
using Shared.Core.Radio;

namespace FieldToolkit.Tests;

public class RadioTests
{
    [Fact]
    public void Reference_file_loads()
    {
        var r = RadioReference.LoadOrEmpty(TestPaths.Content("radio", "reference.json"));
        Assert.True(r.Prowords.Count > 20);
        Assert.Contains(r.Prowords, p => p.Word == "WILCO");
        Assert.All(r.Prowords, p => Assert.False(string.IsNullOrWhiteSpace(p.Meaning)));
        Assert.Equal(7, r.Formats.Single(f => f.Name.StartsWith("METHANE")).Lines.Count);
        Assert.NotEmpty(r.Source);
    }

    [Fact]
    public void Missing_reference_gives_empty() => Assert.Empty(RadioReference.LoadOrEmpty("/nope/reference.json").Prowords);

    [Fact]
    public void Csv_round_trips_quotes_commas_and_newlines()
    {
        var rows = new List<RadioChannel>
        {
            new() { Channel = "1", Name = "Control", RxFrequency = "456.000", TxFrequency = "456.000", Tone = "67.0", Use = "Main, all units", Notes = "Say \"control\"" },
            new() { Channel = "2", Name = "Stage A", Notes = "line one\nline two" }
        };
        var csv = Csv.Write(rows);
        var back = Csv.Read<RadioChannel>(csv);
        Assert.Equal(2, back.Count);
        Assert.Equal("Main, all units", back[0].Use);
        Assert.Equal("Say \"control\"", back[0].Notes);
        Assert.Equal("line one\nline two", back[1].Notes);
    }

    [Fact]
    public void Csv_read_maps_columns_by_header_in_any_order()
    {
        var csv = "who,SIGN,ignored\r\nMedical lead,Doc 1,x\r\n,,\r\nRecovery,Tow 2,y";
        var signs = Csv.Read<CallSign>(csv);
        Assert.Equal(2, signs.Count);
        Assert.Equal("Doc 1", signs[0].Sign);
        Assert.Equal("Medical lead", signs[0].Who);
    }

    [Fact]
    public void Csv_without_known_columns_is_rejected() => Assert.Throws<FormatException>(() => Csv.Read<CallSign>("a,b\n1,2"));

    [Fact]
    public void Log_formatting()
    {
        var t = new DateTime(2026, 5, 1, 9, 30, 15, DateTimeKind.Local).ToUniversalTime();
        var e = new LogEntry { TimeUtc = t, From = "Control", To = "Unit 3", Text = "Proceed to stage end" };
        Assert.Equal("09:30:15  Control → Unit 3: Proceed to stage end", e.Format());
        Assert.Equal("09:30:15  Note only", new LogEntry { TimeUtc = t, Text = "Note only" }.Format());
        var log = LogEntry.FormatLog(new[] { e }, "RADIO LOG");
        Assert.StartsWith("RADIO LOG", log);
        Assert.Contains("1 entries", log);
    }
}
