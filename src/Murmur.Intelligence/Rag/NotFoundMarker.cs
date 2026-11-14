using System.Text;

namespace Murmur.Intelligence.Rag;

/// <summary>
/// Watches the start of a streamed answer for the explicit "nothing found" marker (<see cref="Assistant.NotFoundMarker"/>). The prompt asks the
/// model to open such an answer with the marker; the filter strips it before the text reaches the UI (holding back only a prefix that may still
/// grow into the marker) and remembers that the model found nothing, so the answer gets no citations.
/// </summary>
internal sealed class NotFoundMarker
{
    private readonly StringBuilder _held = new();
    private bool _decided;

    /// <summary>The answer opened with the marker: the model found nothing relevant in the excerpts.</summary>
    public bool Found { get; private set; }

    /// <summary>Takes the next piece of the stream; returns what is safe to show now (may be empty while the start is undecided).</summary>
    public string Push(string delta)
    {
        if (_decided)
        {
            return delta;
        }

        _held.Append(delta);
        var text = _held.ToString().TrimStart();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var marker = Assistant.NotFoundMarker;
        if (text.Length >= marker.Length)
        {
            if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                Found = true;
                text = text[marker.Length..].TrimStart();
            }
            else
            {
                text = _held.ToString();
            }

            _decided = true;
            _held.Clear();
            return text;
        }

        if (marker.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        text = _held.ToString();
        _decided = true;
        _held.Clear();
        return text;
    }

    /// <summary>The end of the stream: what is still held back (a short answer that merely begins like the marker).</summary>
    public string Finish()
    {
        if (_decided)
        {
            return string.Empty;
        }

        _decided = true;
        var rest = _held.ToString();
        _held.Clear();
        return rest;
    }
}
