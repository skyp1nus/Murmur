using Murmur.App.ViewModels;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Fakes;

/// <summary>A session folder in a temp directory plus every fake a <see cref="SessionViewModel"/> needs.</summary>
internal sealed class SessionFixture : IDisposable
{
    public SessionFixture(SessionState state = SessionState.Ready, bool withAudio = true, TimeSpan? duration = null, SessionRecord? record = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "murmur-app-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Files = new SessionFiles(Directory);
        Record = record ?? Sessions.Make("Щотижневий стендап команди", UiHarness.Now.AddHours(-1), duration ?? TimeSpan.FromHours(1), state: state);
        if (withAudio)
        {
            // The view model only checks that the files exist; the fake player never reads them.
            File.WriteAllBytes(Files.MicrophoneAudio, [0]);
            File.WriteAllBytes(Files.SystemAudio, [0]);
        }

        Player = new FakeAudioPlayer { Duration = Record.Duration };
        Time = new TestTime(UiHarness.Now);
    }

    public string Directory { get; }

    public SessionFiles Files { get; }

    public SessionRecord Record { get; }

    public FakeAudioPlayer Player { get; }

    public FakeTranscriptStore Transcripts { get; } = new();

    public FakeProcessing Processing { get; } = new();

    public TestTime Time { get; }

    /// <summary>The view model, with events delivered synchronously (no UI thread in view-model tests).</summary>
    public SessionViewModel Create() =>
        new(Record, Files, Transcripts, Player, Processing, Time, action => action());

    public void UseTranscript(Transcript transcript) => Transcripts.Transcript = transcript;

    /// <summary>A short two-segment transcript: Me says four words from 10 s, Others three from 20 s (0.5 s apart).</summary>
    public Transcript UseShortTranscript()
    {
        var transcript = new Transcript(
            Record.Id,
            [
                TranscriptFactory.Segment(Speaker.Me, 10, "Привіт", "усім", "починаємо", "мітинг"),
                TranscriptFactory.Segment(Speaker.Others, 20, "Добрий", "день", "колеги"),
            ],
            "fake");
        Transcripts.Transcript = transcript;
        return transcript;
    }

    public void WritePeaks(byte[] peaks)
    {
        using var stream = File.Create(Files.Peaks);
        PeaksFile.Write(stream, peaks);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
