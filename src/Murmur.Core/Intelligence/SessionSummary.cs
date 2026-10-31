using Murmur.Core.Sessions;

namespace Murmur.Core.Intelligence;

/// <summary>One line of a summary: the text and, when the model pointed at a moment of the recording, that moment.</summary>
/// <param name="Text">Plain text in the language of the transcript, without the timestamp the model cited.</param>
/// <param name="At">
/// The start of the transcript segment the cited time falls in (the real time on the recording's timeline, like the assistant's
/// citations); <c>null</c> when the model cited nothing or a moment that is not in the transcript.
/// </param>
public sealed record SummaryItem(string Text, TimeSpan? At);

/// <summary>What the local model made of a recording: an overview, the key points and the action items.</summary>
/// <param name="ModelId">The model that wrote it (the id the chat client reports, e.g. a GGUF file name), shown as "Generated locally · …".</param>
/// <param name="CreatedAt">When it was generated.</param>
public sealed record SessionSummary(
    SessionId Session,
    string Overview,
    IReadOnlyList<SummaryItem> KeyPoints,
    IReadOnlyList<SummaryItem> ActionItems,
    string ModelId,
    DateTimeOffset CreatedAt);
