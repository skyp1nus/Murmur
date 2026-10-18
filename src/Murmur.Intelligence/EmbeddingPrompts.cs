namespace Murmur.Intelligence;

/// <summary>
/// Task prompts EmbeddingGemma was trained with: queries and documents are embedded with different prefixes so that
/// a short question lands near the passage that answers it.
/// </summary>
public static class EmbeddingPrompts
{
    /// <summary>Prompt for a search query: <c>task: search result | query: {query}</c>.</summary>
    public static string Query(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return "task: search result | query: " + query;
    }

    /// <summary>Prompt for a document: <c>title: {title or none} | text: {text}</c>.</summary>
    public static string Document(string text, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "title: " + (string.IsNullOrWhiteSpace(title) ? "none" : title) + " | text: " + text;
    }
}
