using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Intelligence;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class ChatViewModelTests : IDisposable
{
    private readonly FakeAssistant _assistant = new();
    private readonly FakeLlm _llm = new();
    private readonly FakeRecorder _recorder = new();
    private readonly SessionFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>Answers stream on other threads in these tests (no UI dispatcher): wait for the view model to catch up.</summary>
    internal static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting: " + because);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private ChatViewModel Create(SessionId? scope = null) =>
        new(_assistant, _llm, _recorder, scope, "/models", action => action());

    private static void AskQuestion(ChatViewModel chat, string question)
    {
        chat.Question = question;
        Assert.True(chat.AskCommand.CanExecute(null));
        chat.AskCommand.Execute(null);
    }

    // Streaming ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_StreamsTheAnswerIntoTheLastBubble_ThenShowsTheChips()
    {
        var scope = _fixture.Record.Id;
        var chat = Create(scope);

        AskQuestion(chat, "  Що вирішили щодо релізу?  ");

        var request = Assert.Single(_assistant.Asks);
        Assert.Equal("Що вирішили щодо релізу?", request.Question);
        Assert.Equal(scope, request.Scope);
        Assert.Empty(request.History!);
        Assert.Equal(2, chat.Messages.Count);
        Assert.True(chat.Messages[0].IsUser);
        Assert.Equal("Що вирішили щодо релізу?", chat.Messages[0].Text);
        var answer = chat.Messages[1];
        Assert.True(answer.IsAssistant);
        Assert.True(answer.ShowThinking);
        Assert.True(chat.IsGenerating);
        Assert.Equal(string.Empty, chat.Question);
        Assert.False(chat.AskCommand.CanExecute(null));
        Assert.True(chat.CancelCommand.CanExecute(null));

        _assistant.Push("Реліз ");
        await Eventually(() => answer.Text == "Реліз ", "the first delta");
        Assert.False(answer.ShowThinking);
        Assert.True(answer.IsStreaming);

        _assistant.Push("переносимо на п'ятницю [12:34]");
        _assistant.Push(", тести — на Олені [41:05].");
        await Eventually(() => answer.Text.EndsWith("[41:05].", StringComparison.Ordinal), "all deltas");
        Assert.Equal("Реліз переносимо на п'ятницю [12:34], тести — на Олені [41:05].", answer.Text);
        Assert.False(answer.HasCitations);

        _assistant.Finish(Hits.Cite(scope, 754, "переносимо реліз"), Hits.Cite(scope, 2465, "Олена бере тести"));
        await chat.AskTask;

        Assert.False(chat.IsGenerating);
        Assert.False(answer.IsStreaming);
        Assert.False(answer.HasStatus);
        Assert.Equal(["12:34", "41:05"], answer.Citations.Select(c => c.Label));
        Assert.Equal("Олена бере тести", answer.Citations[1].Snippet);
    }

    [Fact]
    public async Task ASecondQuestion_SendsTheEarlierTurnsAsHistory()
    {
        var chat = Create(_fixture.Record.Id);
        AskQuestion(chat, "Хто веде реліз?");
        _assistant.Answer(["Андрій [01:10]."], Hits.Cite(_fixture.Record.Id, 70));
        await chat.AskTask;

        AskQuestion(chat, "А коли?");

        var second = _assistant.Asks[1];
        Assert.Equal(
            [new ChatTurn(TurnRole.User, "Хто веде реліз?"), new ChatTurn(TurnRole.Assistant, "Андрій [01:10].")],
            second.History);
        _assistant.Finish();
        await chat.AskTask;
        Assert.Equal(4, chat.Messages.Count);
    }

    [Fact]
    public void Ask_WithABlankQuestion_IsDisabled()
    {
        var chat = Create();
        chat.Question = "   ";

        Assert.False(chat.AskCommand.CanExecute(null));
        chat.AskCommand.Execute(null);
        Assert.Empty(_assistant.Asks);
    }

    // Cancel and errors -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_StopsTheAnswer_KeepsWhatArrived_AndAllowsANewQuestion()
    {
        var chat = Create();
        AskQuestion(chat, "Підсумуй мітинг");
        var answer = chat.Messages[1];
        _assistant.Push("Обговорили бюджет");
        await Eventually(() => answer.HasText, "the first delta");

        chat.CancelCommand.Execute(null);
        await chat.AskTask;

        Assert.False(chat.IsGenerating);
        Assert.Equal("Обговорили бюджет", answer.Text);
        Assert.Equal(ChatViewModel.CancelledText, answer.StatusText);
        Assert.False(answer.HasCitations);
        Assert.False(chat.CancelCommand.CanExecute(null));

        // A cancelled answer is not sent back as history.
        AskQuestion(chat, "Ще раз");
        Assert.Empty(_assistant.Asks[1].History!);
        _assistant.Finish();
        await chat.AskTask;
    }

    [Fact]
    public async Task Cancel_BeforeAnythingArrived_ShowsStopped()
    {
        var chat = Create();
        AskQuestion(chat, "Підсумуй мітинг");

        chat.CancelCommand.Execute(null);
        await chat.AskTask;

        var answer = chat.Messages[1];
        Assert.Equal(ChatViewModel.CancelledText, answer.StatusText);
        Assert.False(answer.ShowThinking);
        Assert.False(chat.IsGenerating);
    }

    [Fact]
    public async Task AFailingAnswer_ShowsTheError_InTheBubble()
    {
        var chat = Create();
        AskQuestion(chat, "Що там?");

        _assistant.Fail(new HttpRequestException("сервер не відповідає"));
        await chat.AskTask;

        var answer = chat.Messages[1];
        Assert.Equal(ChatViewModel.FailedText("сервер не відповідає"), answer.StatusText);
        Assert.False(answer.IsStreaming);
        Assert.False(chat.IsGenerating);
        Assert.Equal(ChatAvailability.Ready, chat.Availability);
    }

    [Fact]
    public async Task ARecordingThatStartsMidAnswer_PausesTheChat()
    {
        var chat = Create();
        AskQuestion(chat, "Що там?");
        _assistant.Push("Почато");
        await Eventually(() => chat.Messages[1].HasText, "the first delta");

        // LocalLlm kills its servers when a recording starts: the request fails.
        await _recorder.StartAsync(new RecordingOptions(), CancellationToken.None);
        _assistant.Fail(new InvalidOperationException("AI is paused while recording."));
        await chat.AskTask;

        Assert.Equal(ChatAvailability.PausedForRecording, chat.Availability);
        Assert.Equal(ChatViewModel.InterruptedByRecordingText, chat.Messages[1].StatusText);
    }

    // States ------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutTheModels_ExplainsHowToInstallThem_AndCannotAsk()
    {
        _llm.Status = LlmStatus.NotInstalled;
        var chat = Create();
        chat.Question = "Що вирішили?";

        Assert.Equal(ChatAvailability.NotInstalled, chat.Availability);
        Assert.True(chat.ShowNotice);
        Assert.True(chat.CanCheckAgain);
        Assert.False(chat.ShowEmptyHint);
        Assert.False(chat.CanEditQuestion);
        Assert.Equal(ChatViewModel.NotInstalledTitle, chat.NoticeTitle);
        Assert.Contains("scripts/fetch-models.ps1 -Ai", chat.NoticeText, StringComparison.Ordinal);
        Assert.Contains("/models", chat.NoticeText, StringComparison.Ordinal);
        Assert.False(chat.AskCommand.CanExecute(null));

        // Installed later: "Перевірити знову" picks it up without a restart.
        _llm.Status = LlmStatus.Unloaded;
        chat.RefreshAvailabilityCommand.Execute(null);

        Assert.Equal(ChatAvailability.Ready, chat.Availability);
        Assert.False(chat.ShowNotice);
        Assert.True(chat.AskCommand.CanExecute(null));
    }

    [Fact]
    public void WithoutAnAssistant_IsNotInstalled()
    {
        var chat = new ChatViewModel(null, null, null, null, post: action => action());

        Assert.Equal(ChatAvailability.NotInstalled, chat.Availability);
        Assert.DoesNotContain("файли потраплять", chat.NoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileRecording_TheChatIsPaused_AndResumesAfterwards()
    {
        var chat = Create();
        chat.Question = "Що вирішили?";
        Assert.True(chat.ShowEmptyHint);

        await _recorder.StartAsync(new RecordingOptions(), CancellationToken.None);

        Assert.Equal(ChatAvailability.PausedForRecording, chat.Availability);
        Assert.Equal(ChatViewModel.PausedTitle, chat.NoticeTitle);
        Assert.False(chat.CanCheckAgain);
        Assert.False(chat.AskCommand.CanExecute(null));

        await _recorder.StopAsync(CancellationToken.None);

        Assert.Equal(ChatAvailability.Ready, chat.Availability);
        Assert.True(chat.AskCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dispose_CancelsTheRunningAnswer_AndStopsFollowingTheRecorder()
    {
        var chat = Create();
        AskQuestion(chat, "Що там?");
        _assistant.Push("Почато");
        await Eventually(() => chat.Messages[1].HasText, "the answer to start");

        chat.Dispose();
        await chat.AskTask;
        await _recorder.StartAsync(new RecordingOptions(), CancellationToken.None);

        Assert.Equal(ChatViewModel.CancelledText, chat.Messages[1].StatusText);
        Assert.False(chat.IsGenerating);
        Assert.Equal(ChatAvailability.Ready, chat.Availability);
    }

    // Chips -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AChipInTheSessionChat_SeeksThePlayerThere_AndPlays()
    {
        _fixture.UseShortTranscript();
        var chat = Create(_fixture.Record.Id);
        var session = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), chat);
        await session.LoadAsync();
        session.IsAskTab = true;
        Assert.True(session.ShowAsk);

        AskQuestion(session.Chat, "Коли привітались?");
        _assistant.Answer(["На початку [00:20]."], Hits.Cite(_fixture.Record.Id, 12), Hits.Cite(_fixture.Record.Id, 20));
        await session.Chat.AskTask;

        session.Chat.Messages[1].Citations[1].ActivateCommand.Execute(null);

        Assert.Equal(S(20), Assert.Single(_fixture.Player.Seeks));
        Assert.Equal(1, _fixture.Player.Plays);
        Assert.Equal(PlaybackState.Playing, _fixture.Player.State);
        Assert.Equal(S(20), session.Position);
        Assert.Equal(1, session.CurrentSegmentIndex);
    }

    [Fact]
    public async Task AChipCitingAnotherSession_AsksTheShellToOpenIt()
    {
        var chat = Create(_fixture.Record.Id);
        var session = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), chat);
        await session.LoadAsync();
        (SessionId Id, TimeSpan At)? requested = null;
        session.OpenSessionRequested += (id, at) => requested = (id, at);
        var other = SessionId.New();

        AskQuestion(chat, "Що там?");
        _assistant.Answer(["Див. [02:00]."], Hits.Cite(other, 120));
        await chat.AskTask;
        chat.Messages[1].Citations[0].ActivateCommand.Execute(null);

        Assert.Equal((other, S(120)), requested);
        Assert.Empty(_fixture.Player.Seeks);
    }

    [Fact]
    public async Task SelectingTheAskTab_RechecksTheModels()
    {
        _llm.Status = LlmStatus.NotInstalled;
        var session = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), Create(_fixture.Record.Id));
        await session.LoadAsync();
        Assert.Equal(ChatAvailability.NotInstalled, session.Chat.Availability);

        _llm.Status = LlmStatus.Unloaded;
        session.IsAskTab = true;

        Assert.Equal(ChatAvailability.Ready, session.Chat.Availability);
    }

    [Fact]
    public async Task ClosingTheSession_DisposesItsChat()
    {
        var session = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), Create(_fixture.Record.Id));
        await session.LoadAsync();
        AskQuestion(session.Chat, "Що там?");
        _assistant.Push("Почато");
        await Eventually(() => session.Chat.Messages[1].HasText, "the answer to start");

        session.Dispose();
        await session.Chat.AskTask;

        Assert.Equal(ChatViewModel.CancelledText, session.Chat.Messages[1].StatusText);
        Assert.False(session.Chat.IsGenerating);
    }
}
