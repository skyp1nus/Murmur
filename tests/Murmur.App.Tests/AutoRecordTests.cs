using System.Text.Json;
using Murmur.App.Diagnostics;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;

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
    }

    [Fact]
    public async Task Run_RecordsSamplesAndWritesTheReport()
    {
        var path = Path.Combine(_dir, "nested", "perf.json");
        _recorder.Stats = new RecorderStats(100, 200, 0, 3);
        var runner = CreateRunner(new AutoRecordOptions(2, Screen: false, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.InRange(root.GetProperty("durationS").GetDouble(), 1.8, 3.5);
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
    public async Task Run_WithScreenFlag_EnablesTheScreen()
    {
        var path = Path.Combine(_dir, "perf.json");
        var runner = CreateRunner(new AutoRecordOptions(1, Screen: true, path, IdleSeconds: 0));

        var code = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.True(Assert.Single(_recorder.Starts).Screen);
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
