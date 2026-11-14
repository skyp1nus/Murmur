namespace Murmur.Intelligence.Rag;

/// <summary>
/// Recognizes an answer that says the recordings do not contain what was asked ("У записах цього немає", "not mentioned in the recording").
/// Such an answer gets no fallback citation of the top excerpt: a time chip would suggest the excerpt answers the question.
/// </summary>
public static class NotFoundAnswer
{
    // Lower-case phrases of "the recording does not say"; apostrophes are normalized to U+0027 first.
    private static readonly string[] Phrases =
    [
        "немає", "нема ", "нема,", "нема.", "не згаду", "не знайд", "не знайш", "не знайти", "не обговорю", "не обговорювал", "не йдеться",
        "не було", "відсутн", "не міст", "не вказ", "не сказано", "не прозвуча", "невідомо", "не можу знайти",
        "not mentioned", "no mention", "not found", "no information", "not in the", "isn't mentioned",
        "is not mentioned", "does not contain", "doesn't contain", "do not contain", "don't contain", "not discussed",
        "no record", "don't have", "do not have", "not available", "cannot find", "can't find", "couldn't find",
        "could not find", "not specified", "not stated", "doesn't say", "does not say", "no details",
    ];

    /// <summary>Simple UA/EN phrase check: does <paramref name="text"/> say the information is not in the recordings?</summary>
    public static bool IsNotFound(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace('’', '\'').Replace('ʼ', '\'').ToLowerInvariant() + " ";
        foreach (var phrase in Phrases)
        {
            if (normalized.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
