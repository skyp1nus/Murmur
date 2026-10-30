using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Sessions;

/// <summary>
/// The export formatters against golden files (<c>Golden/</c>). Set <c>MURMUR_UPDATE_GOLDEN=1</c> to rewrite them from the current
/// output (then review the diff).
/// </summary>
public sealed class TranscriptExportTests
{
    private static readonly SessionId Id = SessionId.Parse("0198a3b2c4d57e6f8091a2b3c4d5e6f7");

    private static readonly SessionRecord Session = new()
    {
        Id = Id,
        Title = "Client demo — Q4 roadmap",
        StartedAt = new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero),
        Duration = new TimeSpan(0, 47, 12),
        State = SessionState.Ready,
    };

    private static TranscriptSegment Segment(Speaker speaker, double startSeconds, double endSeconds, string text) =>
        new(speaker, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), text, []);

    private static readonly Transcript Transcript = new(
        Id,
        [
            Segment(Speaker.Me, 712.4, 727, "Okay, a quick run through the roadmap."),
            Segment(Speaker.Others, 728, 740.25, "Does search match <exact> words & phrases,\nor can I ask in my own words?"),
            Segment(Speaker.Me, 741, 741, "Both."),
            Segment(Speaker.Others, 3725.5, 3730, "One hour later."),
        ],
        "parakeet-tdt-0.6b-v3-int8",
        "en");

    private static readonly SessionSummary Summary = new(
        Id,
        "Walked the client through the roadmap. PDF export moved to next sprint.",
        [new SummaryItem("PDF export — next sprint", TimeSpan.FromSeconds(772)), new SummaryItem("Library search is in progress", null)],
        [new SummaryItem("You — send the demo invite", new TimeSpan(1, 2, 5))],
        "gemma-4-E4B-it-Q4_K_M",
        new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ToMarkdown_WithSummary_MatchesTheGoldenFile() =>
        Golden("with-summary.md", TranscriptExport.ToMarkdown(Session, Transcript, Summary));

    [Fact]
    public void ToMarkdown_WithoutSummary_HasNoSummarySection() =>
        Golden("without-summary.md", TranscriptExport.ToMarkdown(Session, Transcript));

    [Fact]
    public void ToPlainText_MatchesTheGoldenFile() =>
        Golden("transcript.txt", TranscriptExport.ToPlainText(Session, Transcript));

    [Fact]
    public void ToSrt_MatchesTheGoldenFile() =>
        Golden("transcript.srt", TranscriptExport.ToSrt(Session, Transcript));

    [Fact]
    public void ToWebVtt_MatchesTheGoldenFile() =>
        Golden("transcript.vtt", TranscriptExport.ToWebVtt(Session, Transcript));

    [Fact]
    public void Speakers_UseTheLabelsOfTheCaller()
    {
        var labels = new ExportLabels("Я", "Інші", "Підсумок", "Огляд", "Ключові пункти", "Завдання", "Транскрипт");

        var markdown = TranscriptExport.ToMarkdown(Session, Transcript, Summary, labels);

        Assert.Contains("**Я** `11:52`", markdown);
        Assert.Contains("**Інші** `12:08`", markdown);
        Assert.Contains("## Підсумок", markdown);
        Assert.Contains("### Огляд", markdown);
        Assert.Contains("### Ключові пункти", markdown);
        Assert.Contains("### Завдання", markdown);
        Assert.Contains("## Транскрипт", markdown);
        Assert.Contains("Я: Okay", TranscriptExport.ToSrt(Session, Transcript, labels));
        Assert.Contains("<v Інші>", TranscriptExport.ToWebVtt(Session, Transcript, labels));
        Assert.DoesNotContain("You", TranscriptExport.ToPlainText(Session, Transcript, labels));
    }

    [Fact]
    public void Output_UsesLineFeedsOnly_AndTheInvariantCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
        try
        {
            foreach (var format in Enum.GetValues<ExportFormat>())
            {
                var text = TranscriptExport.Format(format, Session, Transcript, Summary);

                Assert.DoesNotContain('\r', text);
                Assert.Matches("[0-9]", text);
                Assert.False(text.Contains('\u0661'), "Arabic-Indic digit");
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Srt_CueEndsAfterItsStart_EvenForAZeroLengthSegment()
    {
        var srt = TranscriptExport.ToSrt(Session, Transcript);

        Assert.Contains("00:12:21,000 --> 00:12:22,000", srt);
    }

    [Fact]
    public void Srt_NumbersTheCuesFromOne_AndKeepsMilliseconds()
    {
        var srt = TranscriptExport.ToSrt(Session, Transcript);

        Assert.StartsWith("1\n00:11:52,400 --> 00:12:07,000\n", srt);
        Assert.Contains("\n4\n01:02:05,500 --> 01:02:10,000\n", srt);
    }

    [Fact]
    public void WebVtt_EscapesMarkupInTheCueText_AndStartsWithTheHeader()
    {
        var vtt = TranscriptExport.ToWebVtt(Session, Transcript);

        Assert.StartsWith("WEBVTT - Client demo — Q4 roadmap\n\n", vtt);
        Assert.Contains("&lt;exact&gt; words &amp; phrases, or can I ask", vtt);
        Assert.DoesNotContain("<exact>", vtt);
    }

    [Fact]
    public void Markdown_ShowsActionItemsAsOpenCheckboxes_AndCitedTimes()
    {
        var markdown = TranscriptExport.ToMarkdown(Session, Transcript, Summary);

        Assert.Contains("- [ ] You — send the demo invite (`1:02:05`)", markdown);
        Assert.Contains("- PDF export — next sprint (`12:52`)", markdown);
        Assert.Contains("- Library search is in progress\n", markdown);
    }

    [Fact]
    public void AnUntitledSession_GetsTheLabelOfTheCaller()
    {
        var untitled = Session with { Title = "  " };

        Assert.StartsWith("# Untitled recording\n", TranscriptExport.ToMarkdown(untitled, Transcript));
        Assert.StartsWith("# Без назви\n", TranscriptExport.ToMarkdown(untitled, Transcript, labels: ExportLabels.English with { Untitled = "Без назви" }));
    }

    [Fact]
    public void Srt_DropsAngleBrackets_SoPlayersDoNotReadMarkup()
    {
        var srt = TranscriptExport.ToSrt(Session, Transcript);

        Assert.Contains("Others: Does search match exact words & phrases, or can I ask in my own words?", srt);
        Assert.DoesNotContain('<', srt);
        Assert.DoesNotContain('>', srt.Replace("-->", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void WebVtt_TheTitleAndTheCuesCannotLookLikeATiming()
    {
        var arrow = Session with { Title = "Plan --> do" };
        var spoken = Transcript with { Segments = [Segment(Speaker.Me, 1, 2, "Left --> right")] };

        var vtt = TranscriptExport.ToWebVtt(arrow, spoken);

        Assert.StartsWith("WEBVTT - Plan -> do\n\n", vtt);
        Assert.Contains("Left --&gt; right", vtt);
        Assert.Single(vtt.Split('\n'), line => line.Contains("-->", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyTranscript_StillFormats()
    {
        var empty = Transcript with { Segments = [] };

        Assert.Equal("WEBVTT - Client demo — Q4 roadmap\n\n", TranscriptExport.ToWebVtt(Session, empty));
        Assert.Equal(string.Empty, TranscriptExport.ToSrt(Session, empty));
        Assert.EndsWith("## Transcript\n", TranscriptExport.ToMarkdown(Session, empty));
    }

    [Theory]
    [InlineData(ExportFormat.Markdown, ".md")]
    [InlineData(ExportFormat.PlainText, ".txt")]
    [InlineData(ExportFormat.Srt, ".srt")]
    [InlineData(ExportFormat.WebVtt, ".vtt")]
    public void Extension_MatchesTheFormat(ExportFormat format, string extension) =>
        Assert.Equal(extension, TranscriptExport.Extension(format));

    private static void Golden(string name, string actual)
    {
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "Sessions", "Golden", name);
        if (Environment.GetEnvironmentVariable("MURMUR_UPDATE_GOLDEN") == "1")
        {
            // Writes into the source tree: the output folder is five levels below the test project.
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Sessions", "Golden", name));
            File.WriteAllText(source, actual, new System.Text.UTF8Encoding(false));
            return;
        }

        Assert.Equal(File.ReadAllText(expectedPath), actual);
    }
}
