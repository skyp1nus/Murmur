using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>Renders the phase 3 screens (AI chat, library search) with Avalonia.Headless (Skia) into docs/screenshots.</summary>
public sealed class Phase3ScreenshotTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static string Path(string name) => System.IO.Path.Combine(RepoPaths.ScreenshotsDirectory, name);

    /// <summary>Runs UI jobs until <paramref name="task"/> completes (its continuations are posted to the dispatcher).</summary>
    private static void PumpUntil(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out pumping the UI thread.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }

        task.GetAwaiter().GetResult();
        UiHarness.Pump();
    }

    [Fact]
    public async Task SessionChat_WithAnAnswerAndTwoChips_WritesPng()
    {
        using var fixture = new SessionFixture(duration: TimeSpan.FromMinutes(52) + TimeSpan.FromSeconds(9));
        fixture.UseTranscript(TranscriptFactory.Make(fixture.Record.Id, 8_000));
        fixture.WritePeaks(SessionScreenshotTests.FakePeaks(53 * 60));
        var assistant = new FakeAssistant();
        var llm = new FakeLlm();
        var harness = new UiHarness();
        harness.Store.Add(fixture.Record);
        var shell = harness.CreateShell(
            record => new SessionViewModel(
                record, fixture.Files, fixture.Transcripts, fixture.Player, fixture.Processing, fixture.Time,
                action => action(), new ChatViewModel(assistant, llm, harness.Recorder, record.Id, "/models")),
            fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        var id = fixture.Record.Id;

        var (texts, chips) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var session = shell.Session!;
                session.IsAskTab = true;
                UiHarness.Pump();

                session.Chat.Question = "Що ми вирішили щодо релізу?";
                session.Chat.AskCommand.Execute(null);
                assistant.Answer(
                    [
                        "Реліз переносимо на пʼятницю: інтеграція з платіжним сервісом ще в роботі [12:34]. ",
                        "Дедлайн і розподіл задач між командами узгоджують окремо, тести готує Олена [41:05].",
                    ],
                    Hits.Cite(id, 754, "інтеграція з платіжним сервісом ще в роботі"),
                    Hits.Cite(id, 2465, "розподіл задач між командами"));
                PumpUntil(session.Chat.AskTask);

                // The first chip was clicked: the player jumped to 12:34 and plays.
                session.Chat.Messages[1].Citations[0].ActivateCommand.Execute(null);
                session.Chat.Question = "Хто готує тести?";
                UiHarness.Pump(6);
                UiHarness.Save(window, Path("phase3-chat.png"));
                var visible = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                var chipLabels = window.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("chip") && b.IsEffectivelyVisible && b.FindAncestorOfType<ChatView>() is not null)
                    .Select(b => b.Content as string)
                    .ToArray();
                window.Close();
                return (visible, chipLabels);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["12:34", "41:05"], chips);
        Assert.Equal([TimeSpan.FromSeconds(754)], fixture.Player.Seeks);
        Assert.Equal(Murmur.Core.Capture.PlaybackState.Playing, fixture.Player.State);
        Assert.Contains("Що ми вирішили щодо релізу?", texts);
        Assert.True(new FileInfo(Path("phase3-chat.png")).Length > 10_000, "Screenshot looks empty.");
    }

    [Fact]
    public async Task LibrarySearch_WithTranscriptHits_WritesPng()
    {
        var harness = new UiHarness();
        harness.SeedSessions();
        var store = harness.Store;
        var records = (await store.ListSessionsAsync(new SessionQuery(), CancellationToken.None)).ToArray();
        var standup = records[0];
        var review = records[1];
        var interview = records[2];

        var assistant = new FakeAssistant
        {
            SearchResult =
            [
                Hits.Make(standup.Id, 754, "Реліз переносимо на пʼятницю: інтеграція з платіжним сервісом ще в роботі, тож давайте не ризикувати й узгодимо дедлайн з командою підтримки."),
                Hits.Make(review.Id, 1918, "Для релізу нової бібліотеки потрібні ще дві іконки й оновлені токени кольорів."),
                Hits.Make(standup.Id, 1302, "Хто бере реліз-ноти? Я підготую чернетку до четверга."),
                Hits.Make(interview.Id, 3725, "На минулій роботі я відповідав за реліз мобільного застосунку раз на два тижні."),
            ],
        };
        var library = new LibraryViewModel(store, harness.Time, post: action => action(), keywordSearch: new FakeSearchIndex(), assistant: assistant);
        var shell = new ShellViewModel(
            library,
            new RecordingViewModel(harness.Recorder, harness.Capture, store, action => action()),
            chatFactory: scope => new ChatViewModel(assistant, new FakeLlm(), harness.Recorder, scope, "/models"));
        await library.LoadAsync();

        var (rows, timeChips) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                library.SearchText = "реліз";
                harness.Time.Advance(LibraryViewModel.SearchDebounce);
                PumpUntil(library.SearchTask);
                UiHarness.Pump(6);
                UiHarness.Save(window, Path("phase3-library-search.png"));
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SearchResultList");
                var chips = window.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("chip") && b.IsEffectivelyVisible)
                    .Select(b => ((TextBlock)b.Child!).Text)
                    .ToArray();
                var result = (list.ItemCount, chips);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(4, rows);
        Assert.Equal(["12:34", "31:58", "21:42", "1:02:05"], timeChips);
        Assert.True(new FileInfo(Path("phase3-library-search.png")).Length > 10_000, "Screenshot looks empty.");
    }
}
