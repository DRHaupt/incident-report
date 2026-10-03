using System.Net.Sockets;
using System.Text;

namespace FieldMap.Core.Aprs;

public enum AprsLinkState
{
    Off,
    Connecting,
    Connected,
    Waiting,
    Failed
}

/// <summary>A source of APRS packets: an APRS-IS server or a KISS TNC.</summary>
public abstract class AprsLink : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _task;

    public abstract string Name { get; }
    public abstract AprsVia Via { get; }
    public AprsLinkState State { get; private set; } = AprsLinkState.Off;
    public string StatusText { get; private set; } = "Off";
    public DateTime? LastPacketUtc { get; private set; }
    public long PacketCount { get; private set; }

    /// <summary>True when packets can be sent (TNC open; APRS-IS logged in with a verified passcode).</summary>
    public abstract bool CanTransmit { get; }

    /// <summary>A packet in TNC2 text form. Raised on a background thread.</summary>
    public event EventHandler<string>? PacketReceived;

    /// <summary>State or status text changed. Raised on a background thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Delays between reconnect attempts.</summary>
    public TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2) };

    public bool Running => _task is { IsCompleted: false };

    public void Start()
    {
        if (Running)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Run(() => RunLoopAsync(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Cancelled.
        }
        _cts?.Dispose();
        _cts = null;
        _task = null;
        SetState(AprsLinkState.Off, "Off");
    }

    private async Task RunLoopAsync(CancellationToken cancel)
    {
        var attempt = 0;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                SetState(AprsLinkState.Connecting, "Connecting…");
                var connectedAt = DateTime.UtcNow;
                await RunOnceAsync(cancel).ConfigureAwait(false);
                if (DateTime.UtcNow - connectedAt > TimeSpan.FromMinutes(1))
                    attempt = 0;
                if (!cancel.IsCancellationRequested)
                    SetState(AprsLinkState.Waiting, "Connection closed – reconnecting");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or ArgumentException or OperationCanceledException)
            {
                SetState(AprsLinkState.Failed, ex.Message);
            }
            OnDisconnected();
            var delay = RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)];
            try
            {
                if (State != AprsLinkState.Failed)
                    SetState(AprsLinkState.Waiting, $"Reconnecting in {delay.TotalSeconds:0} s");
                else
                    StatusText = $"{StatusText} – retrying in {delay.TotalSeconds:0} s";
                StateChanged?.Invoke(this, EventArgs.Empty);
                await Task.Delay(delay, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        OnDisconnected();
    }

    /// <summary>Connects, then reads until the connection ends (returns) or fails (throws).</summary>
    protected abstract Task RunOnceAsync(CancellationToken cancel);

    protected virtual void OnDisconnected()
    {
    }

    /// <summary>Sends a packet in TNC2 form ("CALL>APZFMP,PATH:info").</summary>
    public abstract Task SendAsync(string tnc2, CancellationToken cancel = default);

    protected void SetState(AprsLinkState state, string text)
    {
        State = state;
        StatusText = text;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected void Received(string tnc2)
    {
        LastPacketUtc = DateTime.UtcNow;
        PacketCount++;
        PacketReceived?.Invoke(this, tnc2);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// APRS-IS client (the internet backbone fed by I-gates). Uses the filtered port, so only packets near the area of
/// interest arrive. Without a verified passcode the login is receive-only.
/// </summary>
public sealed class AprsIsClient : AprsLink
{
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private string _filter;

    public AprsIsClient(string host, int port, string login, string passcode, string filter, string software, string version)
    {
        Host = host;
        Port = port;
        Login = login;
        Passcode = string.IsNullOrWhiteSpace(passcode) ? "-1" : passcode.Trim();
        _filter = filter;
        Software = software;
        Version = version;
    }

    public string Host { get; }
    public int Port { get; }
    public string Login { get; }
    public string Passcode { get; }
    public string Software { get; }
    public string Version { get; }

    /// <summary>Server answered "verified" to the login (transmitting allowed).</summary>
    public bool Verified { get; private set; }

    public string? ServerName { get; private set; }

    /// <summary>No data at all (the server sends a comment every 20 s) for this long means the link is dead.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public override string Name => "APRS-IS";
    public override AprsVia Via => AprsVia.Internet;
    public override bool CanTransmit => Verified && _writer != null;

    public string Filter => _filter;

    public string LoginLine => $"user {Login} pass {Passcode} vers {Software} {Version}" + (_filter.Length > 0 ? $" filter {_filter}" : "");

    protected override async Task RunOnceAsync(CancellationToken cancel)
    {
        Verified = false;
        using var tcp = new TcpClient();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await tcp.ConnectAsync(Host, Port, connectTimeout.Token).ConfigureAwait(false);
        }
        tcp.NoDelay = true;
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        lock (_writeLock)
            _writer = writer;
        await writer.WriteLineAsync(LoginLine.AsMemory(), cancel).ConfigureAwait(false);
        SetState(AprsLinkState.Connected, $"Connected to {Host} (logging in)");
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                idle.CancelAfter(IdleTimeout);
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new TimeoutException($"No data from {Host} for {IdleTimeout.TotalSeconds:0} s");
                }
                if (line == null)
                    return;
                if (line.StartsWith('#'))
                {
                    HandleComment(line);
                    continue;
                }
                if (line.Length > 0)
                    Received(line);
            }
        }
        finally
        {
            lock (_writeLock)
                _writer = null;
        }
    }

    private void HandleComment(string line)
    {
        // "# logresp N0CALL verified, server T2EXAMPLE" / "unverified"
        var i = line.IndexOf("logresp", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return;
        var rest = line[(i + 7)..];
        Verified = rest.Contains(" verified", StringComparison.OrdinalIgnoreCase) && !rest.Contains("unverified", StringComparison.OrdinalIgnoreCase);
        var server = rest.IndexOf("server", StringComparison.OrdinalIgnoreCase);
        ServerName = server >= 0 ? rest[(server + 6)..].Trim() : null;
        SetState(AprsLinkState.Connected, $"Connected to {ServerName ?? Host}" + (Verified ? " (can transmit)" : " (receive only)"));
    }

    /// <summary>Changes the server-side filter on a live connection (e.g. the area followed the map).</summary>
    public async Task SetFilterAsync(string filter, CancellationToken cancel = default)
    {
        _filter = filter;
        StreamWriter? w;
        lock (_writeLock)
            w = _writer;
        if (w != null)
            await w.WriteLineAsync(("#filter " + filter).AsMemory(), cancel).ConfigureAwait(false);
    }

    public override async Task SendAsync(string tnc2, CancellationToken cancel = default)
    {
        StreamWriter? w;
        lock (_writeLock)
            w = _writer;
        if (w == null || !Verified)
            throw new InvalidOperationException("Not logged in to APRS-IS with a verified passcode.");
        await w.WriteLineAsync(tnc2.AsMemory(), cancel).ConfigureAwait(false);
    }

    /// <summary>Range filter: packets within <paramref name="km"/> of a point.</summary>
    public static string RangeFilter(double lat, double lon, double km, string extra = "") =>
        FormattableString.Invariant($"r/{lat:0.###}/{lon:0.###}/{Math.Clamp(km, 1, 2000):0}") + (string.IsNullOrWhiteSpace(extra) ? "" : " " + extra.Trim());
}

