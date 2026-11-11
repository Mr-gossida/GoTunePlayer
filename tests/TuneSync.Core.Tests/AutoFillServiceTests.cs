using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class AutoFillServiceTests
{
    private static LibraryFile MakeFile(string path, TrackTags tags, bool hasCover = false, bool hasLyrics = false) => new()
    {
        Path = path,
        Tags = tags,
        HasCover = hasCover,
        HasLyrics = hasLyrics,
    };

    [Fact]
    public async Task BuildProposalsAsync_OnlyFillsMissingFields_NeverOverwritesExisting()
    {
        var file = MakeFile("x.mp3", new TrackTags { Path = "x.mp3", Artist = "IU", Title = "Good Day", Album = "" });
        var mb = new FakeMusicBrainzService
        {
            NextMatch = new MusicBrainzMatch { Title = "Good Day (다른 제목)", Artist = "다른 아티스트", Album = "Real Album" },
        };
        var service = new AutoFillService(mb, new FakeLyricsService());

        var proposals = await service.BuildProposalsAsync([file]);

        var proposal = Assert.Single(proposals);
        Assert.Null(proposal.ProposedTitle); // already set - must not be overwritten
        Assert.Null(proposal.ProposedArtist); // already set - must not be overwritten
        Assert.Equal("Real Album", proposal.ProposedAlbum); // was empty - should be filled
    }

    [Fact]
    public async Task BuildProposalsAsync_UsesFilenameAsSearchKey_WhenTagsAreEmpty()
    {
        var file = MakeFile(@"C:\music\[IU] Good Day.mp3", new TrackTags { Path = @"C:\music\[IU] Good Day.mp3" });
        var mb = new FakeMusicBrainzService { NextMatch = null };
        var service = new AutoFillService(mb, new FakeLyricsService());

        await service.BuildProposalsAsync([file]);

        var call = Assert.Single(mb.Calls);
        Assert.Equal("IU", call.Artist);
        Assert.Equal("Good Day", call.Title);
    }

    [Fact]
    public async Task BuildProposalsAsync_LyricsOnly_SkipsMusicBrainzAndSearchesWithExistingTags()
    {
        var file = MakeFile("x.mp3", new TrackTags { Path = "x.mp3", Artist = "IU", Title = "Good Day", Album = "" });
        var mb = new FakeMusicBrainzService { NextMatch = new MusicBrainzMatch { Title = "Good Day", Artist = "IU", Album = "Real Album" } };
        var lyrics = new FakeLyricsService { NextResult = new LyricsResult { TrackName = "Good Day", ArtistName = "IU", PlainLyrics = "가사" } };
        var service = new AutoFillService(mb, lyrics);

        var proposals = await service.BuildProposalsAsync([file], options: new AutoFillOptions(Tags: false, Cover: false, Lyrics: true));

        var proposal = Assert.Single(proposals);
        Assert.Empty(mb.Calls);
        Assert.Equal("가사", proposal.ProposedLyrics);
        Assert.Null(proposal.ProposedAlbum); // tags weren't asked for, even though the album is empty
    }

    [Fact]
    public async Task BuildProposalsAsync_ExistingCover_IsOnlySearchedWhenReplacementRequested()
    {
        var file = MakeFile("x.mp3", new TrackTags { Path = "x.mp3", Artist = "IU", Title = "Good Day" }, hasCover: true);
        var mb = new FakeMusicBrainzService
        {
            NextMatch = new MusicBrainzMatch { Title = "Good Day", Artist = "IU", ReleaseMbid = "rel-1" },
            NextCover = [1, 2, 3],
        };
        var service = new AutoFillService(mb, new FakeLyricsService());

        var keep = await service.BuildProposalsAsync([file], options: new AutoFillOptions(Tags: false, Cover: true, Lyrics: false));
        var replace = await service.BuildProposalsAsync([file], options: new AutoFillOptions(Tags: false, Cover: true, Lyrics: false, ReplaceExisting: true));

        Assert.Equal("채울 항목 없음", Assert.Single(keep).Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(replace).ProposedCoverBytes);
    }

    [Fact]
    public async Task BuildProposalsAsync_ReportsUnsearchable_WhenNoTagsAndNoParsableFilename()
    {
        var file = MakeFile(@"C:\music\track001.mp3", new TrackTags { Path = @"C:\music\track001.mp3" });
        var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService());

        var proposals = await service.BuildProposalsAsync([file]);

        Assert.Contains("검색 불가", Assert.Single(proposals).Status);
    }

    [Fact]
    public void ApplyProposals_WritesTagsCoverAndLyrics_ToDisk()
    {
        var path = TestFixtures.CopyFixture("sample.flac");
        try
        {
            var file = MakeFile(path, new TrackTags { Path = path, Artist = "IU", Title = "Good Day" });
            var proposal = new AutoFillProposal
            {
                File = file,
                ProposedAlbum = "Real Album",
                ProposedCoverBytes = [1, 2, 3, 4],
                ProposedLyrics = "가사 내용",
            };
            var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService());

            var errors = service.ApplyProposals([proposal]);

            Assert.Empty(errors);
            var reread = TagService.Read(path);
            Assert.Equal("Real Album", reread.Album);
            Assert.Equal("가사 내용", TagService.ReadLyrics(path));
            Assert.Equal("적용 완료", proposal.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyProposals_WritesUndoLog_WhenDirectoryGiven()
    {
        var path = TestFixtures.CopyFixture("sample.mp3");
        var undoDir = Path.Combine(Path.GetTempPath(), $"tunesync-undo-{Guid.NewGuid():N}");
        try
        {
            var file = MakeFile(path, new TrackTags { Path = path, Artist = "IU", Title = "Good Day" });
            var proposal = new AutoFillProposal { File = file, ProposedAlbum = "Real Album" };
            var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService());

            service.ApplyProposals([proposal], undoDir);

            Assert.True(Directory.Exists(undoDir));
            Assert.Single(Directory.GetFiles(undoDir, "*.json"));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(undoDir)) Directory.Delete(undoDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyProposals_SkipsProposalsWithNoChanges()
    {
        var file = MakeFile("x.mp3", new TrackTags { Path = "x.mp3", Artist = "IU", Title = "Good Day" });
        var proposal = new AutoFillProposal { File = file }; // no proposed fields at all
        var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService());

        var errors = service.ApplyProposals([proposal]);

        Assert.Empty(errors);
        Assert.Equal("대기 중", proposal.Status); // untouched - file "x.mp3" doesn't even exist on disk
    }
}
