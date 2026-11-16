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

    /// <summary>The lyrics pane was hidden when the equalizer opened (it opens the pane for
    /// itself): closing the equalizer hides it again.</summary>
    private bool _lyricsOpenedForEqualizer;

    /// <summary>The equalizer is showing in the lyrics' place (no intro staging meanwhile).</summary>
    private bool IsEqualizerOpen => _equalizer?.IsPanelOpen == true;

    private void HookEqualizer()
    {
        _equalizer = ((App)Application.Current).Services.GetRequiredService<EqualizerViewModel>();
        _equalizer.PropertyChanged += Equalizer_PropertyChanged;
        // The lyrics pane closed (its button in the playback bar) while the equalizer was in it:
        // the equalizer goes with it.
        _viewModel.Playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlaybackViewModel.IsLyricsVisible) || _viewModel.Playback.IsLyricsVisible || !_equalizer.IsPanelOpen) return;
            _lyricsOpenedForEqualizer = false;
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

    private void ShowEqualizer(bool animate)
    {
        var playback = _viewModel.Playback;
        if (!playback.IsLyricsVisible)
        {
            _lyricsOpenedForEqualizer = true;
            playback.IsLyricsVisible = true;
        }
        HideBackToLyric();
        LyricsLayer.Visibility = Visibility.Hidden;
        EqPanel.Visibility = Visibility.Visible;
        if (animate) FadeSlideIn(EqPanel, 0, 14, duration: TimeSpan.FromMilliseconds(260));
    }

    private void HideEqualizer()
    {
        EqPanel.Visibility = Visibility.Collapsed;
        LyricsLayer.Visibility = Visibility.Visible;
        if (_lyricsOpenedForEqualizer)
        {
            _lyricsOpenedForEqualizer = false;
            _viewModel.Playback.IsLyricsVisible = false;
            return;
        }
        FadeSlideIn(LyricsLayer, 0, 14, duration: TimeSpan.FromMilliseconds(260));
        // back on the line being sung
        Dispatcher.BeginInvoke(FollowCurrentLyric, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
