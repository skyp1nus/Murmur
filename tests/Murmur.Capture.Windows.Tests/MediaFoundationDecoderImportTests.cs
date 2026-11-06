using Murmur.Capture.Windows.Import;
using Murmur.Core.Audio;
using Murmur.Core.Import;
using Murmur.Core.Jobs;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;
using Murmur.Core.Transcription;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The import pipeline end to end with the real Media Foundation decoder (composed as on Windows: Media Foundation first,
/// then the managed WAV decoder), a fake ASR and in-memory stores; and the owner's own sample files. Windows only.
/// </summary>
[Trait("Category", "Windows")]
public sealed class MediaFoundationDecoderImportTests(ITestOutputHelper output) : IAsyncDisposable
{
    /// <summary>A folder of real files to import (MP3, M4A, FLAC, WMA, AAC, MP4, MOV, OGG, MKV, WEBM...), set by the owner.</summary>
    public const string SamplesVariable = "MURMUR_IMPORT_SAMPLES";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlySet<string> OptionalCodecs = new HashSet<string>(StringComparer.Ordinal) { ".ogg", ".mkv", ".webm" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "murmur-mf-import-tests", Guid.NewGuid().ToString("N"));
    private readonly JobQueue _queue = new();
    private SessionProcessor? _processor;
    private ImportQueue? _imports;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _imports?.Dispose();
        _processor?.Dispose();
        await _queue.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AnMp4AndAnM4a_BecomeSessionsThatTranscribe_TheMp4KeepsItsVideoAsScreenSegmentZero()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation decoding runs only on Windows.");
        Directory.CreateDirectory(_dir);
        var mp4 = Path.Combine(_dir, "Team_sync.mp4");
        var m4a = Path.Combine(_dir, "Voice memo.m4a");
        TestMedia.WriteRequired(mp4, TestMedia.Aac, video: true, TimeSpan.FromSeconds(3));
        TestMedia.WriteRequired(m4a, TestMedia.Aac, video: false, TimeSpan.FromSeconds(2));
        var sessions = new InMemorySessionStore(Path.Combine(_dir, "library"));
        var asr = new FakeAsr();
        asr.Script("system.wav", (0.0, 1.0, "imported words"));
        var imports = Create(sessions, asr);
        await imports.StartAsync(Ct);

        var items = await imports.EnqueueAsync(
            [new ImportRequest(mp4, new ImportOptions(null, OriginalFileMode.CopyToLibrary)), new ImportRequest(m4a, new ImportOptions("uk", OriginalFileMode.LeaveInPlace))],
            Ct);
        var video = await WaitForSessionAsync(imports, sessions, items[0].Id);
        var audio = await WaitForSessionAsync(imports, sessions, items[1].Id);

        Assert.Equal(("Team sync", SessionSource.Imported, SessionState.Ready), (video.Title, video.Source, video.State));
        var segment = Assert.Single(video.ScreenSegments);
        Assert.Equal((0, TimeSpan.Zero), (segment.Index, segment.Offset));
        Assert.True((segment.Duration - TimeSpan.FromSeconds(3)).Duration() <= TimeSpan.FromMilliseconds(200), $"segment {segment.Duration}");
        var videoFiles = sessions.GetFiles(video.Id);
        Assert.True(File.Exists(videoFiles.ScreenSegment(0)));
        Assert.Equal(Path.Combine(videoFiles.Directory, "original.mp4"), video.OriginalPath);
        AssertTrack(videoFiles.SystemAudio);

        Assert.Equal(("Voice memo", SessionState.Ready, m4a), (audio.Title, audio.State, audio.OriginalPath));
        Assert.Empty(audio.ScreenSegments);
        AssertTrack(sessions.GetFiles(audio.Id).SystemAudio);

        Assert.Equal(
            [(videoFiles.SystemAudio, Speaker.Others, (string?)null), (sessions.GetFiles(audio.Id).SystemAudio, Speaker.Others, "uk")],
            asr.Requests.Select(r => (r.AudioPath, r.Speaker, r.Language)));
    }

    /// <summary>
    /// Every file in <see cref="SamplesVariable"/> with an extension the decoder takes: probed and decoded to the recorder's
    /// format, or (OGG, MKV, WEBM without their codecs) refused as unsupported. The results are written to the test output.
    /// </summary>
    [Fact]
    public async Task TheOwnersSampleFiles_DecodeOrAreCleanlyUnsupported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation decoding runs only on Windows.");
        var folder = Environment.GetEnvironmentVariable(SamplesVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), $"Set {SamplesVariable} to a folder of media files (pwsh scripts/verify-local.ps1 -Import -ImportSamples <folder>).");
        Assert.True(Directory.Exists(folder), $"{SamplesVariable} = '{folder}' is not a folder.");
        Directory.CreateDirectory(_dir);
        var decoder = new MediaFoundationDecoder();
        var files = Directory.EnumerateFiles(folder!).Where(f => decoder.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var probe = await decoder.ProbeAsync(file, Ct);
                await decoder.DecodeAudioAsync(file, destination, null, Ct);
                using var reader = WavReader.Open(destination);
                var format = (reader.Layout.SampleRate, reader.Layout.Channels, reader.Layout.BitsPerSample);
                var line = $"{name}: {probe.Kind}, probe {probe.Duration:g}, decoded {reader.Duration:g}, {format}";
                if (format != (16_000, 1, 16) || reader.FrameCount == 0)
                {
                    failures.Add(line);
                }
                else if (probe.Duration == TimeSpan.Zero)
                {
                    line += " (WARNING: the file declares no duration; the session would show 0:00)";
                }
                else if ((reader.Duration - probe.Duration).Duration() > TimeSpan.FromSeconds(Math.Max(0.5, probe.Duration.TotalSeconds * 0.02)))
                {
                    failures.Add(line + " (decoded length differs from the probe)");
                }

                output.WriteLine(line);
            }
            catch (NotSupportedException ex) when (OptionalCodecs.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                output.WriteLine($"{name}: unsupported on this PC ({ex.Message})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                output.WriteLine(failures[^1]);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private ImportQueue Create(InMemorySessionStore sessions, FakeAsr asr)
    {
        ImportQueue? imports = null;
        _processor = new SessionProcessor(new FakeRecorder(), sessions, new InMemoryTranscriptStore(), asr, _queue, languageOf: id => imports?.LanguageOf(id));
        var decoder = new CompositeMediaDecoder(new MediaFoundationDecoder(), new WavMediaDecoder());
        imports = _imports = new ImportQueue(sessions, new InMemoryImportStore().CascadeFrom(sessions), decoder, _queue, _processor);
        return imports;
    }

    private static async Task<SessionRecord> WaitForSessionAsync(ImportQueue imports, InMemorySessionStore sessions, long id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var item = imports.Items.First(i => i.Id == id);
            Assert.True(item.State != ImportState.Failed, $"import of {item.Path} failed: {item.Error}");
            if (item is { State: ImportState.Done, Session: { } session } && sessions.Get(session) is { State: SessionState.Ready } record)
            {
                return record;
            }

            Assert.True(DateTime.UtcNow < deadline, $"timed out: import {item.State}, session {(item.Session is { } s ? sessions.Get(s)?.State : null)}");
            await Task.Delay(20, Ct);
        }
    }

    private static void AssertTrack(string wav)
    {
        using var reader = WavReader.Open(wav);
        Assert.Equal((16_000, 1, 16), (reader.Layout.SampleRate, reader.Layout.Channels, reader.Layout.BitsPerSample));
        Assert.True(reader.FrameCount > 16_000, $"{wav}: {reader.FrameCount} frames");
    }
}
