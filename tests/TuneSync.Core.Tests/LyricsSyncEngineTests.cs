using Microsoft.ML.OnnxRuntime;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

/// <summary>The models are the app's own downloads, checked only by size: one that can't be read
/// is deleted, so the next sync downloads it again. (These open ONNX Runtime and whisper.cpp on
/// the CPU, on files that aren't models.)</summary>
public class LyricsSyncEngineTests : IDisposable
{
    private static readonly LyricsSyncOptions Cpu = new(UseGpu: false, Threads: 1);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}");

    public LyricsSyncEngineTests()
    {
        Directory.CreateDirectory(_folder);
        // Only these tests use the store, and one at a time (one class).
        WhisperModelStore.Folder = _folder;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* already gone */ }
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", name);

    [Fact]
    public void AVocalModelThatCantBeReadIsDeleted()
    {
        File.WriteAllBytes(VocalModelStore.ModelPath, new byte[4096]);
        using var engine = new LyricsSyncEngine(WhisperModelStore.ModelPath, VocalModelStore.ModelPath, Cpu);

        var ex = Assert.Throws<InvalidDataException>(() => engine.SeparateVocals(Asset("sample.mp3"), null, CancellationToken.None));

        Assert.Contains("보컬 분리 모델", ex.Message);
        Assert.False(File.Exists(VocalModelStore.ModelPath));
    }

    [Fact]
    public void AModelGivenByPathIsLeftAlone()
    {
        var mine = Path.Combine(_folder, "mine.onnx");
        File.WriteAllBytes(mine, new byte[4096]);
        using var engine = new LyricsSyncEngine(WhisperModelStore.ModelPath, mine, Cpu);

        Assert.Throws<OnnxRuntimeException>(() => engine.SeparateVocals(Asset("sample.mp3"), null, CancellationToken.None));

        Assert.True(File.Exists(mine));
    }

    [Fact]
    public async Task ASpeechModelThatCantBeReadIsDeleted()
    {
        File.WriteAllBytes(WhisperModelStore.ModelPath, new byte[1 << 20]);
        using var engine = new LyricsSyncEngine(WhisperModelStore.ModelPath, null, Cpu);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => engine.SyncAsync(Asset("sample.mp3"), "한 줄", null, CancellationToken.None));

        Assert.Contains("음성 인식 모델", ex.Message);
        Assert.False(File.Exists(WhisperModelStore.ModelPath));
    }

    [Fact]
    public void NoModelIsOpenedOnceDisposed()
    {
        File.WriteAllBytes(VocalModelStore.ModelPath, new byte[4096]);
        var engine = new LyricsSyncEngine(WhisperModelStore.ModelPath, VocalModelStore.ModelPath, Cpu);
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => engine.SeparateVocals(Asset("sample.mp3"), null, CancellationToken.None));
        Assert.True(File.Exists(VocalModelStore.ModelPath)); // not even tried
    }
}
