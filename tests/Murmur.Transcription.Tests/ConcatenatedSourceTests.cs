using Murmur.Transcription.Audio;

namespace Murmur.Transcription.Tests;

public sealed class ConcatenatedSourceTests
{
    private static readonly float[][] Clips = [Enumerable.Repeat(0.5f, 3_000).ToArray(), Enumerable.Repeat(-0.25f, 5_000).ToArray()];

    [Fact]
    public void ProducesExactlyTheRequestedLength()
    {
        using var source = new ConcatenatedSource(Clips, 123_457);

        Assert.Equal(123_457, TestWav.ReadAll(source, 4_096).Length);
    }

    [Fact]
    public void IsDeterministic_AndIndependentOfReadSize()
    {
        using var a = new ConcatenatedSource(Clips, 200_000);
        using var b = new ConcatenatedSource(Clips, 200_000);

        Assert.Equal(TestWav.ReadAll(a, 1_000), TestWav.ReadAll(b, 777));
    }

    [Fact]
    public void ClipsAlternateRoundRobin_WithSilencesOfHalfToOneAndAHalfSeconds()
    {
        using var source = new ConcatenatedSource(Clips, 1_000_000);
        var all = TestWav.ReadAll(source, 10_000);

        // Run-length encode the signal: clips are constant, silences are zeros.
        var runs = new List<(float Value, int Length)>();
        foreach (var s in all)
        {
            if (runs.Count > 0 && runs[^1].Value == s)
            {
                runs[^1] = (s, runs[^1].Length + 1);
            }
            else
            {
                runs.Add((s, 1));
            }
        }

        var silences = runs.Where(r => r.Value == 0f).Select(r => r.Length).ToArray();
        var clips = runs.Where(r => r.Value != 0f).ToArray();
        Assert.True(silences.Length > 10);
        Assert.All(silences[..^1], len => Assert.InRange(len, 8_000, 24_000));
        Assert.True(silences.Distinct().Count() > 1, "silences should vary");
        for (var i = 0; i < clips.Length - 1; i++)
        {
            Assert.Equal(i % 2 == 0 ? 0.5f : -0.25f, clips[i].Value);
            Assert.Equal(i % 2 == 0 ? 3_000 : 5_000, clips[i].Length);
        }
    }

    [Fact]
    public void RequiresAtLeastOneClip()
    {
        Assert.Throws<ArgumentException>(() => new ConcatenatedSource([], 10));
    }
}
