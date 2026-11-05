using Murmur.Core.Sessions;

namespace Murmur.Core.Intelligence;

public interface IChapterGenerator
{
    /// <summary>
    /// Splits the stored transcript of <paramref name="session"/> into chapters (does not store them). Returns an empty list for
    /// a session shorter than <see cref="MinimumDuration"/>. Throws like ISessionSummarizer (no transcript / no speech:
    /// InvalidOperationException; unusable model answer: InvalidDataException; cancelled: OperationCanceledException).
    /// </summary>
    ValueTask<IReadOnlyList<Chapter>> GenerateChaptersAsync(SessionId session, IProgress<double>? progress, CancellationToken cancellationToken);

    TimeSpan MinimumDuration { get; }
}
