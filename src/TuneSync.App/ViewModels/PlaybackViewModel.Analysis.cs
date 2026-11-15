using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

// What is worked out about the playing song in the background: its cover at full quality, audio
// quality line, waveform and spectrum.
public sealed partial class PlaybackViewModel
{
    private readonly BackgroundAnalysis<WaveformData?> _waveformAnalysis = new(capacity: 30);
    private readonly BackgroundAnalysis<TuneSync.Core.Services.SpectrumData?> _spectrumAnalysis = new(capacity: 10);
    private int _coverVersion;

    /// <summary>The playing track's cover at full quality for the large art on the 플레이 page and
    /// the playback bar. The library only keeps a small thumbnail per file (to stay light with
    /// thousands of songs), which looked blurry blown up to 240px+ on a scaled display.</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _currentCover;

    /// <summary>The same cover decoded at 256px, for the small spots (bottom bar, narrow-layout
    /// header, mini player strip). Scaling the 1000px original down to ~60-90px in high quality
    /// is CPU work WPF repeats whenever such an element reappears, which caused a visible hitch.</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _currentCoverSmall;

    /// <summary>A tiny pre-blurred copy of the cover, stretched behind the player.</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _backdropCover;

    // --- audio quality: "FLAC · 24bit · 96kHz · 2,304kbps" with a Hi-Res / Lossless badge ---

    private int _qualityVersion;

    [ObservableProperty]
    private string _qualityText = "";

    /// <summary>"Hi-Res", "Lossless", or empty for lossy formats.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQualityBadge))]
    private string _qualityBadge = "";

    public bool HasQualityBadge => QualityBadge.Length > 0;

    /// <summary>Read off the UI thread (it opens the file); a placeholder from the extension
    /// shows meanwhile so the line never flickers empty.</summary>
    private async void LoadQuality(LibraryFileViewModel file)
    {
        var version = ++_qualityVersion;
        QualityText = System.IO.Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant();
        QualityBadge = "";
        AudioQuality? quality;
        try
        {
            quality = await Task.Run(() => TagService.ReadAudioQuality(PlayablePath.ResolveForReading(file.Path)));
        }
        catch
        {
            return; // unreadable right now: keep the format alone
        }
        if (version != _qualityVersion || quality is null) return;
        QualityText = quality.Summary;
        QualityBadge = quality.Badge ?? "";
    }

    /// <summary>The playing track's real loudness over time (for the seek bar); null while it's
    /// being analyzed or when the format can't be decoded.</summary>
    [ObservableProperty]
    private WaveformData? _waveform;

    /// <summary>The playing song's spectrum over time, for the analyzer bars (null until it has
    /// been worked out, about a second after the song starts).</summary>
    [ObservableProperty]
    private TuneSync.Core.Services.SpectrumData? _spectrum;

    /// <summary>Works out the spectrum in the background, like the waveform (and from the same
    /// downloaded copy for a network song); unreadable, the bars just stay down.</summary>
    private void LoadSpectrum(string path) => _spectrumAnalysis.Load(path, ReadablePathAsync,
        (file, ct) => TuneSync.Core.Services.SpectrumAnalyzer.Analyze(file, ct: ct), result => Spectrum = result, IsCurrentSong);

    private bool IsCurrentSong(string path) => string.Equals(CurrentFile?.Path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>A local file the analyzers can read: the song itself, or for a network song its
    /// cached copy once the download has finished.</summary>
    private async Task<string> ReadablePathAsync(string path) =>
        RemotePath.IsRemote(path)
            ? await _network.Cache.CompletedPathAsync(path, keep: false)
            : PlayablePath.ResolveForReading(path);

    /// <summary>Decodes the track in the background (about a second for a typical song) so the
    /// seek bar can draw its real waveform. Recently played tracks come from a small cache;
    /// unreadable (a network song that couldn't be fetched), the seek bar keeps its plain wave.</summary>
    private void LoadWaveform(string path) => _waveformAnalysis.Load(path, ReadablePathAsync,
        WaveformAnalyzer.Analyze, result => Waveform = result, IsCurrentSong);

    /// <summary>The cover of the playing song was changed (태그 관리 / 자동 채움): show the new one.</summary>
    private void CurrentFile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryFileViewModel.CoverThumbnail) && sender is LibraryFileViewModel file && file == CurrentFile)
            LoadCover(file);
    }

    /// <summary>Shows the thumbnail at once, then swaps in the embedded original (read and
    /// decoded off the UI thread, at most 1000px wide) as soon as it's ready, and recolors the
    /// player from it.</summary>
    private async void LoadCover(LibraryFileViewModel file)
    {
        var version = ++_coverVersion;
        CurrentCover = file.CoverThumbnail;
        CurrentCoverSmall = file.CoverThumbnail;
        if (!file.HasCover)
        {
            BackdropCover = null;
            PlayerPalette.Apply(null);
            return;
        }

        (System.Windows.Media.Imaging.BitmapImage? Full, System.Windows.Media.Imaging.BitmapImage? Small,
         System.Windows.Media.Imaging.BitmapSource? Backdrop, System.Windows.Media.Color? Key) result;
        var thumbnail = file.CoverThumbnail;
        try
        {
            result = await Task.Run(() =>
            {
                var bytes = CoverArtService.GetCover(PlayablePath.ResolveForReading(file.Path));
                var full = ImageHelper.ToBitmapImage(bytes, decodePixelWidth: 1000);
                var small = ImageHelper.ToBitmapImage(bytes, decodePixelWidth: 256);
                System.Windows.Media.Imaging.BitmapSource? source = ImageHelper.ToBitmapImage(bytes, decodePixelWidth: 64) ?? thumbnail;
                return (full, small, ImageHelper.BlurredBackdrop(source), source is null ? (System.Windows.Media.Color?)null : PlayerPalette.ExtractKeyColor(source));
            });
        }
        catch
        {
            // Unreadable right now (file locked/share offline): keep the thumbnail.
            result = (null, null, ImageHelper.BlurredBackdrop(thumbnail), thumbnail is null ? null : PlayerPalette.ExtractKeyColor(thumbnail));
        }
        if (version != _coverVersion) return;

        if (result.Full is not null) CurrentCover = result.Full;
        if (result.Small is not null) CurrentCoverSmall = result.Small;
        BackdropCover = result.Backdrop;
        PlayerPalette.Apply(result.Key);
    }
}
