using System.Buffers;

namespace TuneSync.Core.Audio;

/// <summary>Runs a 64-bit processing step on float frames: widened, processed, narrowed back.
/// For tests and tools that hold float buffers - the player's chain is 64-bit throughout.</summary>
internal static class Wide
{
    public delegate void Step<T>(T owner, Span<double> frames);

    public static void Run<T>(Span<float> frames, T owner, Step<T> step)
    {
        var wide = ArrayPool<double>.Shared.Rent(frames.Length);
        try
        {
            var span = wide.AsSpan(0, frames.Length);
            for (var i = 0; i < frames.Length; i++) span[i] = frames[i];
            step(owner, span);
            for (var i = 0; i < frames.Length; i++) frames[i] = (float)span[i];
        }
        finally
        {
            ArrayPool<double>.Shared.Return(wide);
        }
    }
}
