namespace Murmur.Intelligence.Tests;

public sealed class EmbeddingPromptsTests
{
    [Fact]
    public void Query_UsesTheEmbeddingGemmaSearchPrompt() =>
        Assert.Equal("task: search result | query: що з бюджетом?", EmbeddingPrompts.Query("що з бюджетом?"));

    [Fact]
    public void Document_UsesTheEmbeddingGemmaDocumentPrompt()
    {
        Assert.Equal("title: none | text: Я: привіт", EmbeddingPrompts.Document("Я: привіт"));
        Assert.Equal("title: none | text: x", EmbeddingPrompts.Document("x", "  "));
        Assert.Equal("title: Ретро | text: x", EmbeddingPrompts.Document("x", "Ретро"));
    }
}
