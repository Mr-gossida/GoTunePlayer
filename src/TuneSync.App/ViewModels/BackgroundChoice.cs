using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>An entry of 설정 › 화면 효과 › 배경 효과: the effect, and for 무작위 whether it picks a
/// new one for every song (<see cref="AppSettings.ScreenSaverLookPerSong"/>).</summary>
public readonly record struct BackgroundChoice(BackgroundEffect Effect, bool PerSong);
