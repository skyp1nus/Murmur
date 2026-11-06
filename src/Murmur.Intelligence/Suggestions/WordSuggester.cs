using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Summary;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Murmur.Intelligence.Suggestions;

/// <summary>
/// <see cref="IWordSuggester"/> over the local chat model (#58): one short, non-streaming request with the sentence of the uncertain word
/// (its segment) and the word itself; the model answers up to three replacements, one per line. The answer is read leniently: bullets,
/// numbering, quotes, markdown, "a → b" and comma-separated lists are all understood, and anything that is not a word or a short phrase
/// (or is the word itself) is dropped. Without the models, or while a recording runs (<see cref="AiPausedException"/>), the list is empty.
/// </summary>
public sealed partial class WordSuggester : IWordSuggester
{
    /// <summary>At most this many replacements.</summary>
    public const int MaxSuggestions = 3;

    /// <summary>A replacement longer than this (characters) is not a word fix.</summary>
    internal const int MaxSuggestionLength = 40;

    /// <summary>A replacement of more words than this is not a short phrase.</summary>
    internal const int MaxSuggestionWords = 4;

    private readonly ILlm _llm;
    private readonly float _temperature;
    private readonly int _maxOutputTokens;

    public WordSuggester(ILlm llm, float temperature = 0.2f, int maxOutputTokens = 64)
    {
        ArgumentNullException.ThrowIfNull(llm);
        _llm = llm;
        _temperature = temperature;
        _maxOutputTokens = maxOutputTokens;
    }

    public async ValueTask<IReadOnlyList<string>> SuggestAsync(Transcript transcript, int segmentIndex, int wordIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segmentIndex, transcript.Segments.Count);
        var segment = transcript.Segments[segmentIndex];
        ArgumentOutOfRangeException.ThrowIfNegative(wordIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(wordIndex, segment.Words.Count);
        cancellationToken.ThrowIfCancellationRequested();

        var word = Core(segment.Words[wordIndex].Text);
        if (word.Length == 0 || _llm.Status == LlmStatus.NotInstalled)
        {
            return [];
        }

        IChatClient chat;
        string answer;
        try
        {
            chat = await _llm.GetChatClientAsync(cancellationToken).ConfigureAwait(false);
            var options = new ChatOptions
            {
                Temperature = _temperature,
                MaxOutputTokens = _maxOutputTokens,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
            };
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.System), new ChatMessage(ChatRole.User, Prompts.User(Sentence(segment, wordIndex), word, SessionSummarizer.LanguageName(transcript.Language)))],
                options,
                cancellationToken).ConfigureAwait(false);
            answer = response.Text;
        }
        catch (AiPausedException)
        {
            // A recording started: the models stay unloaded until it ends.
            return [];
        }

        return Parse(answer, word);
    }

    /// <summary>The words of the segment joined by spaces, the uncertain one between double brackets so the model knows which occurrence it is.</summary>
    internal static string Sentence(TranscriptSegment segment, int wordIndex)
    {
        if (segment.Words.Count == 0)
        {
            return segment.Text;
        }

        var builder = new StringBuilder(segment.Text.Length + 8);
        for (var i = 0; i < segment.Words.Count; i++)
        {
            var text = segment.Words[i].Text.AsSpan().Trim();
            if (text.IsEmpty)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            if (i == wordIndex)
            {
                builder.Append("[[").Append(text).Append("]]");
            }
            else
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The replacements in the answer, in its order: at most <see cref="MaxSuggestions"/>, none equal to <paramref name="word"/> or to an
    /// earlier one (any case), each at most <see cref="MaxSuggestionWords"/> words and <see cref="MaxSuggestionLength"/> characters.
    /// </summary>
    internal static List<string> Parse(string? answer, string word)
    {
        var result = new List<string>(MaxSuggestions);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return result;
        }

        foreach (var raw in answer.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = BulletPattern().Replace(raw.Trim(), string.Empty);
            if (line.Length == 0 || line.EndsWith(':'))
            {
                // A heading such as "Corrections:".
                continue;
            }

            // "parquet → Parakeet": the right side is the fix.
            var arrow = ArrowPattern().Match(line);
            if (arrow.Success)
            {
                line = line[(arrow.Index + arrow.Length)..];
            }

            // A label in front ("Answer: Parakeet") goes, and a one-line list ("Parakeet, parakeet model") is split.
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && colon < line.Length - 1)
            {
                line = line[(colon + 1)..];
            }

            foreach (var part in line.Split([',', ';', '/', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Clean(part);
                if (candidate.Length == 0 || candidate.Length > MaxSuggestionLength || WordCount(candidate) > MaxSuggestionWords
                    || string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase)
                    || result.Exists(r => string.Equals(r, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(candidate);
                if (result.Count == MaxSuggestions)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>The word without the punctuation around it ("parquet," → "parquet").</summary>
    internal static string Core(string text)
    {
        var span = text.AsSpan().Trim();
        var start = 0;
        var end = span.Length;
        while (start < end && !char.IsLetterOrDigit(span[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetterOrDigit(span[end - 1]))
        {
            end--;
        }

        return span[start..end].ToString();
    }

    /// <summary>Quotes, markdown and a trailing explanation in brackets removed, spaces collapsed, the punctuation around it gone.</summary>
    private static string Clean(string part)
    {
        var text = ExplanationPattern().Replace(part, string.Empty);
        text = text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        text = SpacesPattern().Replace(text, " ");
        return Core(text);
    }

    private static int WordCount(string text)
    {
        var count = 1;
        foreach (var c in text)
        {
            if (c == ' ')
            {
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex(@"^\s*(?:[-*•–]|\d{1,2}[.)])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"\s*(?:→|->|=>)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ArrowPattern();

    // "Parakeet (the model name)" or "Parakeet - the model": what follows the fix.
    [GeneratedRegex(@"\s*(?:\(.*\)|\s[-–—]\s.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplanationPattern();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SpacesPattern();

    private static class Prompts
    {
        public const string System =
            "You fix speech-recognition errors in meeting transcripts. The recogniser was unsure about one word, marked [[like this]]. " +
            "Reply with up to 3 words or short phrases the speaker most likely said instead, the most likely first, one per line, " +
            "in the language of the sentence, with no explanations. Prefer names, products and terms that fit the sentence.";

        public static string User(string sentence, string word, string language)
        {
            var builder = new StringBuilder(sentence.Length + 96);
            builder.Append("Sentence: ").Append(sentence).Append('\n');
            builder.Append("Uncertain word: ").Append(word);
            if (language.Length > 0)
            {
                builder.Append('\n').Append("Language: ").Append(language);
            }

            return builder.ToString();
        }
    }
}
