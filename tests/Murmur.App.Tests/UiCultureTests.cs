using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>
/// The process-wide UI language is one object swapped as a whole. These tests change that global state, so they run alone
/// (the <c>ExclusiveUi</c> collection) and put it back after every test.
/// </summary>
[Collection(ExclusiveUiCollection.Name)]
public sealed class UiCultureTests : IDisposable
{
    private readonly string _language = UiCulture.Language;

    public void Dispose() => UiCulture.Apply(_language);

    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("UK-ua", "uk")]
    [InlineData("en", "en")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public void Apply_PublishesTheLanguageAndItsCultureTogether(string? requested, string expected)
    {
        var returned = UiCulture.Apply(requested);

        var (language, culture) = UiCulture.Snapshot;
        Assert.Equal(expected, language);
        Assert.Equal(expected, UiCulture.Language);
        Assert.Same(returned, culture);
        Assert.Same(culture, UiCulture.Culture);
        Assert.Equal(expected, culture.Name);
    }

    [Fact]
    public void Apply_DoesNotTouchTheOsCulture()
    {
        var previous = CultureInfo.CurrentUICulture;

        UiCulture.Apply("uk");

        Assert.Same(previous, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void Apply_SwitchesTheStrings()
    {
        UiCulture.Apply("uk");
        Assert.Equal("Назад", Strings.Common_Back);

        UiCulture.Apply("en");
        Assert.Equal("Back", Strings.Common_Back);
    }

    [Fact]
    public void AScopedOverride_WinsOverTheProcessWideLanguage_AndIsUndone()
    {
        UiCulture.Apply("uk");

        using (UiCulture.Override("en"))
        {
            Assert.Equal(("en", "en"), (UiCulture.Language, UiCulture.Culture.Name));
        }

        Assert.Equal(("uk", "uk"), (UiCulture.Language, UiCulture.Culture.Name));
    }

    [Fact]
    public async Task ConcurrentReaders_NeverSeeALanguageWithTheCultureOfAnotherApply()
    {
        const int Applies = 20_000;
        var failures = 0;
        var done = false;
        string? firstFailure = null;

        void Read()
        {
            while (!Volatile.Read(ref done))
            {
                var (language, culture) = UiCulture.Snapshot;
                if (!string.Equals(language, culture.Name, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref failures);
                    Interlocked.CompareExchange(ref firstFailure, language + "/" + culture.Name, null);
                }
            }
        }

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Factory.StartNew(Read, TaskCreationOptions.LongRunning)).ToArray();
        var writer = Task.Factory.StartNew(
            () =>
            {
                for (var i = 0; i < Applies; i++)
                {
                    UiCulture.Apply((i & 1) == 0 ? "uk" : "en");
                }

                Volatile.Write(ref done, true);
            },
            TaskCreationOptions.LongRunning);

        await writer.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(failures == 0, $"{failures} reads saw a language with the wrong culture, first: {firstFailure}");
    }

    // App.Initialize -> Settings.Language -------------------------------------------------------------------------------

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    [Theory]
    [InlineData("uk", "uk", "Назад")]
    [InlineData("en", "en", "Back")]
    [InlineData("fr", "en", "Back")]
    [InlineData("", "en", "Back")]
    public async Task AppInitialize_AppliesTheLanguageOfTheSettings(string stored, string expectedLanguage, string expectedBack)
    {
        // The process starts in the other language, so a no-op Initialize cannot pass.
        UiCulture.Apply(expectedLanguage == "uk" ? "en" : "uk");
        var settings = new FakeSettingsStore(new AppSettings { Language = stored });
        var services = new AppServices(new FakeRecorder(), new FakeDisplayCapture(), new FakeSessionStore(), new TestTime(UiHarness.Now), Settings: settings);

        var result = await Session.Dispatch(
            () =>
            {
                var current = Application.Current;
                var app = new App([], services);
                try
                {
                    app.Initialize();
                    return (UiCulture.Language, UiCulture.Culture.Name, Strings.Common_Back);
                }
                finally
                {
                    // Only the language is under test: put the shared application back.
                    if (!ReferenceEquals(Application.Current, current))
                    {
                        Assert.Fail("Initialize replaced Application.Current.");
                    }
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((expectedLanguage, expectedLanguage, expectedBack), result);
    }

    [Fact]
    public async Task AppInitialize_WithoutSettings_LeavesTheLanguageAlone()
    {
        UiCulture.Apply("uk");

        var language = await Session.Dispatch(
            () =>
            {
                new App([], null).Initialize();
                return UiCulture.Language;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("uk", language);
    }
}
