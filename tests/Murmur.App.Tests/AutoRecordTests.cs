using System.Text.Json;
using Murmur.App.Diagnostics;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class AutoRecordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "murmur-autorec-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRecorder _recorder = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSessionStore _store = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private AutoRecordRunner CreateRunner(AutoRecordOptions options)
    {
        var vm = new RecordingViewModel(_recorder, _capture, _store, action => action());
        return new AutoRecordRunner(options, vm, _recorder, _store, action => action());
    }

    [Fact]
    public void Parse_ReadsSecondsScreenAndPerfOut()
    {
        Assert.True(AutoRecordOptions.TryParse(["--autorecord", "30", "--screen", "--perf-out", "x.json"], out var options, out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal(new AutoRecordOptions(30, true, "x.json", 10), options);
    }

    [Fact]
    public void Parse_IdleSecondsCanBeOverridden()
    {
        Assert.True(AutoRecordOptions.TryParse(["--autorecord", "5", "--perf-out", "x.json", "--idle-seconds", "2"], out var options, out _));

        Assert.Equal(2, options!.IdleSeconds);
        Assert.False(options.Screen);
    }

    [Fact]
    public void Parse_NoOrb_HidesTheOrbForTheRun()
    {
        Assert.True(AutoRecordOptions.TryParse(["--autorecord", "5", "--no-orb", "--perf-out", "x.json"], out var options, out _));
        Assert.False(options!.ShowOrb);

        Assert.True(AutoRecordOptions.TryParse(["--autorecord", "5", "--perf-out", "x.json"], out options, out _));
        Assert.True(options!.ShowOrb);
    }

    [Fact]
    public void Parse_NoMicAndNoSystem_LeaveThatSourceOut()
    {
        Assert.True(AutoRecordOptions.TryParse(["--autorecord", "5", "--no-mic", "--perf-out", "x.json"], out var options, out _));
        Assert.False(options!.Microphone);
        Assert.True(options.SystemAudio);

        Assert.True(AutoRecordOptions.TryParse(["--autorecord", "5", "--no-system", "--perf-out", "x.json"], out options, out _));
        Assert.True(options!.Microphone);
        Assert.False(options.SystemAudio);

        Assert.True(AutoRecordOptions.TryParse(["--autorecord", "5", "--perf-out", "x.json"], out options, out _));
        Assert.True(options!.Microphone);
        Assert.True(options.SystemAudio);
    }

    [Fact]
    public void Parse_NoMicTogetherWithNoSystem_IsAnError()
    {
        Assert.False(AutoRecordOptions.TryParse(["--autorecord", "5", "--no-mic", "--no-system", "--perf-out", "x.json"], out var options, out var error));

        Assert.Null(options);
        Assert.Contains("--no-mic", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SlopePerMinute_IsTheLeastSquaresGrowthPerMinute()
    {
        var growing = new double[120];
        var flat = new double[120];
        for (var i = 0; i < growing.Length; i++)
        {
            growing[i] = 100 + (i * 0.05) + (i % 2 == 0 ? 0.5 : -0.5); // 3 MB/min with jitter
            flat[i] = 100 + (i % 2 == 0 ? 1 : -1);
        }

        Assert.Equal(3.0, AutoRecordRunner.SlopePerMinute(growing), 1);
        Assert.Equal(0.0, AutoRecordRunner.SlopePerMinute(flat), 1);
        Assert.Equal(0, AutoRecordRunner.SlopePerMinute([]));
        Assert.Equal(0, AutoRecordRunner.SlopePerMinute([42]));
        Assert.Equal(60, AutoRecordRunner.SlopePerMinute([1, 2]));
    }

    [Fact]
    public async Task Run_ReportsTheMemoryBreakdown()
    {
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(2, Screen: false, path, IdleSeconds: 0));

        Assert.Equal(0, await runner.RunAsync(TestContext.Current.CancellationToken));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        foreach (var name in new[] { "wsSlopeMbPerMin", "privateSlopeMbPerMin", "uiThreadAllocatedMb", "samplerAllocatedMb" })
        {
            Assert.True(root.TryGetProperty(name, out _), name);
        }

        Assert.True(root.GetProperty("gcHeapMaxMb").GetDouble() > 0);
        Assert.True(root.GetProperty("gcAllocatedMb").GetDouble() >= root.GetProperty("samplerAllocatedMb").GetDouble());

        // One reading at the start and one for the last sample.
        var memory = root.GetProperty("memory");
        Assert.Equal(2, memory.GetArrayLength());
        foreach (var sample in memory.EnumerateArray())
        {
            Assert.True(sample.GetProperty("wsMb").GetDouble() > 0);
            Assert.True(sample.GetProperty("privateMb").GetDouble() > 0);
            Assert.True(sample.GetProperty("gcHeapMb").GetDouble() > 0);
            Assert.True(sample.GetProperty("gcAllocatedMb").GetDouble() >= 0);
            Assert.True(sample.GetProperty("gen0Collections").GetInt32() >= 0);
        }

        Assert.True(memory[1].GetProperty("seconds").GetDouble() > memory[0].GetProperty("seconds").GetDouble());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Run_LeavesOutTheSourcesTheCommandLineExcludes(bool microphone, bool systemAudio)
    {
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, path, IdleSeconds: 0, Microphone: microphone, SystemAudio: systemAudio));

        Assert.Equal(0, await runner.RunAsync(TestContext.Current.CancellationToken));

        var options = Assert.Single(_recorder.Starts);
        Assert.Equal(microphone, options.Microphone);
        Assert.Equal(systemAudio, options.SystemAudio);
    }

    [Fact]
    public void Parse_WithoutTheFlag_IsANormalStart()
    {
        Assert.False(AutoRecordOptions.TryParse(["--startup-probe", "p.json"], out var options, out var error));

        Assert.Null(options);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("--autorecord")]
    [InlineData("--autorecord", "abc", "--perf-out", "x.json")]
    [InlineData("--autorecord", "0", "--perf-out", "x.json")]
    [InlineData("--autorecord", "-5", "--perf-out", "x.json")]
    [InlineData("--autorecord", "5")]
    [InlineData("--autorecord", "5", "--perf-out")]
    [InlineData("--autorecord", "5", "--perf-out", "x.json", "--idle-seconds", "x")]
    public void Parse_InvalidCommandLine_ReturnsFalseWithAMessage(params string[] args)
    {
        Assert.False(AutoRecordOptions.TryParse(args, out var options, out var error));

        Assert.Null(options);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        double[] values = [5, 1, 4, 2, 3, 10, 9, 8, 7, 6, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11];

        Assert.Equal(19, ProcessSampler.Percentile(values, 95));
        Assert.Equal(20, ProcessSampler.Percentile(values, 100));
        Assert.Equal(10, ProcessSampler.Percentile(values, 50));
        Assert.Equal(0, ProcessSampler.Percentile([], 95));
        Assert.Equal(7, ProcessSampler.Percentile([7], 95));
    }

    [Fact]
    public void ProcessSampler_ReportsPlausibleNumbers()
    {
        using var sampler = new ProcessSampler();
        var spin = Environment.TickCount64;
        while (Environment.TickCount64 - spin < 150)
        {
        }

        var sample = sampler.Sample();

        Assert.True(sample.CpuCorePct > 0);
        Assert.Equal(sample.CpuCorePct / Environment.ProcessorCount, sample.CpuMachinePct, 6);
        Assert.True(sample.WorkingSetMb > 1);
        Assert.True(sample.PrivateMb > 1);
    }

    [Fact]
    public void Report_SerializesWithCamelCaseNamesAndOmitsNullError()
    {
        var json = JsonSerializer.Serialize(
            new PerfReport { DurationS = 5, CpuAvgPct = 1.5, RecorderStats = new PerfRecorderStats { MicrophoneFrames = 7 } },
            PerfJsonContext.Default.PerfReport);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var name in new[] { "durationS", "idleCpuPct", "cpuAvgPct", "cpuP95Pct", "cpuAvgPctOneCore", "wsAvgMb", "wsMaxMb", "privateMaxMb", "recorderStats", "session" })
        {
            Assert.True(root.TryGetProperty(name, out _), name);
        }

        Assert.Equal(7, root.GetProperty("recorderStats").GetProperty("microphoneFrames").GetInt64());
        foreach (var name in new[] { "droppedFrames", "systemFrames", "screenFramesDropped" })
        {
            Assert.True(root.GetProperty("recorderStats").TryGetProperty(name, out _), name);
        }

        foreach (var name in new[] { "id", "folder", "micSeconds", "systemSeconds", "screenSegments" })
        {
            Assert.True(root.GetProperty("session").TryGetProperty(name, out _), name);
        }

        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task Run_RecordsSamplesAndWritesTheReport()
    {
        var path = Path.Combine(_dir, "nested", "perf.json");
        _recorder.Stats = new RecorderStats(100, 200, 0, 3);
        _recorder.Elapsed = TimeSpan.FromSeconds(2.3);
        var runner = CreateRunner(new AutoRecordOptions(2, Screen: false, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal(2.3, root.GetProperty("durationS").GetDouble());
        Assert.False(root.TryGetProperty("screenStartMs", out _));
        Assert.Equal(0, root.GetProperty("session").GetProperty("screenSegmentTimes").GetArrayLength());
        Assert.True(root.GetProperty("wsMaxMb").GetDouble() >= root.GetProperty("wsAvgMb").GetDouble());
        Assert.True(root.GetProperty("cpuP95Pct").GetDouble() >= 0);
        Assert.Equal(200, root.GetProperty("recorderStats").GetProperty("systemFrames").GetInt64());
        Assert.Equal(3, root.GetProperty("recorderStats").GetProperty("screenFramesDropped").GetInt64());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal(32, root.GetProperty("session").GetProperty("id").GetString()!.Length);

        var options = Assert.Single(_recorder.Starts);
        Assert.True(options.Microphone);
        Assert.True(options.SystemAudio);
        Assert.False(options.Screen);
        Assert.Equal(1, _recorder.Stops);
    }

    [Fact]
    public async Task Run_WithoutAMicrophone_RecordsSystemAudioOnly()
    {
        _capture.Microphones.Clear();
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        var options = Assert.Single(_recorder.Starts);
        Assert.False(options.Microphone);
        Assert.True(options.SystemAudio);
    }

    [Fact]
    public async Task Run_WhenTheMicrophoneIsDenied_RecordsAndReportsTheWarning()
    {
        const string warning = "Мікрофон недоступний: Windows забороняє доступ (Параметри → Конфіденційність і безпека → Мікрофон). Записую лише системний звук.";
        _recorder.StartWarning = warning;
        _recorder.Elapsed = TimeSpan.FromSeconds(1.2);
        _recorder.Stats = new RecorderStats(0, 200, 0, 0);
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _));
        var warnings = root.GetProperty("warnings");
        Assert.Equal(1, warnings.GetArrayLength());
        Assert.Equal(warning, warnings[0].GetString());
        Assert.Equal(200, root.GetProperty("recorderStats").GetProperty("systemFrames").GetInt64());
        Assert.True(root.GetProperty("durationS").GetDouble() > 0);
        Assert.Equal(32, root.GetProperty("session").GetProperty("id").GetString()!.Length);
        Assert.Equal(1, _recorder.Stops);
    }

    [Fact]
    public async Task Run_WhenTheStopAddsToTheWarning_KeepsOnlyTheFullerText()
    {
        _recorder.StartWarning = "Mic denied.";
        _recorder.StopError = "Screen failed. Mic denied.";
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, path, IdleSeconds: 0));

        Assert.Equal(0, await runner.RunAsync(TestContext.Current.CancellationToken));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var warnings = doc.RootElement.GetProperty("warnings");
        Assert.Equal(1, warnings.GetArrayLength());
        Assert.Equal("Screen failed. Mic denied.", warnings[0].GetString());
    }

    [Fact]
    public async Task Run_WithScreenFlag_EnablesTheScreen()
    {
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: true, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.True(Assert.Single(_recorder.Starts).Screen);
    }

    [Fact]
    public async Task Run_DurationIsTheRecorderTimeline_NotTheSamplingWindow()
    {
        // The sampling window starts when StartAsync returns (one second here); the files are on the recorder's
        // timeline, which started with the audio before the screen start-up: durationS is that timeline's length.
        _recorder.Elapsed = TimeSpan.FromSeconds(12.5);
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, path, IdleSeconds: 0));

        Assert.Equal(0, await runner.RunAsync(TestContext.Current.CancellationToken));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(12.5, doc.RootElement.GetProperty("durationS").GetDouble());
    }

    [Fact]
    public async Task Run_WhenTheRecorderReportsNoElapsedTime_FallsBackToTheSessionDuration()
    {
        // Elapsed is 0 once the recorder has ended the session by itself; the fake finishes with Duration = Elapsed,
        // so give it the duration the finished session carries through the stop error path instead.
        var path = Path.Combine(_dir, "perf.json");
        _recorder.Elapsed = TimeSpan.Zero;
        _recorder.FinishedDuration = TimeSpan.FromSeconds(7.5);
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, path, IdleSeconds: 0));

        Assert.Equal(0, await runner.RunAsync(TestContext.Current.CancellationToken));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(7.5, doc.RootElement.GetProperty("durationS").GetDouble());
    }

    [Fact]
    public async Task Run_WithScreen_ReportsTheSegmentsAndHowLongTheScreenStartTook()
    {
        // The start call returned at session time 2.0 s; the recorder asked the screen to start at 0.45 s.
        _recorder.Elapsed = TimeSpan.FromSeconds(2.0);
        _recorder.Segments.Add(new ScreenSegment(0, TimeSpan.FromSeconds(0.45), TimeSpan.FromSeconds(1.55)));
        _recorder.Segments.Add(new ScreenSegment(1, TimeSpan.FromSeconds(3.25), TimeSpan.FromSeconds(0.5)));
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: true, path, IdleSeconds: 0));

        Assert.Equal(0, await runner.RunAsync(TestContext.Current.CancellationToken));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal(2.0, root.GetProperty("durationS").GetDouble());
        Assert.Equal(1550, root.GetProperty("screenStartMs").GetDouble());
        var session = root.GetProperty("session");
        Assert.Equal(2, session.GetProperty("screenSegments").GetInt32());
        var segments = session.GetProperty("screenSegmentTimes");
        Assert.Equal(2, segments.GetArrayLength());
        Assert.Equal(0, segments[0].GetProperty("index").GetInt32());
        Assert.Equal(0.45, segments[0].GetProperty("offsetS").GetDouble());
        Assert.Equal(1.55, segments[0].GetProperty("durationS").GetDouble());
        Assert.Equal(1, segments[1].GetProperty("index").GetInt32());
        Assert.Equal(3.25, segments[1].GetProperty("offsetS").GetDouble());
        Assert.Equal(0.5, segments[1].GetProperty("durationS").GetDouble());
    }

    [Fact]
    public async Task Run_WhenTheScreenProducedNoSegment_OmitsTheScreenStart()
    {
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: true, path, IdleSeconds: 0));

        Assert.Equal(0, await runner.RunAsync(TestContext.Current.CancellationToken));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(doc.RootElement.TryGetProperty("screenStartMs", out _));
    }

    [Fact]
    public async Task Run_WhenScreenIsRequestedButUnavailable_FailsWithAnErrorField()
    {
        _capture.Screen = false;
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: true, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Contains("Screen", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(_recorder.Starts);
    }

    [Fact]
    public async Task Run_WhenTheRecorderFailsToStart_ExitsNonZeroWithTheError()
    {
        _recorder.StartFailure = new InvalidOperationException("no loopback device");
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Contains("no loopback device", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenThePerfFileCannotBeWritten_ExitsNonZero()
    {
        var blocker = Path.Combine(_dir, "file");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: false, Path.Combine(blocker, "perf.json"), IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
    }
}
