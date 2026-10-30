using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Models;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Design;

/// <summary>
/// The whole app with every service faked and the demo data of <see cref="DesignDemoData"/> in it: library, recording, a
/// session view, the library chat, Settings and the onboarding. English, devices of the canvas, the canvas clock.
/// </summary>
internal sealed class DesignRig : IDisposable
{
    private SessionFixture? _fixture;

    public DesignRig(bool onboarding = false)
    {
        Store = new FakeSettingsStore(new AppSettings
        {
            Language = "en",
            MicrophoneDeviceId = DesignDemoData.Microphone.Id,
            SystemAudioDeviceId = DesignDemoData.Speakers.Id,
            ScreenDisplayId = DesignDemoData.Display.Id,
            OnboardingCompleted = !onboarding,
            RecordHotkey = DesignDemoData.RecordHotkey,
            ToggleScreenHotkey = DesignDemoData.ToggleScreenHotkey,
            ShowAppHotkey = DesignDemoData.ShowAppHotkey,
        });

        Capture.Inner.Microphones.Clear();
        Capture.Inner.Microphones.Add(DesignDemoData.Microphone);
        Capture.Inner.Outputs.Clear();
        Capture.Inner.Outputs.Add(DesignDemoData.Speakers);
        Capture.Displays.Clear();
        Capture.Displays.Add(DesignDemoData.Display);

        Manager = new FakeModelManager(DesignDemoData.ModelCatalog);
        Manager.RecommendedChatModelId = DesignDemoData.Ids.ChatSmall;
        foreach (var record in DesignDemoData.Recordings)
        {
            var transcribing = record.TranscribingPercent is not null;
            var session = Sessions.Make(record.Title, record.StartedAt, record.Duration, record.Microphone, record.SystemAudio, record.Screen, transcribing ? SessionState.Transcribing : SessionState.Recorded);
            Records.Add(session);
            SessionStore.Add(session);

            // The language and first line of the row come from the stored transcript (the one still being transcribed shows its language only).
            var line = record.Snippet is null
                ? []
                : (IReadOnlyList<TranscriptSegment>)[new TranscriptSegment(Speaker.Me, TimeSpan.Zero, TimeSpan.FromSeconds(5), record.Snippet, [])];
            Transcripts.PerSession[session.Id] = new Transcript(session.Id, line, "design", record.LanguageCode);
            if (record.TranscribingPercent is { } percent)
            {
                Processing.Set(new SessionProcessingStatus(session.Id, SessionProcessingStage.Transcribing, percent / 100.0));
            }
        }

        Settings = new SettingsViewModel(Store, Capture, Manager, null, DesignDemoData.StoragePath, Folders, action => action(), version: DesignDemoData.Version);
        Onboarding = new OnboardingViewModel(
            Settings,
            Store,
            Capture,
            Recorder,
            SessionStore,
            Manager,
            Processing,
            Transcripts,
            Time,
            Launcher,
            action => action(),
            path => DesignDemoData.OnboardingFreeBytes);
        Library = new LibraryViewModel(SessionStore, Time, Processing, action => action(), keywordSearch: new FakeSearchIndex(), assistant: Assistant, recorder: Recorder, transcripts: Transcripts, settings: Store);
        Recording = new RecordingViewModel(Recorder, Capture, SessionStore, action => action(), Store);
        Shell = new ShellViewModel(
            Library,
            Recording,
            sessionFactory: CreateSession,
            chatFactory: scope => new ChatViewModel(Assistant, Llm, Recorder, scope, "/models", action => action(), ChatStore, SessionStore, Time),
            settings: Settings,
            onboardingFactory: () => Onboarding,
            settingsStore: Store,
            trials: new TrialSessionCleaner(SessionStore, Processing),
            post: action => action(),
            chatStore: ChatStore,
            sessionStore: SessionStore,
            chatModelChip: new ChatModelChipViewModel(() => ChatModelName, null, action => action()));
    }

    public FakeSettingsStore Store { get; }

    public FakeDisplayCapture Capture { get; } = new();

    public FakeCapture Devices => Capture.Inner;

    public FakeRecorder Recorder { get; } = new();

    public FakeSessionStore SessionStore { get; } = new();

