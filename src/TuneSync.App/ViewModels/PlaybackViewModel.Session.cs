using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

// Picking up at startup where the last session ended, and remembering that spot on close.
public sealed partial class PlaybackViewModel
{
    // --- session: reopen on the last song, paused where it was left ---

    /// <summary>As the library streams in at startup, picks up the song that was loaded when the
    /// app was last closed (설정 › 시작: at the same spot, or from its beginning, or not at all) and
    /// either waits, paused, or - with 시작하면 바로 재생 - plays it. With auto-play and no song to
    /// pick up, the queue's first song plays once the library has loaded.</summary>
    private void WatchForSessionToResume()
    {
        var mode = _settings.StartupResume;
        var autoPlay = _settings.AutoPlayOnStartup;
        var path = mode == StartupResume.Nothing ? null : _settings.LastFilePath;
        if (string.IsNullOrEmpty(path))
        {
            if (autoPlay) PlayFirstWhenLoaded();
            return;
        }
        var position = mode == StartupResume.LastPosition
            ? TimeSpan.FromSeconds(Math.Max(0, _settings.LastPositionSeconds))
            : TimeSpan.Zero;

        System.Collections.Specialized.NotifyCollectionChangedEventHandler? onFiles = null;
        System.ComponentModel.PropertyChangedEventHandler? onScan = null;
        void StopWatching()
        {
            _library.Files.CollectionChanged -= onFiles;
            _library.PropertyChanged -= onScan;
        }
        onFiles = (_, e) =>
        {
            if (CurrentFile is not null) { StopWatching(); return; } // the user already picked something
            // Songs arrive in batches (a Reset each), so look through the whole list.
            var added = e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset
                ? _library.Files
                : e.NewItems?.OfType<LibraryFileViewModel>() ?? [];
            if (added.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } file) return;
            StopWatching();
            // After the current batch of adds, so the queue can select and scroll to it.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ResumeSession(file, position, autoPlay),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        onScan = (_, e) =>
        {
            if (e.PropertyName != nameof(LibraryViewModel.IsScanning) || _library.IsScanning) return;
            StopWatching(); // the last song isn't in the library anymore
            if (autoPlay) PlayFirstWhenLoaded();
        };
        _library.Files.CollectionChanged += onFiles;
        _library.PropertyChanged += onScan;
    }

    /// <summary>Auto-play with nothing to pick up: once the library has loaded, the queue's first
    /// song (a random one with shuffle on) - unless something was chosen meanwhile.</summary>
    private void PlayFirstWhenLoaded()
    {
        void Go()
        {
            if (CurrentFile is null && _library.Files.Count > 0) PlayNext();
        }
        if (!_library.IsScanning && _library.Files.Count > 0)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Go, System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        System.ComponentModel.PropertyChangedEventHandler? onScan = null;
        onScan = (_, e) =>
        {
            if (e.PropertyName != nameof(LibraryViewModel.IsScanning) || _library.IsScanning) return;
            _library.PropertyChanged -= onScan;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Go, System.Windows.Threading.DispatcherPriority.Background);
        };
        _library.PropertyChanged += onScan;
    }

    private void ResumeSession(LibraryFileViewModel file, TimeSpan position, bool play)
    {
        if (CurrentFile is not null) return;
        PlayFile(file);
        // PlayFile starts playing once the file opens; ask it to go to the saved spot and either
        // wait there, paused, or carry on playing (시작하면 바로 재생).
        _pendingRestore = (position, play);
        if (position > TimeSpan.FromSeconds(1))
        {
            _introUntil = null; // picking up mid-song: straight to the lyrics
            ShowPosition(position);
        }
    }

    /// <summary>Remembers the loaded song and position for next time (called on close).</summary>
    public void SaveSession()
    {
        _settings.LastFilePath = PlayingPath; // where it is now, also when it was renamed while playing
        _settings.LastPositionSeconds = CurrentFile is null ? 0 : CurrentOrShownPosition().TotalSeconds;
        AppSettingsStore.Save(_settings);
    }
}
