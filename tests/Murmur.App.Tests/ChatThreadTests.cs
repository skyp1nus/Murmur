using Murmur.App.Localization;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Intelligence;
using Murmur.Core.Settings;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>Ask AI with saved chats: the thread lifecycle, Regenerate, Copy, the scope pills, the sidebar list, the model chip.</summary>
public sealed class ChatThreadTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeAssistant _assistant = new();
    private readonly FakeChatStore _store = new();
    private readonly FakeSessionStore _sessions = new();
    private readonly FakeRecorder _recorder = new();
    private readonly SessionRecord _demo;
    private readonly SessionRecord _retro;

    public ChatThreadTests()
    {
        _demo = Sessions.Make("Client demo — Q4 roadmap", Today, TimeSpan.FromMinutes(47));
        _retro = Sessions.Make("Sprint retro", Today.AddHours(2), TimeSpan.FromMinutes(41));
        _sessions.Add(_demo);
        _sessions.Add(_retro);
    }

    private ChatViewModel CreateChat(SessionId? scope) =>
        new(_assistant, new FakeLlm(), _recorder, scope, "/models", action => action(), _store, _sessions);

    private LibraryChatViewModel CreatePage(ChatModelChipViewModel? chip = null) => new(CreateChat, _store, _sessions, chip);

    private static async Task AskAsync(FakeAssistant assistant, ChatViewModel chat, string question, string answer, params Citation[] citations)
    {
        chat.Question = question;
        Assert.True(chat.AskCommand.CanExecute(null));
        chat.AskCommand.Execute(null);
        assistant.Answer([answer], citations);
        await chat.AskTask;
    }

    private static Citation Cite(SessionRecord session, double seconds, string snippet = "…") => Hits.Cite(session.Id, seconds, snippet);

    // ---- thread lifecycle -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstQuestionNamesANewThread_AndEveryExchangeIsSaved()
    {
        var page = CreatePage();
        var chat = page.Chat;

        await AskAsync(_assistant, chat, "  What did we decide about PDF export?  ", "Next sprint [12:52].", Cite(_demo, 772, "“PDF export moves…”"));

        var thread = Assert.Single(_store.Threads);
        Assert.Equal("What did we decide about PDF export?", thread.Title);
        Assert.Null(thread.Scope);
        Assert.Equal(thread.Id, chat.Thread!.Id);
        var saved = _store.MessagesOf(thread.Id);
        Assert.Equal([TurnRole.User, TurnRole.Assistant], saved.Select(m => m.Role));
        Assert.Equal(["What did we decide about PDF export?", "Next sprint [12:52]."], saved.Select(m => m.Text));
        Assert.Equal(_demo.Id, Assert.Single(saved[1].Citations).Session);

        await AskAsync(_assistant, chat, "And who sends the invite?", "You do.");

        Assert.Single(_store.Threads);
        Assert.Equal(4, _store.MessagesOf(thread.Id).Count);
        Assert.Equal([0, 1, 2, 3], _store.MessagesOf(thread.Id).Select(m => m.Index));
    }

    [Fact]
    public void AThreadTitleIsTheFirstLineOfTheQuestion_AtMostSixtyCharacters()
    {
        Assert.Equal("Short question?", ChatViewModel.TitleFor("  Short question?  "));
        Assert.Equal("First line", ChatViewModel.TitleFor("First line\nsecond line"));
        var title = ChatViewModel.TitleFor(new string('a', 100));
        Assert.Equal(ChatViewModel.MaxTitleLength, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSidebarListsTheSavedChats_NewestFirst_AndHighlightsTheOneInView()
    {
        var page = CreatePage();
        var old = await _store.CreateThreadAsync("Older chat", null, CancellationToken.None);
        await page.RefreshThreadsAsync();
        Assert.Equal(["Older chat"], page.Entries.Select(e => e.Title));
        Assert.All(page.Entries, e => Assert.False(e.IsActive));

        await AskAsync(_assistant, page.Chat, "New question", "Answer");

        Assert.Equal(["New question", "Older chat"], page.Entries.Select(e => e.Title));
        Assert.Equal([true, false], page.Entries.Select(e => e.IsActive));
        Assert.Equal(old.Id, _store.Threads[1].Id);
    }

    [Fact]
    public async Task AClickOnASavedChat_ReopensItWithItsHistory_AndTheChipsOfADeletedRecordingAreNoLongerLinks()
    {
        var thread = await _store.CreateThreadAsync("PDF export", null, CancellationToken.None);
        await _store.AppendMessageAsync(new ChatMessage(thread.Id, 0, TurnRole.User, "What did we decide?", [], Today), CancellationToken.None);
        var gone = Sessions.Make("Deleted meeting", Today, TimeSpan.FromMinutes(5));
        await _store.AppendMessageAsync(
            new ChatMessage(thread.Id, 1, TurnRole.Assistant, "Next sprint [12:52]. Also [00:20].", [Cite(_demo, 772, "“moves”"), Cite(gone, 20, "“old”")], Today),
            CancellationToken.None);
        var page = CreatePage();
        await page.ThreadsTask;

        page.Entries[0].OpenCommand.Execute(null);
        await page.ActionTask;

        var chat = page.Chat;
        Assert.Equal(thread.Id, chat.Thread!.Id);
        Assert.Equal(2, chat.Messages.Count);
        var answer = chat.Messages[1];
        Assert.False(answer.IsStreaming);
        Assert.True(page.Entries[0].IsActive);
        var live = answer.Citations[0];
        var dead = answer.Citations[1];
        Assert.True(live.IsLink);
        Assert.False(dead.IsLink, "a citation of a deleted recording stays as text");
        Assert.False(dead.ActivateCommand.CanExecute(null));
        Assert.Equal("00:20", dead.Label);
        Assert.Equal([true, false], answer.Sources.Select(s => s.IsLink));
        Assert.Equal("Client demo — Q4 roadmap", answer.Sources[0].Title);
        var inline = answer.Paragraphs.SelectMany(p => p.Parts).Where(p => p.Kind == AnswerPartKind.Chip).Select(p => p.Chip!).ToArray();
        Assert.Equal([live, dead], inline);

        var opened = new List<Citation>();
        page.CitationActivated += opened.Add;
        live.ActivateCommand.Execute(null);
        Assert.Equal(_demo.Id, Assert.Single(opened).Session);
    }

    [Fact]
    public async Task AQuestionAfterReopeningAChat_AppendsToThatThread()
    {
        var thread = await _store.CreateThreadAsync("Chat", null, CancellationToken.None);
        await _store.AppendMessageAsync(new ChatMessage(thread.Id, 0, TurnRole.User, "q1", [], Today), CancellationToken.None);
        await _store.AppendMessageAsync(new ChatMessage(thread.Id, 1, TurnRole.Assistant, "a1", [], Today), CancellationToken.None);
        var page = CreatePage();
        await page.ThreadsTask;
        page.Entries[0].OpenCommand.Execute(null);
        await page.ActionTask;

        await AskAsync(_assistant, page.Chat, "q2", "a2");

        Assert.Single(_store.Threads);
        Assert.Equal(["q1", "a1", "q2", "a2"], _store.MessagesOf(thread.Id).Select(m => m.Text));
        Assert.Equal(["q1", "a1"], _assistant.Asks[0].History!.Select(t => t.Text));
    }

    [Fact]
    public async Task RenamingAndDeletingAChat_FromItsRow()
    {
        var page = CreatePage();
        await AskAsync(_assistant, page.Chat, "First question", "Answer");
        var row = page.Entries[0];
        Assert.True(row.CanManage);

        row.BeginRenameCommand.Execute(null);
        Assert.True(row.IsEditing);
        row.EditText = "  PDF export  ";
        row.CommitRenameCommand.Execute(null);
        await page.ActionTask;

        Assert.False(row.IsEditing);
        Assert.Equal("PDF export", _store.Threads[0].Title);
        Assert.Equal(["PDF export"], page.Entries.Select(e => e.Title));
        Assert.True(page.Entries[0].IsActive);

        page.Entries[0].BeginRenameCommand.Execute(null);
        page.Entries[0].EditText = "Dropped";
        page.Entries[0].CancelRenameCommand.Execute(null);
        Assert.Equal("PDF export", _store.Threads[0].Title);

        var open = page.Chat;
        page.Entries[0].DeleteCommand.Execute(null);
        await page.ActionTask;

        Assert.Empty(_store.Threads);
        Assert.Empty(page.Entries);
        Assert.NotSame(open, page.Chat);
        Assert.True(page.Chat.IsEmpty);
        Assert.Null(page.Chat.Thread);
    }

    [Fact]
    public async Task ABlankRename_KeepsTheTitle()
    {
        var page = CreatePage();
        await AskAsync(_assistant, page.Chat, "Question", "Answer");

        page.Entries[0].BeginRenameCommand.Execute(null);
        page.Entries[0].EditText = "   ";
        page.Entries[0].CommitRenameCommand.Execute(null);

        Assert.Equal("Question", _store.Threads[0].Title);
    }

    [Fact]
    public async Task ADeletedRecordingTakesItsScopedChatWithIt_WhenTheListIsRefreshed()
    {
        var page = CreatePage();
        page.StartNew(_demo);
        page.ScopePills.PickSessionCommand.Execute(null);
        await AskAsync(_assistant, page.Chat, "About the demo", "Answer");
        Assert.Equal(_demo.Id, _store.Threads[0].Scope);

        _store.DeleteSession(_demo.Id);
        await page.RefreshThreadsAsync();

        Assert.Empty(page.Entries);
        Assert.True(page.Chat.IsEmpty);
        Assert.False(page.ScopePills.HasSession);
    }

    [Fact]
    public async Task AFailingStore_DoesNotBreakTheChat()
    {
        _store.Failure = new IOException("database is locked");
        var page = CreatePage();

        await AskAsync(_assistant, page.Chat, "Question", "Answer");

        Assert.Equal(2, page.Chat.Messages.Count);
        Assert.True(page.Chat.Messages[1].Succeeded());
        Assert.Null(page.Chat.Thread);
        Assert.False(page.Chat.Messages[1].HasStatus);
    }

    // ---- regenerate, copy ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Regenerate_AsksTheLastQuestionAgain_ReplacesTheLastAnswer_AndTheSavedOne()
    {
        var page = CreatePage();
        var chat = page.Chat;
        await AskAsync(_assistant, chat, "Q1", "A1");
        await AskAsync(_assistant, chat, "Q2", "First try");
        var user = chat.Messages[2];
        Assert.True(chat.Messages[3].IsLastAnswer);
        Assert.False(chat.Messages[1].IsLastAnswer);
        Assert.True(chat.RegenerateCommand.CanExecute(null));

        var old = chat.Messages[3];
        chat.RegenerateCommand.Execute(null);
        Assert.Equal(4, chat.Messages.Count);
        Assert.NotSame(old, chat.Messages[3]);
        Assert.True(chat.Messages[3].IsStreaming);
        _assistant.Answer(["Second try"], Cite(_demo, 5));
        await chat.AskTask;

        Assert.Equal(4, chat.Messages.Count);
        Assert.Same(user, chat.Messages[2]);
        Assert.Equal("Second try", chat.Messages[3].Text);
        Assert.Equal(["Q1", "A1", "Q2", "Second try"], _store.MessagesOf(chat.Thread!.Id).Select(m => m.Text));
        var request = _assistant.Asks[^1];
        Assert.Equal("Q2", request.Question);
        Assert.Equal(["Q1", "A1"], request.History!.Select(t => t.Text));
        Assert.Equal(_demo.Id, Assert.Single(_store.MessagesOf(chat.Thread!.Id)[3].Citations).Session);
    }

    [Fact]
    public async Task Regenerate_AfterACancelledAnswer_SavesTheExchangeForTheFirstTime()
    {
        var page = CreatePage();
        var chat = page.Chat;
        chat.Question = "Question";
        chat.AskCommand.Execute(null);
        chat.CancelCommand.Execute(null);
        await chat.AskTask;
        Assert.Empty(_store.Threads);
        Assert.True(chat.Messages[1].ShowRegenerate);

        chat.RegenerateCommand.Execute(null);
        _assistant.Answer(["Answer"]);
        await chat.AskTask;

        Assert.Equal(["Question", "Answer"], _store.MessagesOf(Assert.Single(_store.Threads).Id).Select(m => m.Text));
        Assert.Equal(2, chat.Messages.Count);
    }

    [Fact]
    public async Task Regenerate_IsOnlyForTheLastAnswer_AndNotWhileGenerating()
    {
        var page = CreatePage();
        var chat = page.Chat;
        Assert.False(chat.RegenerateCommand.CanExecute(null));
        await AskAsync(_assistant, chat, "Q1", "A1");
        Assert.True(chat.RegenerateCommand.CanExecute(null));

        chat.Question = "Q2";
        chat.AskCommand.Execute(null);

        Assert.False(chat.RegenerateCommand.CanExecute(null));
        Assert.False(chat.Messages[1].ShowRegenerate);
        _assistant.Answer(["A2"]);
        await chat.AskTask;
        Assert.False(chat.Messages[1].ShowRegenerate);
        Assert.True(chat.Messages[3].ShowRegenerate);
    }

    [Fact]
    public async Task CopyAnswer_RaisesTheTextOfThatAnswer()
    {
        var page = CreatePage();
        var chat = page.Chat;
        await AskAsync(_assistant, chat, "Q1", "A1 [00:05]", Cite(_demo, 5));
        var copied = new List<string>();
        chat.CopyRequested += copied.Add;

        Assert.True(chat.Messages[1].ShowCopy);
        chat.Messages[1].CopyCommand!.Execute(null);
        chat.CopyAnswerCommand.Execute(chat.Messages[0]);

        Assert.Equal(["A1 [00:05]"], copied);
        Assert.False(chat.Messages[0].ShowCopy);
    }

    // ---- scope pills, new chat ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheScopePills_WholeLibraryAlone_UntilTheChatIsOpenedFromARecording()
    {
        using var culture = UiCulture.Override("en");
        var page = CreatePage();
        Assert.Equal("Whole library", page.ScopePills.LibraryText);
        Assert.True(page.ScopePills.IsLibrary);
        Assert.False(page.ScopePills.HasSession);

        page.StartNew(_demo);

        Assert.True(page.ScopePills.HasSession);
        Assert.Equal("Client demo — Q4 roadmap", page.ScopePills.SessionTitle);
        Assert.True(page.ScopePills.IsLibrary);
        Assert.False(page.ScopePills.IsSession);

        page.ScopePills.PickSessionCommand.Execute(null);
        Assert.Equal(_demo.Id, page.Chat.Scope);
        Assert.True(page.ScopePills.IsSession);
        Assert.False(page.ScopePills.IsLibrary);

        page.ScopePills.PickLibraryCommand.Execute(null);
        Assert.Null(page.Chat.Scope);
        Assert.True(page.ScopePills.IsLibrary);

        await AskAsync(_assistant, page.Chat, "Q", "A");
        Assert.Null(_assistant.Asks[0].Scope);
    }

    [Fact]
    public async Task PickingAScopeOnAChatWithMessages_StartsANewChat_AndKeepsTheOldOne()
    {
        var page = CreatePage();
        page.StartNew(_demo);
        await AskAsync(_assistant, page.Chat, "About the library", "A");
        var first = page.Chat;
        page.Chat.Question = "half typed";

        page.ScopePills.PickSessionCommand.Execute(null);

        Assert.NotSame(first, page.Chat);
        Assert.Equal(_demo.Id, page.Chat.Scope);
        Assert.Equal("half typed", page.Chat.Question);
        Assert.True(page.Chat.IsEmpty);
        Assert.Single(_store.Threads);

        await AskAsync(_assistant, page.Chat, "About the demo", "B");
        Assert.Equal(_demo.Id, _assistant.Asks[^1].Scope);
        Assert.Equal(2, _store.Threads.Count);
        Assert.Equal(_demo.Id, _store.Threads[0].Scope);
    }

    [Fact]
    public async Task AScopedThread_ReopensWithItsRecordingPillActive()
    {
        var thread = await _store.CreateThreadAsync("About the demo", _demo.Id, CancellationToken.None);
        var page = CreatePage();
        await page.ThreadsTask;

        page.Entries[0].OpenCommand.Execute(null);
        await page.ActionTask;

        Assert.Equal(_demo.Id, page.Chat.Scope);
        Assert.True(page.ScopePills.HasSession);
        Assert.True(page.ScopePills.IsSession);
        Assert.Equal("Client demo — Q4 roadmap", page.ScopePills.SessionTitle);
        Assert.Equal(thread.Id, page.Chat.Thread!.Id);
    }

    [Fact]
    public async Task StartNew_ReusesAnEmptyChat_AndLeavesAChatWithMessagesInTheList()
    {
        var page = CreatePage();
        var empty = page.Chat;
        page.StartNew();
        Assert.Same(empty, page.Chat);

        await AskAsync(_assistant, page.Chat, "Q", "A");
        page.StartNew();

        Assert.NotSame(empty, page.Chat);
        Assert.True(page.Chat.IsEmpty);
        Assert.Single(_store.Threads);
        Assert.Equal([false], page.Entries.Select(e => e.IsActive));
    }

    // ---- through the shell ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAskAiItemOfTheSidebar_StartsANewChat_WhenTheCurrentOneIsNotEmpty()
    {
        using var scope = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        var shell = rig.Shell;
        shell.Sidebar!.ShowChatCommand.Execute(null);
        var page = shell.LibraryChat!;
        var chat = page.Chat;
        chat.Question = "First question";
        chat.AskCommand.Execute(null);
        rig.Assistant.Answer(["Answer"]);
        await chat.AskTask;
        Assert.Equal(["First question"], shell.Sidebar.Entries.Select(e => e.Title));

        shell.ShowLibrary();
        shell.ShowLibraryChat();
        Assert.Same(chat, page.Chat);

        shell.Sidebar.ShowChatCommand.Execute(null);

        Assert.NotSame(chat, page.Chat);
        Assert.True(page.Chat.IsEmpty);
        Assert.Same(page, shell.CurrentPage);
        Assert.Equal(["First question"], shell.Sidebar.Entries.Select(e => e.Title));
        Assert.Equal([false], shell.Sidebar.Entries.Select(e => e.IsActive));
    }

    [Fact]
    public async Task TheLibraryQuestion_OpensANewThread_EvenWhenAChatIsOpen()
    {
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        var shell = rig.Shell;
        shell.ShowNewChat();
        var page = shell.LibraryChat!;
        page.Chat.Question = "Earlier";
        page.Chat.AskCommand.Execute(null);
        rig.Assistant.Answer(["A"]);
        await page.Chat.AskTask;
        var before = page.Chat;

        shell.AskLibrary("Library question");
        rig.Assistant.Answer(["B"]);
        await page.Chat.AskTask;

        Assert.NotSame(before, page.Chat);
        Assert.Equal(2, rig.ChatStore.Threads.Count);
        Assert.Equal("Library question", rig.ChatStore.Threads[0].Title);
        Assert.Equal(["Library question", "Earlier"], shell.Sidebar!.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task TheTopBar_ShowsThePillsAndTheModelChip_OnTheChatPage()
    {
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();

        rig.Shell.ShowNewChat(rig.ClientDemo);

        Assert.Same(rig.Shell.LibraryChat!.ScopePills, rig.Shell.TopBar.Leading);
        Assert.Same(rig.Shell.LibraryChat.ModelChip, rig.Shell.TopBar.Trailing);
        Assert.True(rig.Shell.LibraryChat is IFillsFrame);
        Assert.False(rig.Shell.PageUsesFrameInset);
        Assert.Equal(rig.ClientDemo.Title, rig.Shell.LibraryChat.ScopePills.SessionTitle);
    }

    // ---- model chip -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheModelChip_ShowsTheModelAsLocal_AndTheAccelerationWhenItIsNotTheCpu()
    {
        using var scope = UiCulture.Override("en");
        var source = new FakeAcceleration(new AccelerationStatus(AiAcceleration.Cpu, AiAcceleration.Cpu, AccelerationFallback.None));
        using var chip = new ChatModelChipViewModel(() => "Gemma 4 E4B", source, action => action());
        Assert.Equal("Gemma 4 E4B · local", chip.Text);

        var changes = new List<string?>();
        chip.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        source.Set(new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Vulkan, AccelerationFallback.None));
        Assert.Equal("Gemma 4 E4B · local · Vulkan", chip.Text);
        Assert.Contains(nameof(ChatModelChipViewModel.Text), changes);

        source.Set(new AccelerationStatus(AiAcceleration.Cuda, AiAcceleration.Cuda, AccelerationFallback.None));
        Assert.Equal("Gemma 4 E4B · local · CUDA", chip.Text);

        // A GPU server that failed to start falls back to the CPU build: the suffix goes.
        source.Set(new AccelerationStatus(AiAcceleration.Cuda, AiAcceleration.Cpu, AccelerationFallback.StartFailed, "out of memory"));
        Assert.Equal("Gemma 4 E4B · local", chip.Text);
    }

    [Fact]
    public void TheModelChip_WithoutAKnownModel_NamesGemma4()
    {
        using var scope = UiCulture.Override("en");
        using var chip = new ChatModelChipViewModel(() => null, null, action => action());

        Assert.Equal("Gemma 4 · local", chip.Text);
    }

    private sealed class FakeAcceleration(AccelerationStatus current) : IAccelerationStatusSource
    {
        public AccelerationStatus Current { get; private set; } = current;

        public event Action<AccelerationStatus>? Changed;

        public void Set(AccelerationStatus status)
        {
            Current = status;
            Changed?.Invoke(status);
        }
    }
}

internal static class ChatMessageTestExtensions
{
    public static bool Succeeded(this ChatMessageViewModel message) => !message.IsStreaming && !message.HasStatus;
}