    /// <summary>The saved chats of Ask AI.</summary>
    public FakeChatStore ChatStore { get; } = new();

    /// <summary>The records of <see cref="DesignDemoData.Recordings"/>, in the same order.</summary>
    public List<SessionRecord> Records { get; } = [];

    public FakeModelManager Manager { get; }

    public FakeProcessing Processing { get; } = new();

    public FakeTranscriptStore Transcripts { get; } = new();

    public FakeAssistant Assistant { get; } = new();

    /// <summary>The model host the chats ask about the models (installed or not).</summary>
    public FakeLlm Llm { get; } = new();

    /// <summary>The name the model chip shows.</summary>
    public string? ChatModelName { get; set; } = DesignDemoData.ChatModelName;

    public FakeFolderLauncher Folders { get; } = new();

    public FakePrivacyLauncher Launcher { get; } = new();

    public TestTime Time { get; } = new(UiHarness.Now);

    public SettingsViewModel Settings { get; }

    public OnboardingViewModel Onboarding { get; }

    public LibraryViewModel Library { get; }

    public RecordingViewModel Recording { get; }

    public ShellViewModel Shell { get; }

    /// <summary>The first recording (Client demo), the one the session board opens.</summary>
    public SessionRecord ClientDemo => Records[0];

    public SessionRecord SprintRetro => Records[5];

    /// <summary>The files, audio and transcript of the Client demo; created on first use.</summary>
    public SessionFixture Fixture => _fixture ??= CreateFixture();

    private SessionViewModel CreateSession(SessionRecord record) =>
        new(
            record,
            Fixture.Files,
            Fixture.Transcripts,
            Fixture.Player,
            Fixture.Processing,
            Fixture.Time,
            action => action(),
            new ChatViewModel(Assistant, new FakeLlm(), Recorder, record.Id, "/models", action => action()));

    private SessionFixture CreateFixture()
    {
        var fixture = new SessionFixture(duration: ClientDemo.Duration, record: ClientDemo);
        fixture.UseTranscript(BuildTranscript(ClientDemo.Id));
        fixture.WritePeaks(DesignDemoData.SessionPeaks(ClientDemo.Duration));
        return fixture;
    }

    /// <summary>The lines of the canvas as a transcript: the words of a line are spread evenly up to the next line.</summary>
    public static Transcript BuildTranscript(SessionId id)
    {
        var lines = DesignDemoData.Transcript;
        var segments = new List<TranscriptSegment>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var start = TimeSpan.FromSeconds(lines[i].StartSeconds);
            var end = i + 1 < lines.Count ? TimeSpan.FromSeconds(lines[i + 1].StartSeconds - 1) : start + TimeSpan.FromSeconds(8);
            var texts = lines[i].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var step = (end - start) / texts.Length;
            var words = new TranscriptWord[texts.Length];
            for (var w = 0; w < texts.Length; w++)
            {
                words[w] = new TranscriptWord(texts[w], start + (step * w), start + (step * (w + 1)));
            }

            segments.Add(new TranscriptSegment(lines[i].Speaker, start, end, lines[i].Text, words));
        }

        return new Transcript(id, segments, "design");
    }

    public void Dispose()
    {
        Onboarding.Dispose();
        Settings.Dispose();
        _fixture?.Dispose();
    }

    /// <summary>Presses Continue until the given onboarding step is current (the microphone check is awaited on the way).</summary>
    public async Task GoToStepAsync(int step)
    {
        while (Onboarding.StepNumber < step)
        {
            if (Onboarding.StepNumber == 2)
            {
                await Onboarding.Audio.Settled;
            }

            Onboarding.ContinueCommand.Execute(null);
        }
    }

    /// <summary>Moves the manual clock forward in 250 ms steps until <paramref name="done"/> holds.</summary>
    public async Task RunClockUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            for (var wait = 0; wait < 500 && Time.ActiveTimers == 0 && !done(); wait++)
            {
                await Task.Delay(5);
            }

            if (done())
            {
                return;
            }

            Time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }
    }

    /// <summary>Waits (real time, at most 5 s) until <paramref name="condition"/> holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new TimeoutException("The scene did not reach: " + what);
        }
    }
}
