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
        // 20 frames over 10 s = 2 fps: the duration is right, the frame count is not.
        var slow = Run(["screen-000.mp4"], [Video(10, 2)]);
        Assert.Equal(1, slow.ExitCode);
        Assert.Equal("FAIL", Status(slow, "screen-000.mp4 video"));
        Assert.Equal("PASS", Status(slow, "screen video duration vs durationS"));

        Assert.Equal(0, Run(["screen-000.mp4"], [Video(10, 2)], "-MinScreenFps", "0").ExitCode);
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

    private ScriptRun Run(string[] names, byte[][] files, string? option = null, string? value = null, bool expectScreen = true)
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
              "wsAvgMb": 0.0, "wsMaxMb": 0.0, "privateMaxMb": 0.0,
              "recorderStats": { "microphoneFrames": 0, "systemFrames": 160000, "droppedFrames": 0, "screenFramesDropped": 0 },
              "session": { "id": "test", "folder": "{{folder}}", "micSeconds": 0.0, "systemSeconds": {{DurationS}}, "screenSegments": {{names.Length}} } }
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

        if (option is not null)
        {
            info.ArgumentList.Add(option);
            info.ArgumentList.Add(value!);
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
