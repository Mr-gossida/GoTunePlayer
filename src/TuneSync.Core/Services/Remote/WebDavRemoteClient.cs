using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services.Remote;

/// <summary>WebDAV over HTTP(S): PROPFIND to browse, ranged GET to read from any offset, PUT +
/// MOVE to write back. Works with Synology/QNAP/Nextcloud/ownCloud and most WebDAV servers.</summary>
public sealed class WebDavRemoteClient : IRemoteClient
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly HttpClient _http;
    private readonly Uri _base;

    public WebDavRemoteClient(RemoteSource source, string password) : this(source, password, null)
    {
    }

    /// <summary>With a custom HTTP handler (tests).</summary>
    public WebDavRemoteClient(RemoteSource source, string password, HttpMessageHandler? handlerOverride)
    {
        var address = source.Address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        var builder = new UriBuilder(address);
        if (source.Port > 0) builder.Port = source.Port;
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        _base = builder.Uri;

        var handler = new HttpClientHandler
        {
            Credentials = string.IsNullOrEmpty(source.UserName) ? null : new NetworkCredential(source.UserName, password),
            PreAuthenticate = true,
        };
        if (source.AcceptAnyCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handlerOverride ?? handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(source.UserName))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.UserName}:{password}")));
    }

    private Uri UrlFor(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return new Uri(_base, string.Join('/', segments));
    }

    public async Task TestAsync(CancellationToken ct) => await ListAsync("/", ct);

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct)
    {
        var url = UrlFor(folder);
        if (!url.AbsoluteUri.EndsWith('/')) url = new Uri(url.AbsoluteUri + "/");
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>""",
            Encoding.UTF8, "application/xml");
        using var response = await _http.SendAsync(request, ct);
        ThrowForStatus(response);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var self = Uri.UnescapeDataString(url.AbsolutePath).TrimEnd('/');
        var basePath = Uri.UnescapeDataString(_base.AbsolutePath).TrimEnd('/');
        var entries = new List<RemoteEntry>();
        foreach (var item in xml.Descendants(Dav + "response"))
        {
            var href = item.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            var absolute = Uri.UnescapeDataString(Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs.AbsolutePath : href).TrimEnd('/');
            if (string.Equals(absolute, self, StringComparison.Ordinal)) continue; // the folder itself

            var prop = item.Descendants(Dav + "prop").FirstOrDefault();
            var isDir = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, out var size);
            DateTime.TryParse(prop?.Element(Dav + "getlastmodified")?.Value, out var modified);
            var relative = absolute.StartsWith(basePath, StringComparison.Ordinal) ? absolute[basePath.Length..] : absolute;
            if (relative.Length == 0) relative = "/";
            var name = relative.TrimEnd('/')[(relative.TrimEnd('/').LastIndexOf('/') + 1)..];
            entries.Add(new RemoteEntry(name, relative, isDir, size, modified));
        }
        return entries;
    }

    public long GetSize(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, UrlFor(path));
        using var response = _http.Send(request);
        ThrowForStatus(response);
        return response.Content.Headers.ContentLength ?? 0;
    }

    public Stream OpenRead(string path, long offset)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UrlFor(path));
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead);
        ThrowForStatus(response);
        var stream = response.Content.ReadAsStream();
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            Skip(stream, offset); // server ignores ranges: read up to the offset
        return new ReturningStream(stream, response.Dispose);
    }

    public void Upload(string path, Stream content)
    {
        var temp = path + ".tunesync-upload";
        using (var put = new HttpRequestMessage(HttpMethod.Put, UrlFor(temp)) { Content = new StreamContent(content) })
        using (var response = _http.Send(put))
            ThrowForStatus(response);

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), UrlFor(temp));
        move.Headers.Add("Destination", UrlFor(path).AbsoluteUri);
        move.Headers.Add("Overwrite", "T");
        using var moved = _http.Send(move);
        ThrowForStatus(moved);
    }

    private static void Skip(Stream stream, long count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) break;
            count -= read;
        }
    }

    private static void ThrowForStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new IOException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "사용자 이름 또는 비밀번호가 맞지 않습니다.",
            HttpStatusCode.Forbidden => "이 위치에 접근할 권한이 없습니다.",
            HttpStatusCode.NotFound => "경로를 찾을 수 없습니다.",
            HttpStatusCode.MethodNotAllowed => "이 주소는 WebDAV를 지원하지 않습니다.",
            _ => $"서버 오류 ({(int)response.StatusCode} {response.ReasonPhrase})",
        });
    }

    public void Dispose() => _http.Dispose();
}
