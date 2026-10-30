using Murmur.Core.Sessions;

namespace Murmur.Core.Intelligence;

/// <summary>Identifies a saved chat thread: an opaque string (a time-ordered GUID in the "N" format when made with <see cref="New"/>).</summary>
public readonly record struct ChatThreadId(string Value)
{
    /// <summary>Creates a time-ordered id (UUIDv7), so ids sort by creation time.</summary>
    public static ChatThreadId New() => new(Guid.CreateVersion7().ToString("N"));

    public override string ToString() => Value;
}

/// <summary>A saved conversation of Ask AI.</summary>
/// <param name="Id">The thread.</param>
/// <param name="Title">The name shown in the sidebar: the first question until the user renames the thread.</param>
/// <param name="Scope">The one recording the questions are about, or <c>null</c> for the whole library. Fixed when the thread is created.</param>
/// <param name="UpdatedAt">When the last message was added (or the thread was renamed or created): the list is ordered by it, newest first.</param>
public sealed record ChatThread(ChatThreadId Id, string Title, SessionId? Scope, DateTimeOffset UpdatedAt);

/// <summary>One saved message of a thread.</summary>
/// <param name="Thread">The thread it belongs to.</param>
/// <param name="Index">Position in the thread, from 0: questions and answers alternate.</param>
/// <param name="Role">Whose message it is.</param>
/// <param name="Text">The question, or the answer as the model wrote it (with its <c>[12:34]</c> time markers).</param>
/// <param name="Citations">The moments an answer refers to (empty for a question). A citation of a deleted recording stays here but no longer leads anywhere.</param>
/// <param name="At">When it was saved.</param>
public sealed record ChatMessage(ChatThreadId Thread, int Index, TurnRole Role, string Text, IReadOnlyList<Citation> Citations, DateTimeOffset At);
