using System.Text.RegularExpressions;

namespace FieldMap.Core.Aprs;

public enum AprsMessageState
{
    Received,
    Sending,
    Acked,
    Rejected,
    Failed,
    Bulletin
}

/// <summary>An APRS text message to or from this station, or a bulletin heard.</summary>
public sealed class AprsMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimeUtc { get; set; }
    public bool Outgoing { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>The APRS message number ("{12"), used for acknowledgement.</summary>
    public string? MessageId { get; set; }

    public AprsMessageState State { get; set; }
    public int Tries { get; set; }
    public DateTime? NextTryUtc { get; set; }
    public DateTime? AckedUtc { get; set; }
    public AprsVia Via { get; set; }
    public bool Read { get; set; }
    public string? Problem { get; set; }

    /// <summary>The other station in the conversation.</summary>
    public string Peer => Outgoing ? To : From;

    public string StateText => State switch
    {
        AprsMessageState.Sending => Tries <= 1 ? "Sending…" : $"Sending (try {Tries})…",
        AprsMessageState.Acked => "✓ Received",
        AprsMessageState.Rejected => "✗ Rejected by the station",
        AprsMessageState.Failed => "✗ " + (Problem ?? "Not acknowledged"),
        _ => ""
    };
}

/// <summary>
/// APRS messaging (APRS 1.0.1 chapter 14, reply-acks from the 1.1 addendum): sends messages with a number and
/// retries until acknowledged, acknowledges messages addressed to this station (every copy, as the spec requires,
/// but shows each only once), and keeps bulletins. Thread-safe; packets arrive on network threads.
/// </summary>
public sealed partial class AprsMessenger
{
    public const int MaxText = 67;

    private readonly object _lock = new();
    private readonly List<AprsMessage> _messages;
    private readonly Func<string, AprsVia, AprsVia> _transmit;
    private int _nextId;
    private int _version;

    [GeneratedRegex(@"^[A-Z0-9]{1,6}(-[A-Z0-9]{1,2})?$")]
    private static partial Regex AddresseeRx();

    /// <param name="transmit">Sends an information field ("…:text{12") on the given links; returns the links it went out on.</param>
    /// <param name="messages">Existing messages (e.g. loaded from disk); the list is used and updated in place.</param>
    public AprsMessenger(Func<string, AprsVia, AprsVia> transmit, List<AprsMessage>? messages = null)
    {
        _transmit = transmit;
        _messages = messages ?? new List<AprsMessage>();
        _nextId = Random.Shared.Next(1, 900);
    }

    /// <summary>This station's call sign with SSID. Messages to other addressees are ignored.</summary>
    public string MyCall { get; set; } = "";

    /// <summary>Send acknowledgements for messages addressed to us (a transmission: needs a licensed call sign).</summary>
    public bool Acknowledge { get; set; } = true;

