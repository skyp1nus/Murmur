using System.Diagnostics;
using System.Globalization;
using Murmur.Core.Audio;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// Kills a real child process (<c>Murmur.Core.CrashHelper</c>) while it records, then recovers the session the way
/// the app does on its next start.
/// </summary>
[Collection(RealtimeCollection.Name)]
public sealed class CrashTests : IDisposable
{
    private static readonly TimeSpan RecordFor = TimeSpan.FromSeconds(5.3);
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task KilledWhileRecording_LosesAtMostTheLastFlushInterval()
    {
        var ct = TestContext.Current.CancellationToken;
        var helper = Path.Combine(AppContext.BaseDirectory, "Murmur.Core.CrashHelper.dll");
        Assert.True(File.Exists(helper), $"{helper} is missing");

        var start = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add(_dir.Path);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the helper");
        var watch = new Stopwatch();
        SessionId? sessionId = null;
        var lastElapsed = 0.0;
        var lastElapsedAt = TimeSpan.Zero;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                    ?? throw new InvalidOperationException("helper exited: " + await process.StandardError.ReadToEndAsync(ct));
                if (line.StartsWith("SESSION ", StringComparison.Ordinal))
                {
                    sessionId = SessionId.Parse(line["SESSION ".Length..]);
                    watch.Start();
                }
                else if (line.StartsWith("ELAPSED ", StringComparison.Ordinal))
                {
                    lastElapsed = double.Parse(line["ELAPSED ".Length..], CultureInfo.InvariantCulture);
                    lastElapsedAt = watch.Elapsed;
                    if (lastElapsed >= RecordFor.TotalSeconds)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
        }

        // The recorder had been running at least this long when it died.
        var elapsedAtKill = lastElapsed + (watch.Elapsed - lastElapsedAt).TotalSeconds;
        Assert.NotNull(sessionId);

        var store = new InMemorySessionStore(_dir.Path);
        store.Seed(new SessionRecord { Id = sessionId.Value, Title = "crash test", StartedAt = DateTimeOffset.Now, State = SessionState.Recording });
        var files = store.GetFiles(sessionId.Value);

        var recovered = Assert.Single(await new RecoveryService(store).RecoverAsync(ct));

        Assert.Equal(SessionState.Interrupted, recovered.State);
        Assert.True(recovered.HasMicrophone);
        Assert.True(recovered.HasSystemAudio);
        Assert.True(
            recovered.Duration.TotalSeconds >= elapsedAtKill - 2.5,
            $"recovered {recovered.Duration.TotalSeconds:F3} s of {elapsedAtKill:F3} s");
        Assert.True(recovered.Duration.TotalSeconds <= elapsedAtKill + 0.1);
        TestContext.Current.TestOutputHelper?.WriteLine($"killed at {elapsedAtKill:F3} s, recovered {recovered.Duration.TotalSeconds:F3} s");

        // Both files are valid WAVs whose header now matches the data, and the coded signal is intact.
        foreach (var path in new[] { files.MicrophoneAudio, files.SystemAudio })
        {
            using var reader = WavReader.Open(path);
            Assert.Equal(new FileInfo(path).Length, reader.Layout.DataOffset + reader.Layout.DeclaredDataBytes);
            Assert.Equal(reader.Layout.DeclaredDataBytes / 2, reader.FrameCount);
        }

        var (_, system) = WavReader.ReadAll(files.SystemAudio);
        for (var b = 0; b + 1 < system.Length / SyntheticSignals.BlockSamples16k; b++)
        {
            Assert.True(SyntheticSignals.TryDecodeSystemBlock(system.AsSpan(b * SyntheticSignals.BlockSamples16k), out var index));
            Assert.Equal(b, index);
        }
    }

    /// <summary>
    /// A crash while paused leaves a normal interrupted session: the files hold what was recorded before the pause (the
    /// header was current, the data complete), nothing from the paused time, and recovery needs nothing extra.
    /// </summary>
    [Fact]
    public async Task KilledWhilePaused_RecoversWhatWasRecordedBeforeThePause()
    {
        var ct = TestContext.Current.CancellationToken;
        var helper = Path.Combine(AppContext.BaseDirectory, "Murmur.Core.CrashHelper.dll");
        Assert.True(File.Exists(helper), $"{helper} is missing");

        var start = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add(_dir.Path);
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("1.5");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the helper");
        SessionId? sessionId = null;
        var pausedAt = -1.0;
        var elapsedAfterPause = 0.0;
        var linesAfterPause = 0;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                    ?? throw new InvalidOperationException("helper exited: " + await process.StandardError.ReadToEndAsync(ct));
                if (line.StartsWith("SESSION ", StringComparison.Ordinal))
                {
                    sessionId = SessionId.Parse(line["SESSION ".Length..]);
                }
                else if (line.StartsWith("PAUSED ", StringComparison.Ordinal))
                {
                    pausedAt = double.Parse(line["PAUSED ".Length..], CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("ELAPSED ", StringComparison.Ordinal) && pausedAt >= 0)
                {
                    elapsedAfterPause = double.Parse(line["ELAPSED ".Length..], CultureInfo.InvariantCulture);

                    // Three seconds of pause: longer than the flush interval, so headers and disk are current.
                    if (++linesAfterPause >= 30)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
        }

        Assert.NotNull(sessionId);
        Assert.Equal(pausedAt, elapsedAfterPause, 0.001); // the timeline stood still

        var store = new InMemorySessionStore(_dir.Path);
        store.Seed(new SessionRecord { Id = sessionId.Value, Title = "crash test", StartedAt = DateTimeOffset.Now, State = SessionState.Recording });
        var files = store.GetFiles(sessionId.Value);

        var recovered = Assert.Single(await new RecoveryService(store).RecoverAsync(ct));

        Assert.Equal(SessionState.Interrupted, recovered.State);
        Assert.True(recovered.HasMicrophone);
        Assert.True(recovered.HasSystemAudio);
        Assert.InRange(recovered.Duration.TotalSeconds, pausedAt - 0.1, pausedAt + 0.1);
        foreach (var path in new[] { files.MicrophoneAudio, files.SystemAudio })
        {
            using var reader = WavReader.Open(path);
            Assert.Equal(new FileInfo(path).Length, reader.Layout.DataOffset + reader.Layout.DeclaredDataBytes);
            Assert.Equal(recovered.Duration.TotalSeconds, (double)reader.FrameCount / reader.Format.SampleRate, 0.05);
        }
    }

    private static string DotnetHost()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
        {
            return host;
        }

        var process = Environment.ProcessPath;
        if (process is not null && Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return process;
        }

        return "dotnet";
    }
}
