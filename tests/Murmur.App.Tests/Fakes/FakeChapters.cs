using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary>Scriptable <see cref="IChapterGenerator"/>: answers at once with <see cref="Result"/>, throws <see cref="Failure"/>, or waits for <see cref="Gate"/>.</summary>
internal sealed class FakeChapterGenerator : IChapterGenerator
{
    public int Calls { get; private set; }

    public TimeSpan MinimumDuration { get; set; } = TimeSpan.FromMinutes(10);

    public Exception? Failure { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public double[] ProgressSteps { get; set; } = [];

    public IReadOnlyList<Chapter> Result { get; set; } = Sample;

    public static IReadOnlyList<Chapter> Sample { get; } =
    [
        new("Intro", TimeSpan.Zero),
        new("Roadmap & search", TimeSpan.FromSeconds(250)),
        new("PDF export", TimeSpan.FromSeconds(735)),
        new("Next steps & pricing", TimeSpan.FromSeconds(1470)),
    ];

    public async ValueTask<IReadOnlyList<Chapter>> GenerateChaptersAsync(SessionId session, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls++;
        foreach (var step in ProgressSteps)
        {
            progress?.Report(step);
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return Result;
    }
}

/// <summary>An in-memory <see cref="IChapterStore"/> that records what was saved.</summary>
internal sealed class FakeChapterStore : IChapterStore
{
    private readonly Dictionary<SessionId, IReadOnlyList<Chapter>> _chapters = [];

    public List<(SessionId Session, IReadOnlyList<Chapter> Chapters)> Saved { get; } = [];

    public Exception? GetFailure { get; set; }

    public Exception? SaveFailure { get; set; }

    public void Put(SessionId session, IReadOnlyList<Chapter> chapters) => _chapters[session] = chapters;

    public ValueTask SaveChaptersAsync(SessionId session, IReadOnlyList<Chapter> chapters, CancellationToken cancellationToken)
    {
        if (SaveFailure is { } failure)
        {
            throw failure;
        }

        _chapters[session] = chapters;
        Saved.Add((session, chapters));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Chapter>> GetChaptersAsync(SessionId session, CancellationToken cancellationToken)
    {
        if (GetFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(_chapters.GetValueOrDefault(session) ?? []);
    }
}
