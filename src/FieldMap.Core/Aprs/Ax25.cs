using System.Text;

namespace FieldMap.Core.Aprs;

/// <summary>
/// AX.25 UI frames as exchanged with a KISS TNC (no flags or FCS – the TNC adds and checks those), converted to and
/// from the TNC2 text form used everywhere else.
/// </summary>
public static class Ax25
{
    private const byte ControlUi = 0x03;
    private const byte PidNoLayer3 = 0xF0;

    /// <summary>Decodes a UI frame to "SOURCE>DEST,DIGI1*,DIGI2:info", or null if it isn't an APRS-style UI frame.</summary>
    public static string? Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 16)
            return null;
        var addresses = new List<(string Call, bool Repeated)>();
        var i = 0;
        while (true)
        {
            if (i + 7 > frame.Length || addresses.Count > 10)
                return null;
            var (call, hBit, last) = Address(frame.Slice(i, 7));
            if (call == null)
                return null;
            addresses.Add((call, hBit));
            i += 7;
            if (last)
                break;
        }
        if (addresses.Count < 2 || i + 2 > frame.Length || frame[i] != ControlUi || frame[i + 1] != PidNoLayer3)
            return null;
        var info = Encoding.Latin1.GetString(frame[(i + 2)..]).TrimEnd('\r', '\n');
        var sb = new StringBuilder();
        sb.Append(addresses[1].Call).Append('>').Append(addresses[0].Call);
        // TNC2 marks the last digipeater that has repeated the frame with '*'.
        var lastRepeated = -1;
        for (var k = 2; k < addresses.Count; k++)
            if (addresses[k].Repeated)
                lastRepeated = k;
        for (var k = 2; k < addresses.Count; k++)
        {
            sb.Append(',').Append(addresses[k].Call);
            if (k == lastRepeated)
                sb.Append('*');
        }
        sb.Append(':').Append(info);
        return sb.ToString();
    }

    private static (string? Call, bool HBit, bool Last) Address(ReadOnlySpan<byte> a)
    {
        var chars = new char[6];
        for (var k = 0; k < 6; k++)
        {
            var c = (char)(a[k] >> 1);
            if (c != ' ' && !char.IsAsciiLetterOrDigit(c))
                return (null, false, false);
            chars[k] = c;
        }
        var call = new string(chars).TrimEnd();
        if (call.Length == 0)
            return (null, false, false);
        var ssid = (a[6] >> 1) & 0x0F;
        return (ssid == 0 ? call : $"{call}-{ssid}", (a[6] & 0x80) != 0, (a[6] & 0x01) != 0);
    }

    /// <summary>Encodes "SOURCE>DEST,PATH:info" as a UI frame. Throws for addresses AX.25 can't carry (e.g. TCPIP, qAR).</summary>
    public static byte[] Encode(string tnc2)
    {
        var gt = tnc2.IndexOf('>');
        var colon = tnc2.IndexOf(':');
        if (gt <= 0 || colon < gt)
            throw new FormatException("Not a TNC2 packet");
        var header = tnc2[(gt + 1)..colon].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var calls = new List<string> { header[0], tnc2[..gt] };
        calls.AddRange(header.Skip(1));
        if (calls.Count > 10)
            throw new FormatException("Too many digipeaters (AX.25 allows 8)");
        var bytes = new List<byte>();
        for (var k = 0; k < calls.Count; k++)
        {
            var call = calls[k];
            var repeated = call.EndsWith('*');
            call = call.TrimEnd('*');
            bytes.AddRange(EncodeAddress(call, k == calls.Count - 1, k == 0, repeated && k >= 2));
        }
        bytes.Add(ControlUi);
        bytes.Add(PidNoLayer3);
        bytes.AddRange(Encoding.Latin1.GetBytes(tnc2[(colon + 1)..]));
        return bytes.ToArray();
    }

    private static byte[] EncodeAddress(string call, bool last, bool destination, bool repeated)
    {
        var parts = call.ToUpperInvariant().Split('-');
        var name = parts[0];
        var ssid = 0;
        if (name.Length is 0 or > 6 || !name.All(char.IsAsciiLetterOrDigit) ||
            (parts.Length > 1 && (!int.TryParse(parts[1], out ssid) || ssid is < 0 or > 15)) || parts.Length > 2)
            throw new FormatException($"'{call}' is not a valid AX.25 address");
        var a = new byte[7];
        for (var k = 0; k < 6; k++)
            a[k] = (byte)((k < name.Length ? name[k] : ' ') << 1);
        // Reserved bits set; command/response bit set on the destination (command frame).
        a[6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0) | (destination || repeated ? 0x80 : 0));
        return a;
    }
}

/// <summary>KISS framing (FEND/FESC escaping) for TNCs on a serial port or TCP socket.</summary>
public sealed class KissDecoder
{
    public const byte Fend = 0xC0, Fesc = 0xDB, Tfend = 0xDC, Tfesc = 0xDD;

    private readonly List<byte> _frame = new();
    private bool _escape;
    private bool _inFrame;

    /// <summary>Feeds received bytes; returns the data frames completed (port 0–15, command 0 only).</summary>
    public List<(int Port, byte[] Data)> Push(ReadOnlySpan<byte> bytes)
    {
        var done = new List<(int, byte[])>();
        foreach (var b in bytes)
        {
            if (b == Fend)
            {
                if (_inFrame && _frame.Count > 1 && (_frame[0] & 0x0F) == 0)
                    done.Add((_frame[0] >> 4, _frame.Skip(1).ToArray()));
                _frame.Clear();
                _escape = false;
                _inFrame = true;
                continue;
            }
            if (!_inFrame)
                continue;
            if (_escape)
            {
                _frame.Add(b == Tfend ? Fend : b == Tfesc ? Fesc : b);
                _escape = false;
            }
            else if (b == Fesc)
            {
                _escape = true;
            }
            else
            {
                _frame.Add(b);
            }
            if (_frame.Count > 4096)
            {
                // Garbage on the line: resynchronise on the next FEND.
                _frame.Clear();
                _inFrame = false;
            }
        }
        return done;
    }

    /// <summary>Wraps a frame for sending to the TNC (data command on <paramref name="port"/>).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> data, int port = 0)
    {
        var o = new List<byte>(data.Length + 8) { Fend, (byte)((port & 0x0F) << 4) };
        foreach (var b in data)
        {
            if (b == Fend)
            {
                o.Add(Fesc);
                o.Add(Tfend);
            }
            else if (b == Fesc)
            {
                o.Add(Fesc);
                o.Add(Tfesc);
            }
            else
            {
                o.Add(b);
            }
        }
        o.Add(Fend);
        return o.ToArray();
    }
}
