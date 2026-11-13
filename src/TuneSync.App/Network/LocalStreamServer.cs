using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TuneSync.App.Network;

/// <summary>A tiny HTTP server on this PC only (127.0.0.1) that hands a network song to the
/// player while it is still downloading into the cache. The player sees an ordinary HTTP file with
/// seeking support; a request for bytes that haven't arrived yet simply waits for them. Nothing
/// outside this computer can connect to it.</summary>
public sealed class LocalStreamServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, CacheItem> _published = new();
    private readonly CancellationTokenSource _stop = new();

    public LocalStreamServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>The address the player opens for this song.</summary>
    public Uri UrlFor(CacheItem item)
    {
        var token = Guid.NewGuid().ToString("N");
        _published[token] = item;
        return new Uri($"http://127.0.0.1:{Port}/{token}/song{Path.GetExtension(item.RemotePath).ToLowerInvariant()}");
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            using var network = client.GetStream();
            var request = ReadHeaders(network);
            if (request is null) return;

            var (method, target, headers) = request.Value;
            var token = target.Trim('/').Split('/')[0];
            if (!_published.TryGetValue(token, out var item))
            {
                Write(network, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            // Wait for the size (known as soon as the download has started).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (item.Length == 0 && !item.IsComplete && DateTime.UtcNow < deadline && !item.Completion.IsFaulted)
                Thread.Sleep(30);
            var length = item.Length;
            if (length == 0)
            {
                Write(network, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            long start = 0, end = length - 1;
            var partial = false;
            if (headers.TryGetValue("range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = range[6..].Split(',')[0].Split('-');
                if (long.TryParse(spec[0], out var s)) start = s;
                if (spec.Length > 1 && long.TryParse(spec[1], out var e)) end = Math.Min(e, length - 1);
                partial = true;
            }
            if (start >= length)
            {
                Write(network, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            var header = new StringBuilder()
                .Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
                .Append("Content-Type: ").Append(ContentType(item.RemotePath)).Append("\r\n")
                .Append("Accept-Ranges: bytes\r\n")
                // Never kept by the system's HTTP cache: an edited song must be read anew.
                .Append("Cache-Control: no-store\r\n")
                .Append("Content-Length: ").Append(end - start + 1).Append("\r\n");
            if (partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
            header.Append("Connection: close\r\n\r\n");
            Write(network, header.ToString());
            if (method == "HEAD") return;

            var buffer = new byte[128 * 1024];
            var position = start;
            using var file = new FileStream(item.ReadablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            while (position <= end && !_stop.IsCancellationRequested)
            {
                if (!item.WaitFor(position, _stop.Token)) break;
                file.Position = position;
                var available = Math.Min(item.Downloaded, end + 1) - position;
                var n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, available));
                if (n <= 0)
                {
                    Thread.Sleep(20);
                    continue;
                }
                network.Write(buffer, 0, n);
                position += n;
            }
        }
        catch
        {
            // the player closed the connection (seek, next song) - normal
        }
    }

    private static (string Method, string Target, Dictionary<string, string> Headers)? ReadHeaders(NetworkStream stream)
    {
        var bytes = new List<byte>(1024);
        var one = new byte[1];
        while (bytes.Count < 16384)
        {
            if (stream.Read(one, 0, 1) <= 0) return null;
            bytes.Add(one[0]);
            var c = bytes.Count;
            if (c >= 4 && bytes[c - 4] == '\r' && bytes[c - 3] == '\n' && bytes[c - 2] == '\r' && bytes[c - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        return (first[0].ToUpperInvariant(), first[1], headers);
    }

    private static void Write(NetworkStream stream, string text)
    {
        var data = Encoding.ASCII.GetBytes(text);
        stream.Write(data, 0, data.Length);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".wma" => "audio/x-ms-wma",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
