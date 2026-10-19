using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The onboarding with every service faked: the settings screen, the shell, a recorder, a model manager and a pipeline.</summary>
internal sealed class OnboardingRig : IDisposable
{
    public OnboardingRig(AppSettings? initial = null, bool models = true, bool processing = true)
    {
        Store = new FakeSettingsStore(initial);
        Settings = new SettingsViewModel(Store, Capture, models ? Manager : null, null, "/data/Recordings", null, action => action(), version: "0.4.0");
        Onboarding = new OnboardingViewModel(
            Settings,
            Store,
            Capture,
            Recorder,
            Sessions,
            models ? Manager : null,
            processing ? Processing : null,
            processing ? Transcripts : null,
            Time,
            Launcher,
            action => action(),
            path => FreeBytes);
        var library = new LibraryViewModel(Sessions, Time, null, action => action());
        var recording = new RecordingViewModel(Recorder, Capture, Sessions, action => action(), Store);
        Shell = new ShellViewModel(
            library,
            recording,
            chatFactory: scope => new ChatViewModel(new FakeAssistant(), new FakeLlm(), Recorder, scope, "/models"),
            settings: Settings,
            onboarding: Onboarding);
    }

    public FakeSettingsStore Store { get; }

    public FakeDisplayCapture Capture { get; } = new();

    public FakeCapture Devices => Capture.Inner;

    public FakeRecorder Recorder { get; } = new();

    public FakeSessionStore Sessions { get; } = new();

    public FakeModelManager Manager { get; } = new();

    public FakeProcessing Processing { get; } = new();

    public FakeTranscriptStore Transcripts { get; } = new();

    public FakePrivacyLauncher Launcher { get; } = new();

    public TestTime Time { get; } = new(UiHarness.Now);

    public long FreeBytes { get; set; } = 250_000_000_000;

    public SettingsViewModel Settings { get; }

    public OnboardingViewModel Onboarding { get; }

    public ShellViewModel Shell { get; }

    /// <summary>Presses Continue until the given step is current (each step is activated on the way; the microphone check is awaited).</summary>
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

    /// <summary>
    /// Moves the countdown's clock forward in 250 ms steps until <paramref name="done"/> holds. The step's loop creates its next
    /// timer after each wake-up, so every step waits for it first.
    /// </summary>
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
    public static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The condition did not become true in time.");
    }

    public void Dispose()
    {
        Onboarding.Dispose();
        Settings.Dispose();
    }
}
