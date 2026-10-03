using System.Globalization;
using System.Text.RegularExpressions;

namespace FieldMap.Core.Aprs;

/// <summary>
/// Decodes APRS packets in TNC2 text form ("SOURCE>DEST,PATH:information"), as sent by APRS-IS servers and produced
/// from AX.25 frames received through a TNC. Follows the APRS Protocol Reference 1.0.1 (positions with and without
/// timestamp, compressed positions, Mic-E, objects, items, status, messages, weather) and the 1.1/1.2 addenda for
/// Mic-E altitude and the overlay-capable symbol tables.
/// </summary>
public static partial class AprsParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"/A=(-?\d{5,6})")]
    private static partial Regex AltitudeRx();

    public static AprsPacket Parse(string line)
    {
        line = line.TrimEnd('\r', '\n');
        var gt = line.IndexOf('>');
        var colon = line.IndexOf(':');
        if (gt <= 0 || colon < gt)
            return new AprsPacket { Raw = line, Type = AprsPacketType.Unknown, Error = "Not an APRS packet (no SOURCE>DEST:)" };
        var header = line[(gt + 1)..colon].Split(',');
        var packet = new AprsPacket
        {
            Raw = line,
            Source = line[..gt].Trim(),
            Destination = header[0].Trim(),
            Path = header.Skip(1).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray(),
            Information = line[(colon + 1)..]
        };
        try
        {
            Decode(packet);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            packet.Error = "Could not decode: " + ex.Message;
            packet.Latitude = packet.Longitude = null;
        }
        if (packet.HasPosition && !ValidPosition(packet.Latitude!.Value, packet.Longitude!.Value))
        {
            packet.Error ??= "Position out of range";
            packet.Latitude = packet.Longitude = null;
        }
        return packet;
    }

    private static bool ValidPosition(double lat, double lon) =>
        lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && !(Math.Abs(lat) < 1e-6 && Math.Abs(lon) < 1e-6);

    private static void Decode(AprsPacket p)
    {
        var info = p.Information;
        if (info.Length == 0)
        {
            p.Type = AprsPacketType.Other;
            return;
        }
        switch (info[0])
        {
            case '!' or '=':
                p.Type = AprsPacketType.Position;
                ParsePosition(p, info, 1);
                break;
            case '/' or '@':
                p.Type = AprsPacketType.Position;
                RequireLength(info, 8, "timestamp");
                ParsePosition(p, info, 8);
                break;
            case ';':
                ParseObject(p, info);
                break;
            case ')':
                ParseItem(p, info);
                break;
            case '`' or '\'' or (char)0x1c or (char)0x1d:
                ParseMicE(p, info);
                break;
            case '>':
                p.Type = AprsPacketType.Status;
                p.Status = info[1..].Trim();
                break;
            case ':':
                ParseMessage(p, info);
                break;
            case '_':
                p.Type = AprsPacketType.Weather;
                p.SymbolTable = '/';
                p.SymbolCode = '_';
                if (info.Length > 9)
                    p.Comment = ParseWeather(p, info[9..], null, null);
                break;
            case 'T':
                p.Type = AprsPacketType.Telemetry;
                break;
            case '}':
                ThirdParty(p, info);
                break;
            default:
                // Some trackers put text before the position; '!' may appear within the first 40 characters.
                var bang = info.IndexOf('!', 0, Math.Min(40, info.Length));
                if (bang > 0 && bang + 1 < info.Length)
                {
                    p.Type = AprsPacketType.Position;
                    ParsePosition(p, info, bang + 1);
                }
                else
                {
                    p.Type = AprsPacketType.Other;
                }
                break;
        }
        if (p.SymbolTable == '\\' && p.SymbolCode == '!')
            p.Emergency = true;
    }

    private static void ThirdParty(AprsPacket p, string info)
    {
        // "}CALL>DEST,PATH:info" – a packet carried for another network. Decode the inner packet into this one.
        var inner = Parse(info[1..]);
        p.Type = inner.Type;
        p.ObjectName = inner.ObjectName ?? (inner.Source.Length > 0 ? inner.Source : null);
        p.Alive = inner.Alive;
        p.Latitude = inner.Latitude;
        p.Longitude = inner.Longitude;
        p.Ambiguity = inner.Ambiguity;
        p.SymbolTable = inner.SymbolTable;
        p.SymbolCode = inner.SymbolCode;
        p.CourseDegrees = inner.CourseDegrees;
        p.SpeedKnots = inner.SpeedKnots;
        p.AltitudeMeters = inner.AltitudeMeters;
        p.Comment = inner.Comment;
        p.Status = inner.Status;
        p.MicEMessage = inner.MicEMessage;
        p.Emergency = inner.Emergency;
        p.Weather = inner.Weather;
        p.Addressee = inner.Addressee;
        p.MessageText = inner.MessageText;
        p.MessageId = inner.MessageId;
        p.ReplyAck = inner.ReplyAck;
        p.Error = inner.Error;
    }

    private static void RequireLength(string s, int n, string what)
    {
        if (s.Length < n)
            throw new FormatException($"too short for {what}");
    }

    // ------------------------------------------------------------------ positions

    private static void ParsePosition(AprsPacket p, string info, int i)
    {
        if (i >= info.Length)
            throw new FormatException("no position");
        var c = info[i];
        string rest;
        if (char.IsDigit(c) || c == ' ')
        {
            RequireLength(info, i + 19, "position");
            var (lat, amb) = Latitude(info.Substring(i, 8));
            p.SymbolTable = info[i + 8];
            var lon = Longitude(info.Substring(i + 9, 9), amb);
            p.SymbolCode = info[i + 18];
            p.Latitude = lat;
            p.Longitude = lon;
            p.Ambiguity = amb;
            rest = info[(i + 19)..];
            rest = Extension(p, rest);
        }
        else
        {
            RequireLength(info, i + 13, "compressed position");
            rest = Compressed(p, info, i);
        }
        p.Comment = Altitude(p, p.SymbolCode == '_' ? ParseWeather(p, rest, p.CourseDegrees, p.SpeedKnots) : rest).Trim();
        if (p.SymbolCode == '_' && p.Weather != null)
        {
            // For weather stations, course/speed are the wind.
            p.CourseDegrees = null;
            p.SpeedKnots = null;
        }
    }

    /// <summary>"DDMM.mmN" with optional blanked digits for ambiguity.</summary>
    internal static (double Lat, int Ambiguity) Latitude(string s)
    {
        if (s.Length != 8 || s[4] != '.' || (s[7] != 'N' && s[7] != 'S' && s[7] != 'n' && s[7] != 's'))
            throw new FormatException("bad latitude");
        var amb = s[..7].Count(ch => ch == ' ');
        var digits = s[..7].Replace(' ', '0');
        var deg = int.Parse(digits[..2], Inv);
        var min = double.Parse(digits[2..7], Inv);
        if (deg > 90 || min >= 60)
            throw new FormatException("bad latitude");
        // Centre of the ambiguity box rather than its corner.
        min += amb switch { 1 => 0.005, 2 => 0.05, 3 => 0.5, 4 => 5, _ => 0 };
        var lat = deg + min / 60;
        return (char.ToUpperInvariant(s[7]) == 'S' ? -lat : lat, amb);
    }

    /// <summary>"DDDMM.mmW"; the latitude's ambiguity applies to the longitude too.</summary>
    internal static double Longitude(string s, int ambiguity)
    {
        if (s.Length != 9 || s[5] != '.' || "EWew".IndexOf(s[8]) < 0)
            throw new FormatException("bad longitude");
        var chars = s[..8].ToCharArray();
        // Blank the same number of trailing digits as the latitude.
        var digitPositions = new[] { 7, 6, 4, 3 };
        for (var k = 0; k < Math.Min(ambiguity, 4); k++)
            chars[digitPositions[k]] = ' ';
        var digits = new string(chars).Replace(' ', '0');
        var deg = int.Parse(digits[..3], Inv);
        var min = double.Parse(digits[3..8], Inv);
        if (deg > 180 || min >= 60)
            throw new FormatException("bad longitude");
        min += ambiguity switch { 1 => 0.005, 2 => 0.05, 3 => 0.5, 4 => 5, _ => 0 };
        var lon = deg + min / 60;
        return char.ToUpperInvariant(s[8]) == 'W' ? -lon : lon;
    }

    /// <summary>Removes and decodes a 7-character data extension (course/speed, PHG, RNG, DFS).</summary>
    private static string Extension(AprsPacket p, string rest)
    {
        if (rest.Length < 7)
            return rest;
        if (rest[3] == '/' && Digits3(rest, 0) is { } course && Digits3(rest, 4) is { } speed)
        {
            p.CourseDegrees = course is > 0 and <= 360 ? course % 360 : null;
            p.SpeedKnots = speed;
            return rest[7..];
        }
        if ((rest.StartsWith("PHG", StringComparison.Ordinal) || rest.StartsWith("RNG", StringComparison.Ordinal) || rest.StartsWith("DFS", StringComparison.Ordinal))
            && rest[3..7].All(char.IsDigit))
            return rest[7..];
        return rest;
    }

    private static int? Digits3(string s, int i)
    {
        if (s.Length < i + 3)
            return null;
        var t = s.Substring(i, 3);
        if (t.All(char.IsDigit))
            return int.Parse(t, Inv);
        return t is "..." or "   " ? 0 : null;
    }

    private static string Altitude(AprsPacket p, string comment)
    {
        var m = AltitudeRx().Match(comment);
        if (!m.Success)
            return comment;
        p.AltitudeMeters = int.Parse(m.Groups[1].Value, Inv) * 0.3048;
        return comment.Remove(m.Index, m.Length);
    }

    internal static int Base91(string s)
    {
        var v = 0;
        foreach (var ch in s)
        {
            if (ch < 33 || ch > 124)
                throw new FormatException("bad base-91 character");
            v = v * 91 + (ch - 33);
        }
        return v;
    }

    private static string Compressed(AprsPacket p, string info, int i)
    {
        var table = info[i];
        if (table is >= 'a' and <= 'j')
            table = (char)('0' + (table - 'a'));
        if (!(table is '/' or '\\' || char.IsAsciiLetterUpper(table) || char.IsAsciiDigit(table)))
            throw new FormatException("bad symbol table");
        p.SymbolTable = table;
        p.Latitude = 90 - Base91(info.Substring(i + 1, 4)) / 380926.0;
        p.Longitude = -180 + Base91(info.Substring(i + 5, 4)) / 190463.0;
        p.SymbolCode = info[i + 9];
        var c = info[i + 10];
        var s = info[i + 11];
        var t = info[i + 12];
        if (c != ' ' && t >= 33)
        {
            if ((((t - 33) >> 3) & 3) == 2)
                p.AltitudeMeters = Math.Pow(1.002, (c - 33) * 91 + (s - 33)) * 0.3048;
            else if (c - 33 is >= 0 and <= 89)
            {
                var course = (c - 33) * 4;
                p.CourseDegrees = course == 0 ? null : course % 360;
                p.SpeedKnots = Math.Pow(1.08, s - 33) - 1;
            }
        }
        return info[(i + 13)..];
    }

    // ------------------------------------------------------------------ weather

    /// <summary>Parses weather fields at the start of <paramref name="w"/>; returns what's left (usually the software type).</summary>
    private static string ParseWeather(AprsPacket p, string w, double? windDir, double? windMph)
    {
        var wx = new AprsWeather();
        if (windDir is { } d)
            wx.WindDirection = (int)d;
        // A weather station's course/speed field is wind direction and speed in mph.
        wx.WindSpeedMph = windMph;
        var i = 0;
        var any = windDir != null || windMph != null;
        while (i < w.Length)
        {
            var width = w[i] switch { 'c' or 's' or 'g' or 't' or 'r' or 'p' or 'P' or 'L' or 'l' or '#' => 3, 'h' => 2, 'b' => 5, _ => 0 };
            if (width == 0 || i + 1 + width > w.Length)
                break;
            var raw = w.Substring(i + 1, width);
            if (!raw.All(ch => char.IsDigit(ch) || ch is '-' or '.' or ' '))
                break;
            double? v = double.TryParse(raw, NumberStyles.AllowLeadingSign, Inv, out var x) ? x : null;
            switch (w[i])
            {
                case 'c': wx.WindDirection = v is { } dd ? (int)dd : wx.WindDirection; break;
                case 's': wx.WindSpeedMph = v ?? wx.WindSpeedMph; break;
                case 'g': wx.GustMph = v; break;
                case 't': wx.TemperatureF = v; break;
                case 'r': wx.RainLastHourInches = v / 100; break;
                case 'p': wx.Rain24hInches = v / 100; break;
                case 'P': wx.RainSinceMidnightInches = v / 100; break;
                case 'h': wx.HumidityPercent = v is { } h ? (h == 0 ? 100 : (int)h) : null; break;
                case 'b': wx.PressureMbar = v / 10; break;
            }
            any = true;
            i += 1 + width;
        }
        if (any)
        {
            p.Weather = wx;
            if (p.Type == AprsPacketType.Position)
                p.Type = AprsPacketType.Weather;
        }
        return w[i..];
    }

    // ------------------------------------------------------------------ objects and items

    private static void ParseObject(AprsPacket p, string info)
    {
        // ;NAME_____*DDHHMMzPOSITION
        RequireLength(info, 18, "object");
        p.Type = AprsPacketType.Object;
        p.ObjectName = info.Substring(1, 9).TrimEnd();
        p.Alive = info[10] == '*';
        ParsePosition(p, info, 18);
    }

    private static void ParseItem(AprsPacket p, string info)
    {
        // )NAME!POSITION  (name 3–9 characters, '!' live, '_' killed)
        var end = -1;
        for (var k = 4; k <= Math.Min(10, info.Length - 1); k++)
            if (info[k] is '!' or '_')
            {
                end = k;
                break;
            }
        if (end < 0)
            throw new FormatException("bad item name");
        p.Type = AprsPacketType.Item;
        p.ObjectName = info[1..end];
        p.Alive = info[end] == '!';
        ParsePosition(p, info, end + 1);
    }

    // ------------------------------------------------------------------ messages

    private static void ParseMessage(AprsPacket p, string info)
    {
        p.Type = AprsPacketType.Message;
        if (info.Length < 11 || info[10] != ':')
        {
            p.Type = AprsPacketType.Other;
            return;
        }
        p.Addressee = info[1..10].Trim();
        var text = info[11..];
        var brace = text.LastIndexOf('{');
        if (brace >= 0 && text.Length - brace <= 12)
        {
            var id = text[(brace + 1)..].Trim();
            // Reply-ack (APRS 1.1 addendum): "{MM}AA" = message id MM, and an ack for our message AA.
            var close = id.IndexOf('}');
            if (close >= 0)
            {
                p.ReplyAck = id[(close + 1)..].Trim() is { Length: > 0 } ra ? ra : null;
                id = id[..close];
            }
            p.MessageId = id.Length > 0 ? id : null;
            text = text[..brace];
        }
        p.MessageText = text;
    }

    // ------------------------------------------------------------------ Mic-E

    private static readonly string[] MicEStandard = { "EMERGENCY", "Priority", "Special", "Committed", "Returning", "In Service", "En Route", "Off Duty" };

    private static void ParseMicE(AprsPacket p, string info)
    {
        var dest = p.Destination.Split('-')[0];
        if (dest.Length < 6)
            throw new FormatException("Mic-E destination too short");
        RequireLength(info, 9, "Mic-E");
        p.Type = AprsPacketType.MicE;

        var digits = new char[6];
        int bits = 0, custom = 0;
        for (var k = 0; k < 6; k++)
        {
            var ch = dest[k];
            switch (ch)
            {
                case >= '0' and <= '9': digits[k] = ch; break;
                case >= 'A' and <= 'J': digits[k] = (char)('0' + ch - 'A'); if (k < 3) { bits |= 4 >> k; custom++; } break;
                case >= 'P' and <= 'Y': digits[k] = (char)('0' + ch - 'P'); if (k < 3) bits |= 4 >> k; break;
                case 'K': digits[k] = ' '; if (k < 3) { bits |= 4 >> k; custom++; } break;
                case 'L': digits[k] = ' '; break;
                case 'Z': digits[k] = ' '; if (k < 3) bits |= 4 >> k; break;
                default: throw new FormatException("bad Mic-E destination");
            }
        }
        bool High(int k) => dest[k] is >= 'P' and <= 'Z';
        var latText = new string(digits, 0, 4) + "." + new string(digits, 4, 2) + (High(3) ? "N" : "S");
        var (lat, ambiguity) = Latitude(latText);
        p.Ambiguity = ambiguity;

        var d = info[1] - 28 + (High(4) ? 100 : 0);
        if (d is >= 180 and <= 189)
            d -= 80;
        else if (d is >= 190 and <= 199)
            d -= 190;
        var m = info[2] - 28;
        if (m >= 60)
            m -= 60;
        var h = info[3] - 28;
        if (d is < 0 or > 180 || m is < 0 or >= 60 || h is < 0 or > 99)
            throw new FormatException("bad Mic-E longitude");
        var lon = d + (m + h / 100.0) / 60;
        p.Latitude = lat;
        p.Longitude = High(5) ? -lon : lon;

        var sp = info[4] - 28;
        var dc = info[5] - 28;
        var se = info[6] - 28;
        var speed = sp * 10 + dc / 10;
        if (speed >= 800)
            speed -= 800;
        var course = dc % 10 * 100 + se;
        if (course >= 400)
            course -= 400;
        p.SpeedKnots = speed;
        p.CourseDegrees = course is > 0 and <= 360 ? course % 360 : null;
        p.SymbolCode = info[7];
        p.SymbolTable = info[8];

        if (bits == 0)
        {
            p.MicEMessage = "EMERGENCY";
            p.Emergency = true;
        }
        else
        {
            p.MicEMessage = custom > 0 ? $"Custom-{7 - bits}" : MicEStandard[bits];
        }

        var rest = info[9..];
        if (rest.Length > 0 && rest[0] is '>' or ']' or '`' or '\'')
        {
            var kind = rest[0];
            rest = rest[1..];
            if (kind == ']' && rest.EndsWith('='))
                rest = rest[..^1];
            else if (kind == '>' && rest.EndsWith('^'))
                rest = rest[..^1];
        }
        if (rest.Length >= 4 && rest[3] == '}' && rest[..3].All(ch => ch is >= '!' and <= '{'))
        {
            p.AltitudeMeters = Base91(rest[..3]) - 10000;
            rest = rest[4..];
        }
        p.Comment = Altitude(p, rest).Trim();
    }
}
