using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>
/// A model installed in the Settings while the app runs must be noticed: nothing in the screens keeps "not installed"
/// from an earlier check.
/// </summary>
public sealed class ModelInstalledLaterTests : IDisposable
{
    // The texts asserted below are Ukrainian; English is the default language (D-039).
    private readonly IDisposable _ukrainian = UiCulture.Override("uk");

    public void Dispose() => _ukrainian.Dispose();

    [Fact]
    public async Task Library_AMissingModelBadgeDoesNotOutliveTheInstall()
    {
        var store = new FakeSessionStore();
        var processing = new FakeProcessing { IsAsrInstalled = false };
        var record = Sessions.Make("A", UiHarness.Now.AddHours(-1), TimeSpan.FromMinutes(10), state: SessionState.Recorded);
        store.Add(record);
        processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.ModelMissing, 0, "no model"));
        var vm = new LibraryViewModel(store, new TestTime(UiHarness.Now), processing, action => action());

        await vm.LoadAsync();
        Assert.Equal("Без транскрипту", vm.Sessions[0].StatusText);

        processing.IsAsrInstalled = true;
        await vm.LoadAsync();
        Assert.Null(vm.Sessions[0].StatusText);
    }

    [Fact]
    public async Task Session_TheInstallHintDoesNotOutliveTheInstall()
    {
        using var fixture = new SessionFixture(SessionState.Recorded);
        fixture.Processing.IsAsrInstalled = false;
        fixture.Processing.Set(new SessionProcessingStatus(fixture.Record.Id, SessionProcessingStage.ModelMissing, 0, "no model"));
        using (var first = fixture.Create())
        {
            await first.LoadAsync();
            Assert.Equal(TranscriptStatus.AsrMissing, first.Status);
        }

        fixture.Processing.IsAsrInstalled = true;
        using var second = fixture.Create();
        await second.LoadAsync();

        Assert.Equal(TranscriptStatus.NotTranscribed, second.Status);
    }

    [Fact]
    public void Chat_ChecksAgainOnTheNextUse_AfterTheModelsAppeared()
    {
        var llm = new FakeLlm { Status = Murmur.Core.Intelligence.LlmStatus.NotInstalled };
        var chat = new ChatViewModel(new FakeAssistant(), llm, new FakeRecorder(), null, "/models", action => action());
        Assert.Equal(ChatAvailability.NotInstalled, chat.Availability);

        llm.Status = Murmur.Core.Intelligence.LlmStatus.Unloaded;
        chat.RefreshAvailability();

        Assert.Equal(ChatAvailability.Ready, chat.Availability);
    }
}
