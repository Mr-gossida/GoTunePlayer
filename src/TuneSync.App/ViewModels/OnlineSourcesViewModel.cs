using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.App.ViewModels;

/// <summary>One place 태그 찾기 or 가사 찾기 may ask, as a chip the user switches on or off. Its
/// tooltip says what it is - so where the found values come from is never a mystery.</summary>
public sealed partial class SearchSourceViewModel(string name, string description, bool isOnline) : ObservableObject
{
    /// <summary>The name, as the results' badges show it.</summary>
    public string Name { get; } = name;

    public string Description { get; } = description;

    /// <summary>Asked over the internet (only the artist, title and album are sent); false for
    /// what's on this PC.</summary>
    public bool IsOnline { get; } = isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isOn = true;

    /// <summary>The only one still on: it can't be switched off (a search must ask somewhere).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isLast;

    public bool CanToggle => !IsLast;

    partial void OnIsLastChanged(bool value) => OnPropertyChanged(nameof(CanToggle));

    public string ToolTip => $"{Name}: {Description}" + (IsLast ? "\n\n출처를 하나 이상 켜 두어야 합니다." : IsOn ? "\n\n누르면 이 출처를 빼고 찾습니다." : "\n\n누르면 이 출처에서도 찾습니다.");

    /// <summary>Raised when the user switched it.</summary>
    public event Action? Switched;

    partial void OnIsOnChanged(bool value) => Switched?.Invoke();
}

/// <summary>Where 태그 찾기 and 가사 찾기 look, as the user has chosen (설정 › 도구 › 온라인 검색,
/// and the chips beside the search buttons - the same objects, so both always agree). Kept in
/// the settings as the sources switched off.</summary>
public sealed class OnlineSourcesViewModel
{
    private readonly AppSettings _settings;

    public OnlineSourcesViewModel(AppSettings settings)
    {
        _settings = settings;
        TagSources =
        [
            new(MusicBrainzTagSource.SourceName, "누구나 함께 고쳐 가는 공개 음악 백과사전입니다(무료 오픈 데이터). 앨범·앨범 아티스트·트랙·디스크 번호가 정확한 편입니다.", true),
            new(ITunesTagSource.SourceName, "애플 음악 스토어의 공개 검색입니다(미국·일본 스토어). 장르와 발매 연도가 있고, 한국 곡은 영문 이름으로 나올 때가 많습니다.", true),
            new(DeezerTagSource.SourceName, "음악 스트리밍 서비스 Deezer의 공개 검색입니다. 장르·발매일·앨범 아티스트가 있습니다.", true),
        ];
        LyricsSources =
        [
            new(LrcLibLyricsService.SourceName, "누구나 올려 함께 쓰는 공개 가사 데이터베이스입니다(무료). 싱크 가사가 많습니다.", true),
            new(LocalLyricsSource.SourceName, "곡과 같은 폴더에 있는 같은 이름의 .lrc·.txt 파일입니다. 이 PC에 있는 곡만 찾고, 인터넷으로 보내는 것은 없습니다.", false),
        ];
        Load(TagSources, settings.DisabledTagSources);
        Load(LyricsSources, settings.DisabledLyricsSources);
    }

    public IReadOnlyList<SearchSourceViewModel> TagSources { get; }

    public IReadOnlyList<SearchSourceViewModel> LyricsSources { get; }

    /// <summary>The tag sources switched off.</summary>
    public IReadOnlyCollection<string> DisabledTagSources => _settings.DisabledTagSources;

    public bool IsLyricsSourceOn(string name) => LyricsSources.FirstOrDefault(s => s.Name == name)?.IsOn ?? false;

    /// <summary>"MusicBrainz · iTunes" - the sources on, for the status line.</summary>
    public static string Names(IEnumerable<SearchSourceViewModel> sources) => string.Join(" · ", sources.Where(s => s.IsOn).Select(s => s.Name));

    private void Load(IReadOnlyList<SearchSourceViewModel> sources, List<string> disabled)
    {
        foreach (var source in sources) source.IsOn = !disabled.Contains(source.Name);
        // all of them off (settings edited by hand): a search must ask somewhere
        if (sources.All(s => !s.IsOn)) foreach (var source in sources) source.IsOn = true;
        Lock(sources);
        foreach (var source in sources) source.Switched += () => Changed(sources, disabled);
    }

    private void Changed(IReadOnlyList<SearchSourceViewModel> sources, List<string> disabled)
    {
        disabled.Clear();
        disabled.AddRange(sources.Where(s => !s.IsOn).Select(s => s.Name));
        Lock(sources);
        AppSettingsStore.Save(_settings);
    }

    /// <summary>The last source on can't be switched off.</summary>
    private static void Lock(IReadOnlyList<SearchSourceViewModel> sources)
    {
        var on = sources.Count(s => s.IsOn);
        foreach (var source in sources) source.IsLast = on == 1 && source.IsOn;
    }
}
