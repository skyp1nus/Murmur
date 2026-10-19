using System.Diagnostics;
using System.Text;
using static Murmur.Scripts.Tests.Mp4Builder;

namespace Murmur.Scripts.Tests;

/// <summary>
/// Runs <c>scripts/check-recording.ps1 -ExpectScreen</c> (PowerShell 7) over a synthetic session folder. Skipped when
/// <c>pwsh</c> is not on PATH (set <c>MURMUR_PWSH</c> to point at it). The script never plays or records anything.
/// </summary>
public sealed class CheckRecordingScriptTests : IDisposable
{
    private const double DurationS = 10;

    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "murmur-checkrec-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_screen_video_as_long_as_the_recording_passes()
    {
        var run = Run(["screen-000.mp4"], [Video(seconds: 10, fps: 30)]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Result: **PASS**", run.Output);
        Assert.Equal("PASS", Status(run, "screen video duration vs durationS"));
        Assert.Equal("PASS", Status(run, "screen-000.mp4 video"));
        Assert.Contains("300 frames", run.Output);
    }

    [Fact]
    public void The_twelve_frame_file_of_a_ten_minute_recording_fails()
    {
        uint[] first = [1232, 597, 1167, 1012, 770, 1690, 555, 1166, 1140, 1000];
        uint[] second = [392, 1000];

        var run = Run(["screen-000.mp4"], [Fragmented(30000, first, second)]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Result: **FAIL**", run.Output);
        Assert.Equal("FAIL", Status(run, "screen video duration vs durationS"));
        Assert.Contains("12 frames", run.Output);
    }

    [Fact]
    public void The_summed_duration_is_compared_within_the_tolerance()
    {
        // 8.5 s of video for 10 s of audio: 1.5 s short, inside the default 2 s.
        Assert.Equal(0, Run(["screen-000.mp4"], [Video(8.5, 30)]).ExitCode);

        // 7 s: 3 s short, outside it, but accepted with a wider tolerance.
        var narrow = Run(["screen-000.mp4"], [Video(7, 30)]);
        Assert.Equal(1, narrow.ExitCode);
        Assert.Equal("FAIL", Status(narrow, "screen video duration vs durationS"));
        Assert.Equal(0, Run(["screen-000.mp4"], [Video(7, 30)], "-ScreenToleranceS", "5").ExitCode);
    }

    [Fact]
    public void Segments_are_summed()
    {
        var run = Run(["screen-000.mp4", "screen-001.mp4"], [Video(4, 30), Video(5.5, 30)]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("PASS", Status(run, "screen-000.mp4 video"));
        Assert.Equal("PASS", Status(run, "screen-001.mp4 video"));
        Assert.Contains("9.500 s", run.Output);
    }

    [Fact]
    public void An_implausibly_low_frame_rate_fails_unless_the_check_is_disabled()
    {
        // 3 frames over 10 s = 0.3 fps: the duration is right, the frame count is not (a static screen still gets a
        // repeated frame every second).
        var slow = Run(["screen-000.mp4"], [Fragmented(30000, [[100_000, 100_000], [100_000]])]);
        Assert.Equal(1, slow.ExitCode);
        Assert.Equal("FAIL", Status(slow, "screen-000.mp4 video"));
        Assert.Equal("PASS", Status(slow, "screen video duration vs durationS"));

        Assert.Equal(0, Run(["screen-000.mp4"], [Fragmented(30000, [[100_000, 100_000], [100_000]])], "-MinScreenFps", "0").ExitCode);
    }

    [Fact]
    public void A_static_screen_at_one_repeated_frame_a_second_passes()
    {
        // Variable frame rate: a static screen holds the last frame and repeats it once a second (1 fps).
        var run = Run(["screen-000.mp4"], [Video(10, 1)]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("PASS", Status(run, "screen-000.mp4 video"));
    }

    [Fact]
    public void An_empty_or_broken_segment_fails()
    {
        var empty = Run(["screen-000.mp4"], [[]]);
        Assert.Equal(1, empty.ExitCode);

        var broken = Run(["screen-000.mp4"], [Encoding.ASCII.GetBytes("this is not an mp4 file at all")]);
        Assert.Equal(1, broken.ExitCode);
        Assert.Equal("FAIL", Status(broken, "screen-000.mp4 video"));
    }

    [Fact]
    public void Without_expect_screen_the_video_is_not_inspected()
    {
        var run = Run(["screen-000.mp4"], [Fragmented(30000, [1000, 1000])], expectScreen: false);

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("screen video", run.Output);
    }

    private const string SyncRow = "screen-000.mp4 duration vs session segment";

    [Fact]
    public void A_segment_as_long_as_the_session_says_passes_the_sync_check()
    {
        var run = RunWithSegments(["screen-000.mp4"], [Video(10, 30)], Segments((0, 0.0, 10.0)));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("PASS", Status(run, SyncRow));
        Assert.Equal("INFO", Status(run, "screen-000.mp4 media - segment durationS (signed)"));
        Assert.Contains("| 0.000 s (offsetS 0.000) |", run.Output);
    }

    [Fact]
    public void A_segment_that_started_late_fails_the_sync_check()
    {
        // The bug: media time 0 taken after the encoder start-up, so the file is shorter than the segment (0.5 s here,
        // a whole number of frames; the owner's PC lost 0.43-0.48 s).
        var run = RunWithSegments(["screen-000.mp4"], [Video(9.5, 30)], Segments((0, 0.0, 10.0)));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("FAIL", Status(run, SyncRow));
        Assert.Contains("| -0.500 s (offsetS 0.000) |", run.Output);
        Assert.Equal("PASS", Status(run, "screen video duration vs durationS")); // the summed check is far too loose for this
    }

    [Fact]
    public void The_sync_tolerance_is_a_quarter_second_and_can_be_changed()
    {
        Assert.Equal(0, RunWithSegments(["screen-000.mp4"], [Video(9.8, 30)], Segments((0, 0.0, 10.0))).ExitCode);
        Assert.Equal(0, RunWithSegments(["screen-000.mp4"], [Video(10.2, 30)], Segments((0, 0.0, 10.0))).ExitCode);

        var outside = RunWithSegments(["screen-000.mp4"], [Video(9.7, 30)], Segments((0, 0.0, 10.0)));
        Assert.Equal(1, outside.ExitCode);
        Assert.Equal("FAIL", Status(outside, SyncRow));

        Assert.Equal(0, RunWithSegments(["screen-000.mp4"], [Video(9.7, 30)], Segments((0, 0.0, 10.0)), "-SegmentToleranceS", "0.5").ExitCode);
    }

    [Fact]
    public void Each_segment_is_matched_to_its_entry_by_index()
    {
        var both = RunWithSegments(["screen-000.mp4", "screen-001.mp4"], [Video(4, 30), Video(5.5, 30)], Segments((0, 0.0, 4.0), (1, 4.5, 5.5)));
        Assert.Equal(0, both.ExitCode);
        Assert.Equal("PASS", Status(both, SyncRow));
        Assert.Equal("PASS", Status(both, "screen-001.mp4 duration vs session segment"));

        // The second file is compared with entry 1, not entry 0.
        var swapped = RunWithSegments(["screen-000.mp4", "screen-001.mp4"], [Video(4, 30), Video(5.5, 30)], Segments((0, 0.0, 5.5), (1, 4.5, 4.0)));
        Assert.Equal(1, swapped.ExitCode);
        Assert.Equal("FAIL", Status(swapped, SyncRow));
        Assert.Equal("FAIL", Status(swapped, "screen-001.mp4 duration vs session segment"));
    }

    [Fact]
    public void A_file_without_an_entry_fails_and_a_json_without_entries_skips_the_check()
    {
        var missing = RunWithSegments(["screen-000.mp4"], [Video(10, 30)], Segments((7, 0.0, 10.0)));
        Assert.Equal(1, missing.ExitCode);
        Assert.Equal("FAIL", Status(missing, SyncRow));

        var older = Run(["screen-000.mp4"], [Video(10, 30)]);
        Assert.Equal(0, older.ExitCode);
        Assert.Equal("SKIP", Status(older, SyncRow));
    }

    [Fact]
    public void The_signed_audio_difference_is_reported()
    {
        // system.wav is 10 s, durationS 10: zero. (The wav check itself stays +/- 1 s.)
        var run = Run(["screen-000.mp4"], [Video(10, 30)]);

        Assert.Equal("INFO", Status(run, "system.wav duration - durationS (signed)"));
        Assert.Contains("| 0.000 s |", run.Output);
        Assert.Equal("PASS", Status(run, "system.wav duration vs durationS"));
    }

    [Fact]
    public void The_screen_start_is_reported_when_the_perf_json_has_it()
    {
        var run = RunWithSegments(["screen-000.mp4"], [Video(10, 30)], Segments((0, 0.0, 10.0)), screenStartMs: 452.4);

        Assert.Equal("INFO", Status(run, "screenStartMs"));
        Assert.Contains("| 452 |", run.Output);
    }

    private const string SlopeRow = "working-set slope after 60 s";

    [Fact]
    public void A_flat_working_set_passes_the_slope_gate()
    {
        // 10 minutes at ~260 MB with +/- 1.5 MB of noise: no absolute cap, only growth counts.
        var run = RunMemory(Memory(600, t => 260 + (t / 10 % 3 == 0 ? 1.5 : -1.5)), "-MaxWsSlopeMbPerMin", "1");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("PASS", Status(run, SlopeRow));
        Assert.Contains("over 55 samples (60..600 s)", run.Output);
    }

    [Fact]
    public void A_growing_working_set_fails_the_slope_gate()
    {
        // 2 MB per minute after the first minute.
        var run = RunMemory(Memory(600, t => 200 + (2 * t / 60.0)), "-MaxWsSlopeMbPerMin", "1");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("FAIL", Status(run, SlopeRow));
        Assert.Contains("2.000 MB/min", run.Output);
    }

    [Fact]
    public void The_warm_up_is_left_out_of_the_fit()
    {
        // 100 -> 250 MB during the first minute (JIT, encoder, pools), flat afterwards: passes; fitted from the start, the
        // same run grows by far more than 1 MB/min (what the app's whole-run wsSlopeMbPerMin would say).
        var memory = Memory(600, t => t < 60 ? 100 + (2.5 * t) : 250);
        Assert.Equal(0, RunMemory(memory, "-MaxWsSlopeMbPerMin", "1").ExitCode);

        var fromStart = RunMemory(memory, "-MaxWsSlopeMbPerMin", "1", "-WsSlopeSkipS", "0");
        Assert.Equal(1, fromStart.ExitCode);
        Assert.Equal("FAIL", Status(fromStart, "working-set slope after 0 s"));
    }

    [Fact]
    public void Too_few_samples_after_the_warm_up_fail_the_gate_but_not_the_report()
    {
        // A 70 s run: samples at 0..70 s, only 60 and 70 after the warm-up.
        var memory = Memory(70, _ => 150);
        var gated = RunMemory(memory, "-MaxWsSlopeMbPerMin", "1");
        Assert.Equal(1, gated.ExitCode);
        Assert.Equal("FAIL", Status(gated, SlopeRow));
        Assert.Contains("too few samples to fit (2 after the warm-up, need 3)", gated.Output);

        // No memory[] at all (an older perf JSON) is "too few" as well.
        Assert.Equal(1, RunMemory(null, "-MaxWsSlopeMbPerMin", "1").ExitCode);

        var reported = RunMemory(memory);
        Assert.Equal(0, reported.ExitCode);
        Assert.Equal("INFO", Status(reported, SlopeRow));
    }

    [Fact]
    public void Without_a_limit_the_slope_is_only_reported()
    {
        var run = RunMemory(Memory(600, t => 200 + (5 * t / 60.0)));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("INFO", Status(run, SlopeRow));
        Assert.Contains("5.000 MB/min", run.Output);
    }

    /// <summary>A perf JSON memory[] array: a sample every 10 s from 0 to <paramref name="seconds"/>, wsMb = <paramref name="ws"/>(t).</summary>
    private static string Memory(int seconds, Func<int, double> ws) =>
        "[" + string.Join(", ", Enumerable.Range(0, (seconds / 10) + 1).Select(i => i * 10).Select(t =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{ \"seconds\": {t}, \"wsMb\": {ws(t):0.###}, \"privateMb\": 0, \"gcHeapMb\": 0 }}"))) + "]";

    private ScriptRun RunMemory(string? memory, params string[] options) => Run([], [], options, expectScreen: false, memory);

    // ------------------------------------------------------------------ plumbing

    private sealed record ScriptRun(int ExitCode, string Output);

    /// <summary>A fragmented MP4 of <paramref name="seconds"/> at <paramref name="fps"/>, one fragment per second.</summary>
    private static byte[] Video(double seconds, int fps)
    {
        const uint timescale = 30000;
        var frame = (uint)(timescale / fps);
        var total = (int)Math.Round(seconds * fps);
        var fragments = new List<uint[]>();
        for (var done = 0; done < total; done += fps)
        {
            fragments.Add([.. Enumerable.Repeat(frame, Math.Min(fps, total - done))]);
        }

        return Fragmented(timescale, [.. fragments]);
    }

    private ScriptRun Run(string[] names, byte[][] files, string? option = null, string? value = null, bool expectScreen = true) =>
        Run(names, files, option is null ? [] : [option, value!], expectScreen, memory: null);

    /// <summary>session.screenSegmentTimes JSON for <c>(index, offsetS, durationS)</c> entries.</summary>
    private static string Segments(params (int Index, double OffsetS, double DurationS)[] entries) =>
        "[" + string.Join(", ", entries.Select(e => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{ \"index\": {e.Index}, \"offsetS\": {e.OffsetS}, \"durationS\": {e.DurationS} }}"))) + "]";

    private ScriptRun RunWithSegments(string[] names, byte[][] files, string segments, string? option = null, string? value = null, double? screenStartMs = null) =>
        Run(names, files, option is null ? [] : [option, value!], expectScreen: true, memory: null, segments, screenStartMs);

    private ScriptRun Run(string[] names, byte[][] files, string[] options, bool expectScreen, string? memory, string? segments = null, double? screenStartMs = null)
    {
        var pwsh = FindPwsh();
        Assert.SkipUnless(pwsh is not null, "pwsh (PowerShell 7) is not available; set MURMUR_PWSH to run the script tests.");

        var session = Directory.CreateDirectory(Path.Combine(_dir, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllBytes(Path.Combine(session, "system.wav"), SilentWav(seconds: (int)DurationS));
        for (var i = 0; i < names.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(session, names[i]), files[i]);
        }

        var json = Path.Combine(session, "perf.json");
        var folder = session.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(
            json,
            $$"""
            { "durationS": {{DurationS}}, "idleCpuPct": 0.0, "cpuAvgPct": 0.0, "cpuP95Pct": 0.0, "cpuAvgPctOneCore": 0.0,
              "wsAvgMb": 0.0, "wsMaxMb": 0.0, "privateMaxMb": 0.0, "wsSlopeMbPerMin": 0.0,{{(memory is null ? string.Empty : " \"memory\": " + memory + ",")}}{{(screenStartMs is null ? string.Empty : string.Create(System.Globalization.CultureInfo.InvariantCulture, $" \"screenStartMs\": {screenStartMs},"))}}
              "recorderStats": { "microphoneFrames": 0, "systemFrames": 160000, "droppedFrames": 0, "screenFramesDropped": 0 },
              "session": { "id": "test", "folder": "{{folder}}", "micSeconds": 0.0, "systemSeconds": {{DurationS}}, "screenSegments": {{names.Length}}{{(segments is null ? string.Empty : ", \"screenSegmentTimes\": " + segments)}} } }
            """);

        var info = new ProcessStartInfo(pwsh!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(FindScript());
        info.ArgumentList.Add("-PerfJson");
        info.ArgumentList.Add(json);
        if (expectScreen)
        {
            info.ArgumentList.Add("-ExpectScreen");
        }

        foreach (var argument in options)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("check-recording.ps1 did not finish within 120 s.");
        }

        return new ScriptRun(process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    /// <summary>The Status cell of the report row whose first cell is <paramref name="check"/>.</summary>
    private static string Status(ScriptRun run, string check)
    {
        var prefix = "| " + check + " |";
        var line = run.Output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(line is not null, $"No report row '{check}' in:\n{run.Output}");
        return line.TrimEnd('|', ' ').Split('|')[^1].Trim();
    }

    private static byte[] SilentWav(int seconds)
    {
        const int rate = 16000;
        var data = new byte[seconds * rate * 2];
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + data.Length);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(data.Length);
            w.Write(data);
        }

        return stream.ToArray();
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "check-recording.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("scripts/check-recording.ps1 not found above " + AppContext.BaseDirectory);
    }

    private static string? FindPwsh()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MURMUR_PWSH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