    /// <summary>Delays after each try before the next; the message fails after the last one.</summary>
    public TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480) };

    public int MaxKeep { get; set; } = 2000;

    /// <summary>Raised (on the calling thread) when a new message for us arrives.</summary>
    public event EventHandler<AprsMessage>? Received;

    public int Version
    {
        get { lock (_lock) return _version; }
    }

    public int Unread
    {
        get { lock (_lock) return _messages.Count(m => !m.Outgoing && m.State == AprsMessageState.Received && !m.Read); }
    }

    public List<AprsMessage> Snapshot()
    {
        lock (_lock)
            return _messages.Select(Copy).ToList();
    }

    /// <summary>Runs <paramref name="save"/> while no message is changing (for writing the list to disk).</summary>
    public void Persist(Action save)
    {
        lock (_lock)
            save();
    }

    public static bool IsValidAddressee(string call) => AddresseeRx().IsMatch(call.Trim().ToUpperInvariant());

    public static string Pad(string addressee) => addressee.Trim().ToUpperInvariant().PadRight(9)[..9];

    /// <summary>Handles a received packet. Returns the new message if one was added for the user to see.</summary>
    public AprsMessage? Handle(AprsPacket p, AprsVia via, DateTime nowUtc)
    {
        if (p.Type != AprsPacketType.Message || p.Addressee == null || p.MessageText == null)
            return null;
        var text = p.MessageText;
        AprsMessage? added = null;
        lock (_lock)
        {
            if (IsBulletin(p.Addressee))
            {
                if (!_messages.Any(m => m.State == AprsMessageState.Bulletin && m.From.Equals(p.Source, StringComparison.OrdinalIgnoreCase) && m.To == p.Addressee && m.Text == text))
                {
                    _messages.Add(new AprsMessage { TimeUtc = nowUtc, From = p.Source, To = p.Addressee, Text = text.Trim(), State = AprsMessageState.Bulletin, Via = via, Read = true });
                    Trim();
                    _version++;
                }
                return null;
            }
            if (MyCall.Length == 0 || !p.Addressee.Equals(MyCall, StringComparison.OrdinalIgnoreCase))
                return null;

            // Acknowledgement or rejection of one of ours.
            if (text.Length > 3 && (text.StartsWith("ack", StringComparison.Ordinal) || text.StartsWith("rej", StringComparison.Ordinal)) && p.MessageId == null)
            {
                var id = text[3..].Trim().TrimEnd('}');
                Resolve(p.Source, id, text.StartsWith("ack", StringComparison.Ordinal), nowUtc);
                return null;
            }
            if (p.ReplyAck != null)
                Resolve(p.Source, p.ReplyAck, true, nowUtc);
            if (text.StartsWith("PARM.", StringComparison.Ordinal) || text.StartsWith("UNIT.", StringComparison.Ordinal) ||
                text.StartsWith("EQNS.", StringComparison.Ordinal) || text.StartsWith("BITS.", StringComparison.Ordinal))
                return null;

            var duplicate = _messages.Any(m => !m.Outgoing && m.From.Equals(p.Source, StringComparison.OrdinalIgnoreCase) &&
                (p.MessageId != null ? m.MessageId == p.MessageId && nowUtc - m.TimeUtc < TimeSpan.FromHours(2)
                                     : m.Text == text && nowUtc - m.TimeUtc < TimeSpan.FromMinutes(5)));
            if (!duplicate)
            {
                added = new AprsMessage { TimeUtc = nowUtc, From = p.Source, To = MyCall, Text = text, MessageId = p.MessageId, State = AprsMessageState.Received, Via = via };
                _messages.Add(added);
                Trim();
            }
            else
            {
                var existing = _messages.Last(m => !m.Outgoing && m.From.Equals(p.Source, StringComparison.OrdinalIgnoreCase));
                existing.Via |= via;
            }
            _version++;
        }
        // Every copy is acknowledged: the sender keeps retrying until an ack gets through.
        if (p.MessageId != null && Acknowledge)
            _transmit(":" + Pad(p.Source) + ":ack" + p.MessageId, via);
        if (added != null)
            Received?.Invoke(this, Copy(added));
        return added;
    }

    private static bool IsBulletin(string addressee) =>
        addressee.StartsWith("BLN", StringComparison.OrdinalIgnoreCase) || addressee.StartsWith("NWS", StringComparison.OrdinalIgnoreCase);

    private void Resolve(string from, string id, bool ack, DateTime nowUtc)
    {
        foreach (var m in _messages.Where(m => m.Outgoing && m.MessageId == id && m.To.Equals(from, StringComparison.OrdinalIgnoreCase)
                                              && m.State is AprsMessageState.Sending or AprsMessageState.Failed))
        {
            m.State = ack ? AprsMessageState.Acked : AprsMessageState.Rejected;
            m.AckedUtc = nowUtc;
            m.NextTryUtc = null;
            m.Problem = null;
        }
        _version++;
    }

    /// <summary>Sends a message (up to 67 characters) and retries until it's acknowledged.</summary>
    public AprsMessage Send(string to, string text, DateTime nowUtc)
    {
        to = to.Trim().ToUpperInvariant();
        if (!IsValidAddressee(to))
            throw new ArgumentException($"'{to}' is not a valid APRS address.");
        if (MyCall.Length == 0)
            throw new InvalidOperationException("Set your call sign before sending messages.");
        text = new string(text.Where(ch => ch is >= ' ' and <= '~' && ch is not '|' and not '~' and not '{').ToArray()).Trim();
        if (text.Length == 0)
            throw new ArgumentException("The message is empty.");
        if (text.Length > MaxText)
            text = text[..MaxText];
        AprsMessage m;
        lock (_lock)
        {
            _nextId = _nextId % 99999 + 1;
            m = new AprsMessage { TimeUtc = nowUtc, Outgoing = true, From = MyCall, To = to, Text = text, MessageId = _nextId.ToString(), State = AprsMessageState.Sending, Read = true };
            _messages.Add(m);
            Trim();
            _version++;
        }
        TrySend(m, nowUtc);
        return Copy(m);
    }

    /// <summary>Sends a failed message again from the first try.</summary>
    public void Retry(Guid id, DateTime nowUtc)
    {
        AprsMessage? m;
        lock (_lock)
        {
            m = _messages.FirstOrDefault(x => x.Id == id && x.Outgoing);
            if (m == null)
                return;
            m.State = AprsMessageState.Sending;
            m.Tries = 0;
            m.Problem = null;
            _version++;
        }
        TrySend(m, nowUtc);
    }

    private void TrySend(AprsMessage m, DateTime nowUtc)
    {
        var info = ":" + Pad(m.To) + ":" + m.Text + "{" + m.MessageId;
        var sent = _transmit(info, AprsVia.Radio | AprsVia.Internet);
        lock (_lock)
        {
            if (m.State != AprsMessageState.Sending)
                return;
            if (sent == AprsVia.None)
            {
                m.State = AprsMessageState.Failed;
                m.Problem = "No TNC open and no APRS-IS login that can send";
                m.NextTryUtc = null;
            }
            else
            {
                m.Via |= sent;
                m.Tries++;
                m.NextTryUtc = nowUtc + RetryDelays[Math.Min(m.Tries - 1, RetryDelays.Length - 1)];
            }
            _version++;
        }
    }

    /// <summary>Retries unacknowledged messages that are due; call regularly (e.g. once a second).</summary>
    public void Tick(DateTime nowUtc)
    {
        List<AprsMessage> due;
        lock (_lock)
        {
            due = _messages.Where(m => m.Outgoing && m.State == AprsMessageState.Sending && m.NextTryUtc <= nowUtc).ToList();
            foreach (var m in due.Where(m => m.Tries >= RetryDelays.Length).ToList())
            {
                m.State = AprsMessageState.Failed;
                m.Problem = $"Not acknowledged after {m.Tries} tries";
                m.NextTryUtc = null;
                due.Remove(m);
                _version++;
            }
        }
        foreach (var m in due)
            TrySend(m, nowUtc);
    }

    public void MarkRead(string peer)
    {
        lock (_lock)
        {
            foreach (var m in _messages.Where(m => !m.Outgoing && !m.Read && m.From.Equals(peer, StringComparison.OrdinalIgnoreCase)))
                m.Read = true;
            _version++;
        }
    }

    public void ClearConversation(string peer)
    {
        lock (_lock)
        {
            _messages.RemoveAll(m => m.State != AprsMessageState.Bulletin && m.Peer.Equals(peer, StringComparison.OrdinalIgnoreCase));
            _version++;
        }
    }

    private void Trim()
    {
        if (_messages.Count > MaxKeep)
            _messages.RemoveRange(0, _messages.Count - MaxKeep);
    }

    private static AprsMessage Copy(AprsMessage m) => new()
    {
        Id = m.Id, TimeUtc = m.TimeUtc, Outgoing = m.Outgoing, From = m.From, To = m.To, Text = m.Text, MessageId = m.MessageId, State = m.State,
        Tries = m.Tries, NextTryUtc = m.NextTryUtc, AckedUtc = m.AckedUtc, Via = m.Via, Read = m.Read, Problem = m.Problem
    };
}

