using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Intelligence;

namespace Murmur.App.Tests;

/// <summary>The Ask AI tab of a recording is the Ask AI conversation pinned to it, and the "…" menu leads to the full page.</summary>
public sealed class SessionAskAiTests
{
    [Fact]
    public async Task TheTab_ContinuesTheLatestSavedChatOfTheRecording()
    {
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        var older = await rig.ChatStore.CreateThreadAsync("Older", rig.ClientDemo.Id, CancellationToken.None);
        var latest = await rig.ChatStore.CreateThreadAsync("Latest", rig.ClientDemo.Id, CancellationToken.None);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(latest.Id, 0, TurnRole.User, "What was decided?", [], UiHarness.Now), CancellationToken.None);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(latest.Id, 1, TurnRole.Assistant, "PDF export moved.", [], UiHarness.Now), CancellationToken.None);
        var otherRecording = await rig.ChatStore.CreateThreadAsync("Other recording", rig.SprintRetro.Id, CancellationToken.None);

        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var chat = rig.Shell.Session!.Chat;
        await DesignRig.UntilAsync(() => chat.Messages.Count == 2, "the saved chat of the recording");

        Assert.Equal(["What was decided?", "PDF export moved."], chat.Messages.Select(m => m.Text));
        Assert.Equal(latest.Id, chat.Thread!.Id);
        Assert.Equal(rig.ClientDemo.Id, chat.Scope);
        Assert.NotEqual(older.Id, chat.Thread.Id);
        Assert.NotEqual(otherRecording.Id, chat.Thread.Id);
    }

    [Fact]
    public async Task AQuestionInTheTab_IsSavedAsAChatScopedToTheRecording()
    {
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var chat = rig.Shell.Session!.Chat;

        chat.Question = "Who sends the invite?";
        chat.AskCommand.Execute(null);
        rig.Assistant.Answer(["You do."]);
        await chat.AskTask;

        var thread = Assert.Single(rig.ChatStore.Threads);
        Assert.Equal(rig.ClientDemo.Id, thread.Scope);
        Assert.Equal("Who sends the invite?", thread.Title);
    }

    [Fact]
    public async Task OpenInAskAi_OpensTheFullPageOnANewChatScopedToTheRecording()
    {
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;

        Assert.True(rig.Shell.Session!.OpenInAskAiCommand.CanExecute(null));
        rig.Shell.Session.OpenInAskAiCommand.Execute(null);

        var page = Assert.IsType<LibraryChatViewModel>(rig.Shell.CurrentPage);
        Assert.Null(rig.Shell.Session);
        Assert.Equal(rig.ClientDemo.Id, page.Chat.Scope);
        Assert.True(page.ScopePills.HasSession);
        Assert.True(page.ScopePills.IsSession);
        Assert.Equal(rig.ClientDemo.Title, page.ScopePills.SessionTitle);
    }
}