/// <summary>
/// A KISS TNC on any byte stream: a serial (COM) port – hardware TNC, radio with built-in TNC in KISS mode, or a
/// Bluetooth TNC paired as a COM port – or a TCP KISS port such as Direwolf's (8001).
/// </summary>
public sealed class KissTncClient : AprsLink
{
    private readonly Func<CancellationToken, Task<Stream>> _open;
    private readonly string _name;
    private readonly object _writeLock = new();
    private Stream? _stream;

    /// <param name="open">Opens the stream (the serial port or TCP connection).</param>
    /// <param name="initText">Text sent before KISS starts, for TNCs that need a command to enter KISS mode (e.g. "KISS ON\rRESTART\r").</param>
    public KissTncClient(string name, Func<CancellationToken, Task<Stream>> open, string initText = "", int kissPort = 0)
    {
        _name = name;
        _open = open;
        InitText = initText;
        KissPort = kissPort;
    }

    public string InitText { get; }
    public int KissPort { get; }

    public override string Name => _name;
    public override AprsVia Via => AprsVia.Radio;
    public override bool CanTransmit => _stream != null;

    /// <summary>Frames that weren't APRS UI frames (other packet traffic).</summary>
    public long IgnoredFrames { get; private set; }

    protected override async Task RunOnceAsync(CancellationToken cancel)
    {
        var stream = await _open(cancel).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        if (InitText.Length > 0)
        {
            var init = Encoding.ASCII.GetBytes(InitText.Replace("\\r", "\r").Replace("\r\n", "\r").Replace('\n', '\r'));
            await stream.WriteAsync(init, cancel).ConfigureAwait(false);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
            await Task.Delay(500, cancel).ConfigureAwait(false);
        }
        lock (_writeLock)
            _stream = stream;
        SetState(AprsLinkState.Connected, $"{_name} open");
        var decoder = new KissDecoder();
        var buffer = new byte[4096];
        try
        {
            using var reg = cancel.Register(() =>
            {
                // Serial streams don't honour cancellation tokens; closing the stream ends the pending read.
                try
                {
                    stream.Dispose();
                }
                catch (IOException)
                {
                }
            });
            while (!cancel.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
                if (n == 0)
                    return;
                foreach (var (port, data) in decoder.Push(buffer.AsSpan(0, n)))
                {
                    var text = Ax25.Decode(data);
                    if (text == null)
                    {
                        IgnoredFrames++;
                        continue;
                    }
                    Received(text);
                }
            }
        }
        catch (ObjectDisposedException) when (cancel.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_writeLock)
                _stream = null;
        }
    }

    public override async Task SendAsync(string tnc2, CancellationToken cancel = default)
    {
        Stream? s;
        lock (_writeLock)
            s = _stream;
        if (s == null)
            throw new InvalidOperationException($"{_name} is not open.");
        var frame = KissDecoder.Encode(Ax25.Encode(tnc2), KissPort);
        await s.WriteAsync(frame, cancel).ConfigureAwait(false);
        await s.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Opens a TCP KISS port (e.g. Direwolf on localhost:8001).</summary>
    public static Func<CancellationToken, Task<Stream>> Tcp(string host, int port) => async cancel =>
    {
        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return new OwningStream(tcp.GetStream(), tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    };

    /// <summary>A network stream that also closes its TCP client.</summary>
    private sealed class OwningStream : Stream
    {
        private readonly NetworkStream _inner;
        private readonly TcpClient _client;

        public OwningStream(NetworkStream inner, TcpClient client)
        {
            _inner = inner;
            _client = client;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
