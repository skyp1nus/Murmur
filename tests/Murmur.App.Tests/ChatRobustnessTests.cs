using Murmur.App.Localization;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>Ask AI under stress: answers in flight when the user moves on, stores that fail or answer late, citations without markers.</summary>
public sealed class ChatRobustnessTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeAssistant _assistant = new();
    private readonly FakeChatStore _store = new();
    private readonly FakeSessionStore _sessions = new();
    private readonly FakeRecorder _recorder = new();
    private readonly SessionRecord _demo = Sessions.Make("Client demo", Today, TimeSpan.FromMinutes(47));

    public ChatRobustnessTests() => _sessions.Add(_demo);

    private ChatViewModel CreateChat(SessionId? scope) =>
        new(_assistant, new FakeLlm(), _recorder, scope, "/models", action => action(), _store, _sessions);

    private LibraryChatViewModel CreatePage() => new(CreateChat, _store, _sessions);

    private static void Ask(ChatViewModel chat, string question)
    {
        chat.Question = question;
        chat.AskCommand.Execute(null);
    }

    private async Task Finish(LibraryChatViewModel page, ChatViewModel chat, string answer)
    {
        _assistant.Answer([answer]);
        await chat.AskTask;
        await page.ThreadsTask;
    }

    // ---- an answer in flight is never cancelled by leaving it -----------------------------------------------------------------

    [Fact]
    public async Task AskAi_DuringAStream_KeepsTheExchange_AndItJoinsTheList()
    {
        var page = CreatePage();
        var streaming = page.Chat;
        Ask(streaming, "Long question");

        page.StartNew();

        Assert.NotSame(streaming, page.Chat);
        Assert.True(page.Chat.IsEmpty);
        Assert.True(streaming.IsGenerating, "it was detached, not cancelled");
        await Finish(page, streaming, "Long answer");

        Assert.Equal(["Long question", "Long answer"], _store.MessagesOf(Assert.Single(_store.Threads).Id).Select(m => m.Text));
        Assert.Equal(["Long question"], page.Entries.Select(e => e.Title));
        Assert.False(page.Entries[0].IsActive, "the chat in view is the new empty one");
    }

    [Fact]
    public async Task AnotherRow_DuringAStream_KeepsTheExchange()
    {
        var other = await _store.CreateThreadAsync("Other chat", null, CancellationToken.None);
        await _store.AppendMessageAsync(new ChatMessage(other.Id, 0, TurnRole.User, "q", [], Today), CancellationToken.None);
        await _store.AppendMessageAsync(new ChatMessage(other.Id, 1, TurnRole.Assistant, "a", [], Today), CancellationToken.None);
        var page = CreatePage();
        await page.ThreadsTask;
        var streaming = page.Chat;
        Ask(streaming, "Question in flight");

        page.Entries[0].OpenCommand.Execute(null);
        await page.ActionTask;

        Assert.Equal(other.Id, page.Chat.Thread!.Id);
        await Finish(page, streaming, "Answer");
        Assert.Equal(2, _store.Threads.Count);
        Assert.Contains(page.Entries, e => e.Title == "Question in flight");
        Assert.Equal(["q", "a"], _store.MessagesOf(other.Id).Select(m => m.Text));
    }

    [Fact]
    public async Task APill_DuringAStream_KeepsTheExchange()
    {
        var page = CreatePage();
        page.StartNew(_demo);
        var streaming = page.Chat;
        Ask(streaming, "Library question");

        page.ScopePills.PickSessionCommand.Execute(null);

        Assert.Equal(_demo.Id, page.Chat.Scope);
        await Finish(page, streaming, "Answer");
        Assert.Null(Assert.Single(_store.Threads).Scope);
        Assert.Equal(["Library question"], page.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task TheLibraryQuestion_DuringAStream_DoesNotCancelIt()
    {
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        var shell = rig.Shell;
        shell.ShowNewChat();
        var page = shell.LibraryChat!;
        var streaming = page.Chat;
        Ask(streaming, "First, still streaming");
        shell.ShowLibrary();

        shell.AskLibrary("Second question");
        var second = page.Chat;

        Assert.NotSame(streaming, second);
        Assert.True(streaming.IsGenerating, "it was detached, not cancelled");
        Assert.False(rig.Assistant.AnswerCancelled);
        rig.Assistant.Answer(["Second answer"]);
        await second.AskTask;
        await page.ThreadsTask;
        Assert.Equal(["Second question"], rig.ChatStore.Threads.Select(t => t.Title));

        // Leaving the page for good (the window closes) is what cancels it.
        page.Dispose();
        await streaming.AskTask;
        Assert.False(streaming.IsGenerating);
    }

    [Fact]
    public async Task ReopeningAThreadThatStillStreams_ShowsTheLiveChat_NotACopy()
    {
        var page = CreatePage();
        var chat = page.Chat;
        Ask(chat, "Q1");
        await Finish(page, chat, "A1");
        Ask(chat, "Q2");
        var thread = chat.Thread!;

        page.StartNew();
        Assert.NotSame(chat, page.Chat);
        page.Entries[0].OpenCommand.Execute(null);
        await page.ActionTask;

        Assert.Same(chat, page.Chat);
        Assert.True(page.Chat.IsGenerating);
        await Finish(page, chat, "A2");
        Assert.Equal(["Q1", "A1", "Q2", "A2"], _store.MessagesOf(thread.Id).Select(m => m.Text));
        Assert.Equal(["Q1", "A1", "Q2", "A2"], page.Chat.Messages.Select(m => m.Text));
    }

    // ---- the list of chats ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASlowOlderListing_DoesNotOverwriteANewerOne()
    {
        var page = CreatePage();
        await page.ThreadsTask;
        var old = await _store.CreateThreadAsync("Old", null, CancellationToken.None);
        var gate = new TaskCompletionSource();
        _store.ListGate = gate.Task;
        var slow = page.RefreshThreadsAsync();

        await _store.DeleteThreadAsync(old.Id, CancellationToken.None);
        await page.RefreshThreadsAsync();
        Assert.Empty(page.Entries);

        gate.SetResult();
        await slow;

        Assert.Empty(page.Entries);
    }

    [Fact]
    public async Task ARenameInProgress_SurvivesARefresh()
    {
        var page = CreatePage();
        var thread = await _store.CreateThreadAsync("Chat", null, CancellationToken.None);
        await page.RefreshThreadsAsync();
        var row = page.Entries[0];
        row.BeginRenameCommand.Execute(null);
        row.EditText = "Half typed";

        await _store.CreateThreadAsync("Another", null, CancellationToken.None);
        await page.RefreshThreadsAsync();

        Assert.Same(row, page.Entries[0]);
        Assert.True(row.IsEditing);
        Assert.Equal("Half typed", row.EditText);

        row.CancelRenameCommand.Execute(null);
        Assert.Equal(["Another", "Chat"], page.Entries.Select(e => e.Title));
        Assert.Equal(thread.Id, _store.Threads[1].Id);
    }

    [Fact]
    public async Task AChatThatCannotBeOpened_SaysSo()
    {
        using var culture = UiCulture.Override("en");
        var thread = await _store.CreateThreadAsync("Chat", null, CancellationToken.None);
        var page = CreatePage();
        await page.ThreadsTask;
        _store.Failure = new IOException("locked");

        page.Entries[0].OpenCommand.Execute(null);
        await page.ActionTask;

        Assert.True(page.HasError);
        Assert.Equal("Couldn’t open this chat.", page.Error);
        Assert.NotEqual(thread.Id, page.Chat.Thread?.Id);

        _store.Failure = null;
        page.StartNew();
        Assert.False(page.HasError);
    }

    // ---- a store that fails ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeletedThread_IsNotAppendedTo_AndTheExchangeSaysItIsNotSaved()
    {
        using var culture = UiCulture.Override("en");
        var page = CreatePage();
        var chat = page.Chat;
        Ask(chat, "Q1");
        await Finish(page, chat, "A1");
        var first = chat.Thread!;

        // The thread vanishes behind the chat's back (a cascade from a deleted recording).
        await _store.DeleteThreadAsync(first.Id, CancellationToken.None);
        Ask(chat, "Q2");
        await Finish(page, chat, "A2");

        Assert.Null(chat.Thread);
        Assert.True(chat.Messages[3].HasSaveNote);
        Assert.Equal("This exchange could not be saved.", chat.Messages[3].SaveNote);

        // The next exchange starts a new thread instead of failing again.
        Ask(chat, "Q3");
        await Finish(page, chat, "A3");
        Assert.NotNull(chat.Thread);
        Assert.NotEqual(first.Id, chat.Thread!.Id);
        Assert.Equal(["Q3", "A3"], _store.MessagesOf(chat.Thread.Id).Select(m => m.Text));
        Assert.False(chat.Messages[5].HasSaveNote);
    }

    [Fact]
    public async Task AScopedChatWhoseRecordingWasDeleted_SaysItIsNoLongerSaved()
    {
        using var culture = UiCulture.Override("en");
        _store.KnownSessions = [];
        var page = CreatePage();
        page.StartNew(_demo);
        page.ScopePills.PickSessionCommand.Execute(null);
        var chat = page.Chat;

        Ask(chat, "About the demo");
        await Finish(page, chat, "Answer");

        Assert.Equal("The recording of this chat was deleted, so it is no longer saved.", chat.Messages[1].SaveNote);
        Assert.Empty(_store.Threads);
    }

    [Fact]
    public async Task AFailingStore_SaysItIsNotSaved_AndKeepsTheThread()
    {
        using var culture = UiCulture.Override("en");
        var page = CreatePage();
        var chat = page.Chat;
        _store.Failure = new IOException("disk full");
        Ask(chat, "Q");
        await Finish(page, chat, "A");

        Assert.Equal("This exchange could not be saved.", chat.Messages[1].SaveNote);
        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public void ATitleIsNeverCutInsideASurrogatePair()
    {
        var question = new string('a', 58) + "😀" + new string('b', 20);

        var title = ChatViewModel.TitleFor(question);

        Assert.Equal(new string('a', 58) + "…", title);
        Assert.DoesNotContain(title, c => char.IsSurrogate(c));
    }

    // ---- citations without a marker; a deleted recording ------------------------------------------------------------------------

    [Fact]
    public async Task ASingleCitationWithoutAMarkerInTheText_StillShowsItsSourceCard()
    {
        var page = CreatePage();
        var chat = page.Chat;
        Ask(chat, "Q");
        _assistant.Answer(["An answer with no time marker."], Hits.Cite(_demo.Id, 30, "“quote”"));
        await chat.AskTask;

        var answer = chat.Messages[1];
        Assert.True(answer.HasSources);
        Assert.Equal("Client demo", Assert.Single(answer.Sources).Title);

        Ask(chat, "Q2");
        _assistant.Answer(["With a chip [00:45]."], Hits.Cite(_demo.Id, 45));
        await chat.AskTask;
        Assert.False(chat.Messages[3].HasSources, "its chip in the line is the whole source");
    }

    [Fact]
    public async Task ADeletedRecording_ExplainsItselfInTheTooltipOfItsChipAndCard()
    {
        using var culture = UiCulture.Override("en");
        var gone = Sessions.Make("Gone", Today, TimeSpan.FromMinutes(1));
        var page = CreatePage();
        var chat = page.Chat;
        Ask(chat, "Q");
        _assistant.Answer(["See [00:05]."], Hits.Cite(gone.Id, 5, "“old”"));
        await chat.AskTask;

        var answer = chat.Messages[1];
        Assert.False(answer.Citations[0].IsLink);
        Assert.Equal("Deleted recording", answer.Citations[0].Tooltip);
        Assert.Equal("Deleted recording", answer.Sources[0].Tooltip);
    }

    // ---- opening the page ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OpeningAskAi_ChecksTheModelsAgain_AndRefreshesTheModelChip()
    {
        using var culture = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        rig.Llm.Status = LlmStatus.NotInstalled;
        rig.ChatModelName = null;
        var shell = rig.Shell;
        shell.ShowLibraryChat();
        var page = shell.LibraryChat!;
        Assert.Equal(ChatAvailability.NotInstalled, page.Chat.Availability);
        Assert.Equal("Gemma 4 · local", page.ModelChip!.Text);

        // Gemma was installed, and chosen, in the Settings meanwhile.
        rig.Llm.Status = LlmStatus.Unloaded;
        rig.ChatModelName = "Gemma 4 26B A4B";
        shell.ShowSettings();
        shell.ShowLibraryChat();

        Assert.Equal(ChatAvailability.Ready, page.Chat.Availability);
        Assert.Equal("Gemma 4 26B A4B · local", page.ModelChip.Text);

        rig.Llm.Status = LlmStatus.NotInstalled;
        rig.ChatModelName = null;
        shell.ShowNewChat();
        Assert.Equal(ChatAvailability.NotInstalled, page.Chat.Availability);
        Assert.Equal("Gemma 4 · local", page.ModelChip.Text);
    }
}
