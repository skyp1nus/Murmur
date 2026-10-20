using System.Text;
using Murmur.Core.Settings;

namespace Murmur.Storage.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void NoFile_UsesTheDefaults()
    {
        using var root = new TempDataRoot();

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(new AppSettings(), store.Current);
        Assert.Equal(Path.Combine(Path.GetFullPath(root.Path), "settings.json"), store.FilePath);
        Assert.False(File.Exists(root.SettingsFile));
    }

    [Fact]
    public async Task Save_ThenReopen_RestoresEveryValue()
    {
        using var root = new TempDataRoot();
        var saved = new AppSettings
        {
            Theme = ThemePreference.Dark,
            Language = "uk",
            OnboardingCompleted = true,
            ScreenByDefault = true,
            MicrophoneDeviceId = "mic-1",
            SystemAudioDeviceId = "out-2",
            TranscriptionLanguage = "de",
            RecordHotkey = null,
            RecordingsDirectory = "/data/rec",
            ChatModelId = "gemma-4-e2b",
        };

        var first = new JsonSettingsStore(new StoragePaths(root.Path));
        await first.SaveAsync(saved, TestContext.Current.CancellationToken);
        var second = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(saved, first.Current);
        Assert.Equal(saved, second.Current);
    }

    [Fact]
    public async Task Save_WritesCamelCaseNamesAndEnumsAsStrings()
    {
        using var root = new TempDataRoot();
        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(root.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("\"theme\": \"Dark\"", text, StringComparison.Ordinal);
        Assert.Contains("\"onboardingCompleted\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"recordHotkey\": \"Ctrl+Shift+R\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingProperties_TakeTheRecordDefaults_UnknownOnesAreIgnored()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, """{ "theme": "Light", "futureSetting": { "a": [1, 2] }, "Language": "en" }""");

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(new AppSettings { Theme = ThemePreference.Light, Language = "en" }, store.Current);
        Assert.Equal("Ctrl+Shift+R", store.Current.RecordHotkey);
        Assert.False(File.Exists(root.SettingsFile + ".bad"));
    }

    [Fact]
    public void NoFile_DefaultsToEnglish_AtTheCurrentSettingsVersion()
    {
        using var root = new TempDataRoot();

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal("en", store.Current.Language);
        Assert.Equal(2, store.Current.SettingsVersion);
    }

    [Theory]
    [InlineData("""{ "theme": "Dark", "language": "uk", "onboardingCompleted": true }""")]
    [InlineData("""{ "Language": "uk" }""")]
    [InlineData("""{ "theme": "Dark", "language": "en", "onboardingCompleted": true }""")]
    [InlineData("""{ "settingsVersion": 1, "language": "uk" }""")]
    public void AFileFromBeforeDecisionD039_IsLoadedInEnglishAtVersion2_ButKeepsEverythingElse(string json)
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, json);

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal("en", store.Current.Language);
        Assert.Equal(2, store.Current.SettingsVersion);
        Assert.Equal(json.Contains("\"Dark\"", StringComparison.Ordinal) ? ThemePreference.Dark : ThemePreference.System, store.Current.Theme);
        Assert.Equal(json.Contains("onboardingCompleted", StringComparison.Ordinal), store.Current.OnboardingCompleted);
    }

    [Fact]
    public async Task AMigratedFile_IsRewrittenWithVersion2OnTheNextSave_AndTheChoiceThenSticks()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, """{ "theme": "Light", "language": "uk" }""");
        var migrated = new JsonSettingsStore(new StoragePaths(root.Path));
        Assert.Equal("en", migrated.Current.Language);
        // Nothing is written until something saves.
        Assert.DoesNotContain("settingsVersion", File.ReadAllText(root.SettingsFile), StringComparison.Ordinal);

        await migrated.SaveAsync(migrated.Current with { OnboardingCompleted = true }, TestContext.Current.CancellationToken);

        Assert.Contains("\"settingsVersion\": 2", File.ReadAllText(root.SettingsFile), StringComparison.Ordinal);
        Assert.Contains("\"language\": \"en\"", File.ReadAllText(root.SettingsFile), StringComparison.Ordinal);
        var reopened = new JsonSettingsStore(new StoragePaths(root.Path));
        Assert.Equal("en", reopened.Current.Language);
        Assert.True(reopened.Current.OnboardingCompleted);

        // The user now picks Ukrainian: that is a choice and survives restarts.
        await reopened.SaveAsync(reopened.Current with { Language = "uk" }, TestContext.Current.CancellationToken);
        Assert.Equal("uk", new JsonSettingsStore(new StoragePaths(root.Path)).Current.Language);
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("en")]
    public void AFileAtVersion2_KeepsTheLanguageTheUserChose(string language)
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, "{ \"settingsVersion\": 2, \"language\": \"" + language + "\", \"theme\": \"Dark\" }");

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(language, store.Current.Language);
        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        Assert.Equal(2, store.Current.SettingsVersion);
    }

    [Fact]
    public void AFileFromANewerVersion_IsNotMigrated()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, """{ "settingsVersion": 3, "language": "uk" }""");

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal("uk", store.Current.Language);
        Assert.Equal(3, store.Current.SettingsVersion);
    }

    [Fact]
    public void AFileWithoutAVersion_ButWithBom_IsMigratedToo()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllBytes(root.SettingsFile, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("""{ "language": "uk" }""")]);

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal("en", store.Current.Language);
        Assert.False(File.Exists(root.SettingsFile + ".bad"));
    }

    [Fact]
    public void ExplicitNull_OverridesANonNullDefault()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, """{ "recordHotkey": null }""");

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Null(store.Current.RecordHotkey);
    }

    [Fact]
    public void FileWithUtf8Bom_IsRead()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllBytes(root.SettingsFile, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("""{ "theme": "Dark" }""")]);

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        Assert.False(File.Exists(root.SettingsFile + ".bad"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "theme": "Purple" }""")]
    [InlineData("""{ "onboardingCompleted": "yes" }""")]
    public void CorruptFile_IsRenamedToBad_AndDefaultsAreUsed(string content)
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, content);

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(new AppSettings(), store.Current);
        Assert.False(File.Exists(root.SettingsFile));
        Assert.Equal(content, File.ReadAllText(root.SettingsFile + ".bad"));
    }

    [Fact]
    public async Task CorruptFile_IsReplacedByTheNextSave_AndTheBadCopyStays()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, "garbage");
        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        await store.SaveAsync(new AppSettings { Language = "uk" }, TestContext.Current.CancellationToken);

        Assert.Equal("uk", new JsonSettingsStore(new StoragePaths(root.Path)).Current.Language);
        Assert.Equal("garbage", File.ReadAllText(root.SettingsFile + ".bad"));
    }

    [Fact]
    public async Task Save_RaisesChangedAfterTheFileIsWritten()
    {
        using var root = new TempDataRoot();
        var store = new JsonSettingsStore(new StoragePaths(root.Path));
        var seen = new List<(AppSettings Settings, AppSettings Current, string OnDisk)>();
        store.Changed += settings => seen.Add((settings, store.Current, File.ReadAllText(root.SettingsFile)));

        var next = new AppSettings { Theme = ThemePreference.Light };
        await store.SaveAsync(next, TestContext.Current.CancellationToken);

        var change = Assert.Single(seen);
        Assert.Equal(next, change.Settings);
        Assert.Equal(next, change.Current);
        Assert.Contains("\"Light\"", change.OnDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSave_KeepsCurrentAndDoesNotRaiseChanged()
    {
        using var root = new TempDataRoot();
        var blocker = Path.Combine(root.Path, "blocker");
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(blocker, "a file where the settings folder should be");
        var store = new JsonSettingsStore(Path.Combine(blocker, "settings.json"));
        var raised = false;
        store.Changed += _ => raised = true;

        await Assert.ThrowsAnyAsync<IOException>(
            async () => await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, TestContext.Current.CancellationToken));

        Assert.False(raised);
        Assert.Equal(new AppSettings(), store.Current);
    }

    [Fact]
    public async Task Save_CreatesTheFolder_AndLeavesNoTempFiles()
    {
        using var root = new TempDataRoot();
        var nested = Path.Combine(root.Path, "a", "b");
        var store = new JsonSettingsStore(new StoragePaths(nested));

        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, TestContext.Current.CancellationToken);
        await store.SaveAsync(new AppSettings { Theme = ThemePreference.Light }, TestContext.Current.CancellationToken);

        Assert.Equal(["settings.json"], Directory.GetFiles(nested).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task CancelledSave_ChangesNothing()
    {
        using var root = new TempDataRoot();
        var store = new JsonSettingsStore(new StoragePaths(root.Path));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, cts.Token));

        Assert.Equal(new AppSettings(), store.Current);
        Assert.False(File.Exists(root.SettingsFile));
    }

    [Fact]
    public async Task ConcurrentSaves_LeaveAValidFileMatchingCurrent()
    {
        using var root = new TempDataRoot();
        var store = new JsonSettingsStore(new StoragePaths(root.Path));
        var changes = 0;
        store.Changed += _ => Interlocked.Increment(ref changes);

        var saves = Enumerable.Range(0, 40)
            .Select(i => Task.Run(
                async () => await store.SaveAsync(
                    new AppSettings { Theme = (ThemePreference)(i % 3), Language = i % 2 == 0 ? "uk" : "en", ChatModelId = "m" + i },
                    TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(saves);

        Assert.Equal(40, changes);
        Assert.Equal(store.Current, new JsonSettingsStore(new StoragePaths(root.Path)).Current);
        Assert.Equal(["settings.json"], Directory.GetFiles(root.Path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task SaveAsync_NullSettings_Throws()
    {
        using var root = new TempDataRoot();
        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.SaveAsync(null!, TestContext.Current.CancellationToken));
    }
}
