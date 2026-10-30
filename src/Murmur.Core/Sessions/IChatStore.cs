using Murmur.Core.Intelligence;

namespace Murmur.Core.Sessions;

/// <summary>Saved chats of Ask AI: threads and their messages.</summary>
public interface IChatStore
{
    /// <summary>The threads, most recently updated first; at most <paramref name="limit"/> (zero or less yields none).</summary>
    ValueTask<IReadOnlyList<ChatThread>> ListThreadsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an empty thread. <paramref name="scope"/> is the recording its questions are about, <c>null</c> = the whole library; a
    /// scope that does not exist throws <see cref="KeyNotFoundException"/>.
    /// </summary>
    ValueTask<ChatThread> CreateThreadAsync(string title, SessionId? scope, CancellationToken cancellationToken);

    /// <summary>The messages of a thread by <see cref="ChatMessage.Index"/>; empty for an unknown thread.</summary>
    ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(ChatThreadId thread, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="message"/> at its <see cref="ChatMessage.Index"/> and sets the thread's <see cref="ChatThread.UpdatedAt"/> to
    /// its <see cref="ChatMessage.At"/>. The index is the current message count (an append), or the index of the last message when both are
    /// answers: then the new answer replaces it (Regenerate). Anything else (a gap, a question where a message already is, a replacement of
    /// an older message) throws <see cref="InvalidOperationException"/> or <see cref="ArgumentOutOfRangeException"/>, so two writers on one
    /// thread cannot silently overwrite each other; an unknown thread throws <see cref="KeyNotFoundException"/>.
    /// </summary>
    ValueTask AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken);

    /// <summary>Changes the title and nothing else (the order of the list stays). An unknown thread throws <see cref="KeyNotFoundException"/>.</summary>
    ValueTask RenameThreadAsync(ChatThreadId thread, string title, CancellationToken cancellationToken);

    /// <summary>Deletes the thread and its messages; an unknown thread is ignored.</summary>
    ValueTask DeleteThreadAsync(ChatThreadId thread, CancellationToken cancellationToken);
}
