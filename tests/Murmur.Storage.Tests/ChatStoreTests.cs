using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.Storage.Tests;

public sealed class ChatStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _root = new();
    private readonly List<SqliteStorage> _opened = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var storage in _opened)
        {
            storage.Dispose();
        }

        _root.Dispose();
    }

    private SqliteStorage Open()
    {
        var storage = _root.Open();
        _opened.Add(storage);
        return storage;
    }

    private static ChatMessage Message(ChatThreadId thread, int index, TurnRole role, string text, DateTimeOffset at, params Citation[] citations) =>
        new(thread, index, role, text, citations, at);

    [Fact]
    public async Task CreateThread_StoresTitleAndScope_AndListsItWithNoMessages()
    {
        var storage = Open();
        var session = await storage.CreateSessionAsync("Client demo", T0, Ct);

        var all = await storage.CreateThreadAsync("PDF export", null, Ct);
        var scoped = await storage.CreateThreadAsync("Invite", session.Id, Ct);

        Assert.Equal("PDF export", all.Title);
        Assert.Null(all.Scope);
        Assert.Equal(session.Id, scoped.Scope);
        Assert.NotEqual(all.Id, scoped.Id);
        var listed = await storage.ListThreadsAsync(10, Ct);
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, t => t.Id == scoped.Id && t.Scope == session.Id);
        Assert.Empty(await storage.GetMessagesAsync(all.Id, Ct));
    }

    [Fact]
    public async Task CreateThread_ForAnUnknownSession_Throws()
    {
        var storage = Open();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.CreateThreadAsync("x", SessionId.New(), Ct));
    }

    [Fact]
    public async Task ListThreads_NewestUpdateFirst_AndAMessageMovesAThreadToTheTop()
    {
        var storage = Open();
        var first = await storage.CreateThreadAsync("first", null, Ct);
        await Task.Delay(5, Ct);
        var second = await storage.CreateThreadAsync("second", null, Ct);
        Assert.Equal([second.Id, first.Id], (await storage.ListThreadsAsync(10, Ct)).Select(t => t.Id));

        await storage.AppendMessageAsync(Message(first.Id, 0, TurnRole.User, "hello", DateTimeOffset.UtcNow.AddMinutes(1)), Ct);

        Assert.Equal([first.Id, second.Id], (await storage.ListThreadsAsync(10, Ct)).Select(t => t.Id));
        Assert.Single(await storage.ListThreadsAsync(1, Ct));
        Assert.Empty(await storage.ListThreadsAsync(0, Ct));
    }

    [Fact]
    public async Task Messages_AreReturnedInOrder_WithRolesTextAndCitations()
    {
        var storage = Open();
        var session = await storage.CreateSessionAsync("Client demo", T0, Ct);
        var thread = await storage.CreateThreadAsync("t", null, Ct);
        var citation = new Citation(session.Id, TimeSpan.FromSeconds(772), "12:52", "“PDF export moves to next sprint…”");

        await storage.AppendMessageAsync(Message(thread.Id, 0, TurnRole.User, "What did we decide?", T0), Ct);
        await storage.AppendMessageAsync(Message(thread.Id, 1, TurnRole.Assistant, "Next sprint [12:52].", T0.AddSeconds(3), citation), Ct);

        var messages = await storage.GetMessagesAsync(thread.Id, Ct);
        Assert.Equal(2, messages.Count);
        Assert.Equal((0, TurnRole.User, "What did we decide?"), (messages[0].Index, messages[0].Role, messages[0].Text));
        Assert.Empty(messages[0].Citations);
        Assert.Equal((1, TurnRole.Assistant), (messages[1].Index, messages[1].Role));
        Assert.Equal(citation, Assert.Single(messages[1].Citations));
        Assert.Equal(T0.AddSeconds(3).ToUnixTimeMilliseconds(), messages[1].At.ToUnixTimeMilliseconds());
        Assert.All(messages, m => Assert.Equal(thread.Id, m.Thread));
    }

    [Fact]
    public async Task Messages_SurviveReopeningTheDatabase()
    {
        var thread = await Open().CreateThreadAsync("kept", null, Ct);
        await Open().AppendMessageAsync(Message(thread.Id, 0, TurnRole.User, "still here", T0), Ct);

        var reopened = _root.Open();
        _opened.Add(reopened);

        Assert.Equal("kept", Assert.Single(await reopened.ListThreadsAsync(10, Ct)).Title);
        Assert.Equal("still here", Assert.Single(await reopened.GetMessagesAsync(thread.Id, Ct)).Text);
    }

    [Fact]
    public async Task AppendMessage_AtAnExistingIndex_ReplacesItAndDropsTheLaterOnes()
    {
        var storage = Open();
        var thread = await storage.CreateThreadAsync("t", null, Ct);
        await storage.AppendMessageAsync(Message(thread.Id, 0, TurnRole.User, "q1", T0), Ct);
        await storage.AppendMessageAsync(Message(thread.Id, 1, TurnRole.Assistant, "a1", T0), Ct);
        await storage.AppendMessageAsync(Message(thread.Id, 2, TurnRole.User, "q2", T0), Ct);
        await storage.AppendMessageAsync(Message(thread.Id, 3, TurnRole.Assistant, "a2", T0), Ct);

        await storage.AppendMessageAsync(Message(thread.Id, 3, TurnRole.Assistant, "a2 again", T0.AddMinutes(1)), Ct);
        Assert.Equal(["q1", "a1", "q2", "a2 again"], (await storage.GetMessagesAsync(thread.Id, Ct)).Select(m => m.Text));

        await storage.AppendMessageAsync(Message(thread.Id, 1, TurnRole.Assistant, "a1 again", T0.AddMinutes(2)), Ct);
        Assert.Equal(["q1", "a1 again"], (await storage.GetMessagesAsync(thread.Id, Ct)).Select(m => m.Text));
    }

    [Fact]
    public async Task AppendMessage_ToAnUnknownThread_OrPastTheEnd_Throws()
    {
        var storage = Open();
        var thread = await storage.CreateThreadAsync("t", null, Ct);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.AppendMessageAsync(Message(ChatThreadId.New(), 0, TurnRole.User, "x", T0), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.AppendMessageAsync(Message(thread.Id, 1, TurnRole.User, "gap", T0), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.AppendMessageAsync(Message(thread.Id, -1, TurnRole.User, "neg", T0), Ct));
        Assert.Empty(await storage.GetMessagesAsync(thread.Id, Ct));
    }

    [Fact]
    public async Task RenameThread_ChangesTheTitleOnly_AndAnUnknownThreadThrows()
    {
        var storage = Open();
        var older = await storage.CreateThreadAsync("older", null, Ct);
        await Task.Delay(5, Ct);
        var newer = await storage.CreateThreadAsync("newer", null, Ct);

        await storage.RenameThreadAsync(older.Id, "Renamed", Ct);

        var listed = await storage.ListThreadsAsync(10, Ct);
        Assert.Equal([newer.Id, older.Id], listed.Select(t => t.Id));
        Assert.Equal("Renamed", listed[1].Title);
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.RenameThreadAsync(ChatThreadId.New(), "x", Ct));
    }

    [Fact]
    public async Task DeleteThread_RemovesItsMessages_AndIgnoresAnUnknownThread()
    {
        var storage = Open();
        var gone = await storage.CreateThreadAsync("gone", null, Ct);
        var kept = await storage.CreateThreadAsync("kept", null, Ct);
        await storage.AppendMessageAsync(Message(gone.Id, 0, TurnRole.User, "q", T0), Ct);
        await storage.AppendMessageAsync(Message(kept.Id, 0, TurnRole.User, "q", T0), Ct);

        await storage.DeleteThreadAsync(gone.Id, Ct);
        await storage.DeleteThreadAsync(ChatThreadId.New(), Ct);

        Assert.Equal(kept.Id, Assert.Single(await storage.ListThreadsAsync(10, Ct)).Id);
        Assert.Empty(await storage.GetMessagesAsync(gone.Id, Ct));
        Assert.Single(await storage.GetMessagesAsync(kept.Id, Ct));
    }

    [Fact]
    public async Task DeleteSession_DeletesTheThreadsScopedToIt_WithTheirMessages_AndKeepsTheOthers()
    {
        var storage = Open();
        var doomed = await storage.CreateSessionAsync("doomed", T0, Ct);
        var other = await storage.CreateSessionAsync("other", T0.AddHours(1), Ct);
        var scopedToDoomed = await storage.CreateThreadAsync("about doomed", doomed.Id, Ct);
        var scopedToOther = await storage.CreateThreadAsync("about other", other.Id, Ct);
        var library = await storage.CreateThreadAsync("library", null, Ct);
        foreach (var thread in new[] { scopedToDoomed, scopedToOther, library })
        {
            await storage.AppendMessageAsync(Message(thread.Id, 0, TurnRole.User, "q", T0), Ct);
        }

        await storage.DeleteSessionAsync(doomed.Id, Ct);

        var left = (await storage.ListThreadsAsync(10, Ct)).Select(t => t.Id).ToHashSet();
        Assert.DoesNotContain(scopedToDoomed.Id, left);
        Assert.Contains(scopedToOther.Id, left);
        Assert.Contains(library.Id, left);
        Assert.Empty(await storage.GetMessagesAsync(scopedToDoomed.Id, Ct));
        Assert.Single(await storage.GetMessagesAsync(scopedToOther.Id, Ct));
    }

    [Fact]
    public async Task DeleteSession_LeavesTheCitationsOfAnswersInOtherThreads_AsText()
    {
        var storage = Open();
        var doomed = await storage.CreateSessionAsync("doomed", T0, Ct);
        var thread = await storage.CreateThreadAsync("library", null, Ct);
        var citation = new Citation(doomed.Id, TimeSpan.FromSeconds(31 * 60) + TimeSpan.FromSeconds(5), "31:05", "“…the designer first.”");
        await storage.AppendMessageAsync(Message(thread.Id, 0, TurnRole.Assistant, "Review the template [31:05].", T0, citation), Ct);

        await storage.DeleteSessionAsync(doomed.Id, Ct);

        var message = Assert.Single(await storage.GetMessagesAsync(thread.Id, Ct));
        Assert.Equal("Review the template [31:05].", message.Text);
        Assert.Equal(citation, Assert.Single(message.Citations));
        Assert.Null(await storage.GetSessionAsync(doomed.Id, Ct));
    }
}
