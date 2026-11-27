using System.Net;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

public class ModelDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}");

    private string ModelPath => Path.Combine(_folder, "model.bin");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* already gone */ }
    }

    private sealed class Server(Func<HttpContent> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body(), RequestMessage = request });
    }

    /// <summary>Bytes without a length up front (like a chunked answer): <paramref name="count"/>
    /// of them, or endless when null.</summary>
    private sealed class Body(long? count) : Stream
    {
        public long Sent { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int max)
        {
            var n = count is { } total ? (int)Math.Min(max, total - Sent) : max;
            for (var i = 0; i < n; i++) buffer[offset + i] = (byte)(Sent + i);
            Sent += n;
            return n;
        }
    }

    private Task Download(Func<HttpContent> body, long expectedBytes, IProgress<double>? progress = null, CancellationToken ct = default,
        string url = "https://models.example/model.bin", string sha256 = "00") =>
        ModelDownload.ToFileAsync(new HttpClient(new Server(body)), url, ModelPath, expectedBytes, sha256, progress, ct);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private void AssertNothingLeft()
    {
        Assert.False(File.Exists(ModelPath));
        Assert.False(File.Exists(ModelPath + ".part"));
    }

    [Fact]
    public async Task AWholeModelIsMovedIntoPlace()
    {
        var model = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();

        await Download(() => new ByteArrayContent(model), model.Length, sha256: Sha256(model).ToLowerInvariant());

        Assert.Equal(model, File.ReadAllBytes(ModelPath));
        Assert.False(File.Exists(ModelPath + ".part"));
    }

    [Fact]
    public async Task OtherBytesOfTheRightSizeAreRefused()
    {
        var model = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        var swapped = (byte[])model.Clone();
        swapped[150_000] ^= 0xFF;

        var ex = await Assert.ThrowsAsync<IOException>(() => Download(() => new ByteArrayContent(swapped), model.Length, sha256: Sha256(model)));

        Assert.Contains("예상과 다릅니다", ex.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task AnotherAnnouncedSizeIsRefused_BeforeDownloading()
    {
        var body = new Body(2000);

        var ex = await Assert.ThrowsAsync<IOException>(() => Download(() => new StreamContent(body) { Headers = { ContentLength = 2000 } }, 1000));

        Assert.Contains("예상과 다릅니다", ex.Message);
        Assert.Equal(0, body.Sent);
        AssertNothingLeft();
    }

    [Fact]
    public async Task AServerThatKeepsSendingIsStoppedAtTheModelsSize()
    {
        var body = new Body(null);

        var ex = await Assert.ThrowsAsync<IOException>(() => Download(() => new StreamContent(body), 1_000_000));

        Assert.Contains("예상과 다릅니다", ex.Message);
        Assert.InRange(body.Sent, 1_000_001, 1_000_000 + 2 * 65536);
        AssertNothingLeft();
    }

    [Fact]
    public async Task AShortDownloadSaysSo_AndLeavesNothing()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => Download(() => new StreamContent(new Body(500_000)), 1_000_000));

        Assert.Contains("끝까지 받지 못했습니다", ex.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task CancellingLeavesNoPartFile()
    {
        using var cts = new CancellationTokenSource();
        var cancelOnFirstReport = new CallbackProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Download(() => new StreamContent(new Body(1_000_000)), 1_000_000, cancelOnFirstReport, cts.Token));

        AssertNothingLeft();
    }

    [Fact]
    public async Task OnlyOverHttps()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Download(() => new ByteArrayContent([1, 2, 3]), 3, url: "http://models.example/model.bin"));

        AssertNothingLeft();
    }

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
