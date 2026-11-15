namespace TuneSync.Core.Services.LyricsSync;

/// <summary>When the singer is singing, read off the loudness of the separated vocals
/// (16 kHz mono). Speech recognition is vague about where a phrase starts after a pause - it
/// tends to round to a whole second, or to count the pause as part of the word - while the
/// vocals themselves show it to a few hundredths of a second.</summary>
public sealed class VoiceActivity
{
    private const double FrameSeconds = 0.02;

    /// <summary>Stretches of singing, in seconds, in order.</summary>
    public IReadOnlyList<(double Start, double End)> Regions { get; }

    private VoiceActivity(List<(double, double)> regions) => Regions = regions;

    /// <param name="threshold">Loudness, as a share of the song's loud singing, above which a
    /// moment counts as singing.</param>
    /// <param name="minGap">Pauses shorter than this don't split a stretch.</param>
    public static VoiceActivity Detect(float[] vocals, double threshold = 0.06, double minGap = 0.25, double minLength = 0.12)
    {
        var frame = (int)(AudioDecoder.SampleRate * FrameSeconds);
        var count = vocals.Length / frame;
        if (count == 0) return new VoiceActivity([]);
        var level = new float[count];
        for (var f = 0; f < count; f++)
        {
            double sum = 0;
            for (int i = f * frame, end = i + frame; i < end; i++) sum += vocals[i] * vocals[i];
            level[f] = (float)Math.Sqrt(sum / frame);
        }

        var sorted = (float[])level.Clone();
        Array.Sort(sorted);
        var limit = sorted[(int)(count * 0.95)] * threshold;

        var regions = new List<(double Start, double End)>();
        var start = -1;
        for (var f = 0; f <= count; f++)
        {
            var on = f < count && level[f] > limit;
            if (on && start < 0) start = f;
            else if (!on && start >= 0)
            {
                var (a, b) = (start * FrameSeconds, f * FrameSeconds);
                if (regions.Count > 0 && a - regions[^1].End < minGap) regions[^1] = (regions[^1].Start, b);
                else regions.Add((a, b));
                start = -1;
            }
        }
        regions.RemoveAll(r => r.End - r.Start < minLength);
        return new VoiceActivity(regions);
    }

    /// <summary>Pieces of at most <paramref name="maxSeconds"/> to listen to, each starting
    /// where singing starts and cut only in pauses; long pauses are left out altogether.</summary>
    public List<(double Start, double End)> Windows(double maxSeconds = 30, double splitGap = 2, double pad = 0.3)
    {
        var windows = new List<(double Start, double End)>();
        var i = 0;
        var carry = -1.0; // where a stretch too long for one window was cut
        while (i < Regions.Count)
        {
            var start = carry >= 0 ? carry : Math.Max(windows.Count > 0 ? windows[^1].End : 0, Regions[i].Start - pad);
            carry = -1;
            var end = start;
            while (i < Regions.Count)
            {
                var r = Regions[i];
                if (end > start && r.Start - end >= splitGap) break;
                if (r.End + pad - start > maxSeconds)
                {
                    if (end > start) break;           // cut in the pause before this stretch
                    end = start + maxSeconds;         // one stretch longer than a window: cut inside it
                    carry = end;
                    if (r.End <= end) i++;
                    break;
                }
                end = r.End;
                i++;
            }
            windows.Add((start, carry >= 0 ? end : end + pad));
        }
        return windows;
    }
}
