using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

// The equalizer shows in the lyrics' place (EqualizerViewModel.IsPanelOpen): the lyrics stay laid
// out underneath (hidden, so they don't draw), and come back as they were when it closes.
public partial class PlayView
{
    private EqualizerViewModel? _equalizer;

    /// <summary>The equalizer is showing in the lyrics' place (no intro staging meanwhile).</summary>
    private bool IsEqualizerOpen => _equalizer?.IsPanelOpen == true;

    private void HookEqualizer()
    {
        _equalizer = ((App)Application.Current).Services.GetRequiredService<EqualizerViewModel>();
        _equalizer.PropertyChanged += Equalizer_PropertyChanged;
        // The 가사 button in the playback bar while the equalizer is open: it is about the lyrics,
        // so the equalizer makes way - turned on, the lyrics show; turned off, the cover alone.
        _viewModel.Playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackViewModel.IsLyricsVisible) && _equalizer.IsPanelOpen)
                _equalizer.IsPanelOpen = false;
        };
        if (_equalizer.IsPanelOpen) ShowEqualizer(animate: false);
    }

    private void Equalizer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EqualizerViewModel.IsPanelOpen) || _equalizer is null) return;
        if (_equalizer.IsPanelOpen) ShowEqualizer(animate: true);
        else HideEqualizer();
        // An intro's hero card steps back to the left while the equalizer shows, and returns after.
        ApplyIntroStage(animate: true);
    }

    /// <summary>Into the area beside the cover - opened for it when the lyrics are off, without
    /// turning the lyrics on (the 가사 button keeps its state).</summary>
    private void ShowEqualizer(bool animate)
    {
        HideBackToLyric();
        LyricsLayer.Visibility = Visibility.Hidden;
        EqPanel.Visibility = Visibility.Visible;
        _viewModel.Playback.IsEqualizerShown = true;
        if (animate) FadeSlideIn(EqPanel, 0, 14, duration: TimeSpan.FromMilliseconds(260));
    }

    /// <summary>Back to what was there before: the lyrics, or (lyrics off) the cover alone.</summary>
    private void HideEqualizer()
    {
        EqPanel.Visibility = Visibility.Collapsed;
        LyricsLayer.Visibility = Visibility.Visible;
        _viewModel.Playback.IsEqualizerShown = false;
        if (!_viewModel.Playback.IsLyricsVisible) return;
        FadeSlideIn(LyricsLayer, 0, 14, duration: TimeSpan.FromMilliseconds(260));
        // back on the line being sung
        Dispatcher.BeginInvoke(FollowCurrentLyric, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
