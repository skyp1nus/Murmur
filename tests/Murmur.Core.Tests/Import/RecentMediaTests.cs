using Murmur.Core.Import;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Import;

/// <summary>The "Recent in Downloads" search of the import dialog (#67): which files of a folder it lists, in which order.</summary>
public sealed class RecentMediaTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);
    private static readonly HashSet<string> Media = new(StringComparer.Ordinal) { ".wav", ".mp3", ".mp4" };

    private readonly TempDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private string Write(string name, TimeSpan age, int bytes = 3)
    {
        var path = _dir.File(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    private IReadOnlyList<RecentMediaFile> Find(int max = 5, DateTimeOffset? now = null) =>
        RecentMedia.Find(_dir.Path, Media, now ?? Now, Month, max, Ct);

    private static string[] Names(IEnumerable<RecentMediaFile> files) => [.. files.Select(f => Path.GetFileName(f.Path))];

    [Fact]
    public void OnlyFilesWithAMediaExtension_AreListed_WhateverTheCaseOfTheExtension()
    {
        Write("talk.wav", TimeSpan.FromHours(1));
        Write("Podcast.MP3", TimeSpan.FromHours(2));
        Write("clip.Mp4", TimeSpan.FromHours(3));
        Write("notes.pdf", TimeSpan.FromHours(4));
        Write("README", TimeSpan.FromHours(5));
        Write("archive.wav.zip", TimeSpan.FromHours(6));

        Assert.Equal(["talk.wav", "Podcast.MP3", "clip.Mp4"], Names(Find()));
    }

    [Fact]
    public void OnlyFilesWrittenInTheWindowBeforeNow_AreListed()
    {
        Write("yesterday.wav", TimeSpan.FromDays(1));
        Write("four-weeks.wav", TimeSpan.FromDays(29));
        Write("edge.wav", Month);
        Write("too-old.wav", Month + TimeSpan.FromMinutes(1));
        Write("future.wav", -TimeSpan.FromHours(1));

        Assert.Equal(["yesterday.wav", "four-weeks.wav", "edge.wav"], Names(Find()));
    }

    [Fact]
    public void TheNewestComeFirst_AndAtMostMax()
    {
        for (var i = 1; i <= 7; i++)
        {
            Write($"file{i}.wav", TimeSpan.FromDays(i));
        }

        Assert.Equal(["file1.wav", "file2.wav", "file3.wav", "file4.wav", "file5.wav"], Names(Find()));
        Assert.Equal(["file1.wav", "file2.wav"], Names(Find(max: 2)));
        Assert.Empty(Find(max: 0));
    }

    [Fact]
    public void FilesWrittenAtTheSameTime_AreOrderedByPath()
    {
        Write("b.wav", TimeSpan.FromHours(1));
        Write("a.wav", TimeSpan.FromHours(1));
        Write("c.wav", TimeSpan.FromHours(1));

        Assert.Equal(["a.wav", "b.wav", "c.wav"], Names(Find()));
    }

    [Fact]
    public void AnEntry_HasTheFullPath_TheSize_AndTheLastWriteTime()
    {
        var path = Write("memo.wav", TimeSpan.FromDays(2), bytes: 1234);

        var entry = Assert.Single(Find());

        Assert.Equal(Path.GetFullPath(path), entry.Path);
        Assert.Equal(1234, entry.SizeBytes);
        Assert.Equal(Now - TimeSpan.FromDays(2), entry.LastWriteTime);
    }

    [Fact]
    public void SubFolders_AreNotSearched_AndAFolderNamedLikeAFile_IsNoFile()
    {
        Write("top.wav", TimeSpan.FromHours(1));
        Write(Path.Combine("sub", "nested.wav"), TimeSpan.FromMinutes(1));
        Directory.CreateDirectory(_dir.File("album.mp3"));

        Assert.Equal(["top.wav"], Names(Find()));
    }

    [Fact]
    public void AMissingFolder_OrAFile_GivesNothing()
    {
        var file = Write("talk.wav", TimeSpan.FromHours(1));

        Assert.Empty(RecentMedia.Find(_dir.File("missing"), Media, Now, Month, 5, Ct));
        Assert.Empty(RecentMedia.Find(file, Media, Now, Month, 5, Ct));
        Assert.Empty(RecentMedia.Find(string.Empty, Media, Now, Month, 5, Ct));
        Assert.Empty(RecentMedia.Find(_dir.Path, new HashSet<string>(), Now, Month, 5, Ct));
    }

    [Fact]
    public void AFileWhoseDetailsCannotBeRead_IsSkipped_TheOthersAreListed()
    {
        Write("good.wav", TimeSpan.FromHours(2));
        var target = Write("target.bin", TimeSpan.FromHours(1), bytes: 77);
        try
        {
            // A link whose target is gone has no size or time to show; a link to a file counts as that file.
            File.CreateSymbolicLink(_dir.File("gone.wav"), _dir.File("deleted.wav"));
            File.CreateSymbolicLink(_dir.File("linked.mp3"), target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This system does not let the tests create symbolic links: " + ex.Message);
        }

        var found = Find();

        Assert.Equal(["linked.mp3", "good.wav"], Names(found));
        Assert.Equal(77, found[0].SizeBytes);
    }

    [Fact]
    public void ACancelledSearch_Throws()
    {
        Write("talk.wav", TimeSpan.FromHours(1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => RecentMedia.Find(_dir.Path, Media, Now, Month, 5, cancelled.Token));
    }

    // The Downloads folder --------------------------------------------------------------------------------------

    [Fact]
    public void HomeDownloads_IsTheDownloadsFolderOfTheHome_WhenItExists()
    {
        var downloads = Directory.CreateDirectory(_dir.File("Downloads")).FullName;

        Assert.Equal(downloads, new HomeDownloadsFolder(_dir.Path).Path);
    }

    [Fact]
    public void HomeDownloads_IsNull_WhenTheFolderIsMissing_OrThereIsNoHome()
    {
        Assert.Null(new HomeDownloadsFolder(_dir.Path).Path);
        Assert.Null(new HomeDownloadsFolder(_dir.File("nobody")).Path);
        Assert.Null(new HomeDownloadsFolder(null).Path);
        Assert.Null(new HomeDownloadsFolder("  ").Path);
    }

    [Fact]
    public void HomeDownloads_NoticesAFolderCreatedLater()
    {
        var folder = new HomeDownloadsFolder(_dir.Path);
        Assert.Null(folder.Path);

        Directory.CreateDirectory(_dir.File("Downloads"));

        Assert.Equal(Path.GetFullPath(_dir.File("Downloads")), folder.Path);
    }

    [Fact]
    public void HomeDownloads_OfTheUser_IsUnderTheProfileFolder_OrNull()
    {
        var path = new HomeDownloadsFolder().Path;

        if (path is not null)
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), path);
            Assert.True(Directory.Exists(path));
        }
    }

    // The spoken languages --------------------------------------------------------------------------------------

    [Fact]
    public void SpokenLanguages_AreTheTwentyFiveOfParakeet_WithUniqueCodes()
    {
        var codes = SpokenLanguages.All.Select(l => l.Code).ToArray();

        Assert.Equal(25, codes.Length);
        Assert.Equal(25, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ["bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hr", "hu", "it", "lt", "lv", "mt", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "uk"],
            codes);
        Assert.All(SpokenLanguages.All, l => Assert.False(string.IsNullOrWhiteSpace(l.NativeName)));
    }

    [Fact]
    public void SpokenLanguages_AreNamedInTheirOwnLanguage()
    {
        string Name(string code) => SpokenLanguages.All.Single(l => l.Code == code).NativeName;

        Assert.Equal("English", Name("en"));
        Assert.Equal("Deutsch", Name("de"));
        Assert.Equal("Français", Name("fr"));
        Assert.Equal("Ελληνικά", Name("el"));
        Assert.Equal("Українська", Name("uk"));
        Assert.Equal("Русский", Name("ru"));
        Assert.Equal("Български", Name("bg"));
    }
}
