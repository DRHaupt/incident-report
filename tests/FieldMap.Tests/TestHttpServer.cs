using System.Net;
using System.Net.Sockets;

namespace FieldMap.Tests;

/// <summary>A tiny local HTTP server that serves byte arrays with Range support and can drop a connection mid-file.</summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, byte[]> _files = new();
    private readonly CancellationTokenSource _stop = new();

    public TestHttpServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(Loop);
    }

    public string BaseUrl { get; }

    /// <summary>When set, the next full or partial response stops after this many bytes (simulating a lost connection).</summary>
    public long? CutAfter { get; set; }

    public string ETag { get; set; } = "\"v1\"";
    public int RangeRequests { get; private set; }

    public void Serve(string name, byte[] content) => _files[name] = content;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }
            try
            {
                Handle(ctx);
            }
            catch (Exception)
            {
                try { ctx.Response.Abort(); } catch (Exception) { }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var name = ctx.Request.Url!.AbsolutePath.TrimStart('/');
        var res = ctx.Response;
        if (!_files.TryGetValue(name, out var data))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        res.Headers["ETag"] = ETag;
        res.Headers["Last-Modified"] = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc).ToString("R");
        res.Headers["Accept-Ranges"] = "bytes";
        long start = 0;
        long end = data.Length - 1;
        var range = ctx.Request.Headers["Range"];
        var ifRange = ctx.Request.Headers["If-Range"];
        if (range != null && range.StartsWith("bytes=") && (ifRange == null || ifRange == ETag))
        {
            RangeRequests++;
            var parts = range[6..].Split('-');
            start = long.Parse(parts[0]);
            if (parts.Length > 1 && parts[1].Length > 0)
                end = Math.Min(data.Length - 1, long.Parse(parts[1]));
            if (start >= data.Length)
            {
                res.StatusCode = 416;
                res.Close();
                return;
            }
            res.StatusCode = 206;
            res.Headers["Content-Range"] = $"bytes {start}-{end}/{data.Length}";
        }
        res.ContentLength64 = end - start + 1;
        if (ctx.Request.HttpMethod == "HEAD")
        {
            res.Close();
            return;
        }
        var count = end - start + 1;
        if (CutAfter is { } cut && cut < count)
        {
            CutAfter = null;
            res.OutputStream.Write(data, (int)start, (int)cut);
            res.OutputStream.Flush();
            res.Abort(); // connection lost mid-download
            return;
        }
        res.OutputStream.Write(data, (int)start, (int)count);
        res.Close();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}