/// <summary>SmartBeaconing settings (speed-dependent rate plus corner pegging).</summary>
public sealed class SmartBeaconSettings
{
    /// <summary>Below this speed the station counts as stopped and beacons at the slow rate.</summary>
    public double LowSpeedKmh { get; set; } = 8;

    /// <summary>At or above this speed it beacons at the fast rate.</summary>
    public double HighSpeedKmh { get; set; } = 90;

    public int SlowRateSeconds { get; set; } = 1200;
    public int FastRateSeconds { get; set; } = 180;

    /// <summary>Turn needed at high speed to beacon early, in degrees.</summary>
    public double MinTurnAngle { get; set; } = 28;

    /// <summary>Extra turn needed at low speed: threshold = MinTurnAngle + TurnSlope / speed (mph), as in the original algorithm.</summary>
    public double TurnSlope { get; set; } = 255;

    /// <summary>Minimum time between turn-triggered beacons.</summary>
    public int MinTurnSeconds { get; set; } = 15;
}

/// <summary>
/// SmartBeaconing (Tony Arnerich KD7TA and Steve Bragg KA9MVA, used by most APRS trackers): beacon more often the faster
/// you go, rarely when stopped, and straight away after a significant turn, so tracks follow the roads without
/// flooding the channel.
/// </summary>
public static class SmartBeacon
{
    /// <summary>The beacon interval at a speed.</summary>
    public static TimeSpan Rate(SmartBeaconSettings s, double speedKmh)
    {
        if (speedKmh < s.LowSpeedKmh)
            return TimeSpan.FromSeconds(s.SlowRateSeconds);
        if (speedKmh >= s.HighSpeedKmh)
            return TimeSpan.FromSeconds(s.FastRateSeconds);
        var seconds = s.FastRateSeconds * s.HighSpeedKmh / speedKmh;
        return TimeSpan.FromSeconds(Math.Min(s.SlowRateSeconds, seconds));
    }

    /// <summary>The heading change that triggers an early beacon at a speed.</summary>
    public static double TurnThreshold(SmartBeaconSettings s, double speedKmh)
    {
        var mph = Math.Max(speedKmh / 1.609344, 0.1);
        return s.MinTurnAngle + s.TurnSlope / mph;
    }

    public static double HeadingChange(double a, double b)
    {
        var d = Math.Abs(((a - b) % 360 + 360) % 360);
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Whether a beacon is due now. <paramref name="reason"/> says why ("first", "time", "turn").</summary>
    public static bool Due(SmartBeaconSettings s, DateTime nowUtc, DateTime? lastUtc, double? lastCourse, double speedKmh, double? course, out string reason)
    {
        reason = "";
        if (lastUtc is not { } last)
        {
            reason = "first";
            return true;
        }
        var elapsed = nowUtc - last;
        if (elapsed >= Rate(s, speedKmh))
        {
            reason = "time";
            return true;
        }
        if (speedKmh >= s.LowSpeedKmh && course is { } c && lastCourse is { } lc && elapsed >= TimeSpan.FromSeconds(s.MinTurnSeconds)
            && HeadingChange(c, lc) > TurnThreshold(s, speedKmh))
        {
            reason = "turn";
            return true;
        }
        return false;
    }
}
