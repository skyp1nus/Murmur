using System.Runtime.InteropServices;
using Murmur.Capture.Windows;
using Murmur.Capture.Windows.Platform;
using Murmur.Core.Import;

namespace Murmur.Capture.Windows.Tests;

/// <summary>The Downloads folder of the import dialog on Windows (#67): the shell's known folder, else the profile's Downloads folder.</summary>
public sealed class KnownFolderDownloadsTests : IDisposable
{
    private const string WindowsOnly = "The known-folder call of the shell runs only on Windows.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "murmur-downloads-" + Guid.NewGuid().ToString("N"));

    public KnownFolderDownloadsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Fallback(string? path) : IDownloadsFolder
    {
        public int Reads { get; private set; }

        public string? Path
        {
            get
            {
                Reads++;
                return path;
            }
        }
    }

    [Fact]
    public void TheKnownFolder_IsTheAnswer_WhenItExists()
    {
        var moved = Directory.CreateDirectory(Path.Combine(_root, "D", "Downloads")).FullName;
        var fallback = new Fallback("C:\\Users\\me\\Downloads");

        var folder = new KnownFolderDownloads(() => moved, fallback);

        Assert.Equal(moved, folder.Path);
        Assert.Equal(0, fallback.Reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public void TheFallback_Answers_WhenTheShellHasNoFolder_OrItDoesNotExist(string? known)
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        var path = known == "missing" ? Path.Combine(_root, "gone") : known;

        Assert.Equal(home, new KnownFolderDownloads(() => path, new Fallback(home)).Path);
    }

    [Fact]
    public void TheFallback_Answers_WhenTheShellCallFails()
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;

        Assert.Equal(home, new KnownFolderDownloads(() => throw new COMException("E_FAIL", unchecked((int)0x80004005)), new Fallback(home)).Path);
        Assert.Equal(home, new KnownFolderDownloads(() => throw new DllNotFoundException("shell32"), new Fallback(home)).Path);
    }

    [Fact]
    public void NoFolderAtAll_IsNull()
    {
        Assert.Null(new KnownFolderDownloads(() => null, new Fallback(null)).Path);
    }

    [Fact]
    public void TheFolderId_IsFolderIdDownloads()
    {
        Assert.Equal(new Guid("374DE290-123F-4565-9164-39C4925E467B"), KnownFolderNativeMethods.DownloadsId);
    }

    [Fact]
    public void TheShell_KnowsTheDownloadsFolder_OfTheUser()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var known = KnownFolderNativeMethods.Downloads();

        Assert.False(string.IsNullOrWhiteSpace(known));
        Assert.True(Path.IsPathFullyQualified(known));
        var path = WindowsCapture.CreateDownloadsFolder().Path;
        Assert.Equal(Directory.Exists(known) ? Path.GetFullPath(known) : new HomeDownloadsFolder().Path, path);
    }
}
