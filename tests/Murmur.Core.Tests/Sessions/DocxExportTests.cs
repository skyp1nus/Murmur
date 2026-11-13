using System.IO.Compression;
using System.Xml.Linq;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Sessions;

/// <summary>The hand-written .docx: a valid zip with the WordprocessingML parts, the Markdown export's content, escaped text, stable bytes.</summary>
public sealed class DocxExportTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly SessionId Id = SessionId.Parse("0198a3b2c4d57e6f8091a2b3c4d5e6f7");

    private static readonly SessionRecord Session = new()
    {
        Id = Id,
        Title = "Q4 <roadmap> & \"plans\" 'draft'",
        StartedAt = new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero),
        Duration = new TimeSpan(0, 47, 12),
        State = SessionState.Ready,
    };

    private static TranscriptSegment Segment(Speaker speaker, double start, double end, string text) =>
        new(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, []);

    private static readonly Transcript Transcript = new(
        Id,
        [
            Segment(Speaker.Me, 712, 727, "Does 1 < 2 && 3 > 2? \"Yes\", it's 'true'."),
            Segment(Speaker.Others, 728, 740, "Plain answer.\nOn two lines."),
            Segment(Speaker.Me, 741, 750, "Control \u0001 char and a lone \uD800 surrogate, and an emoji 😀."),
        ],
        "parakeet",
        "en");

    private static readonly SessionSummary Summary = new(
        Id,
        "Overview with <b> & more.",
        [new SummaryItem("Point one", TimeSpan.FromSeconds(75)), new SummaryItem("Point two", null)],
        [new SummaryItem("Send the invite", TimeSpan.FromSeconds(95))],
        "gemma",
        new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    private static byte[] Build(ExportOptions? options = null, ExportRange? range = null, SessionSummary? summary = null) =>
        TranscriptExport.ToDocx(Session, Transcript, summary ?? Summary, null, options ?? ExportOptions.Default, range);

    private static XDocument Part(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static List<(string Style, string Text)> Paragraphs(byte[] docx) =>
        Part(docx, "word/document.xml").Descendants(W + "p")
            .Select(p => (
                (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? string.Empty,
                string.Concat(p.Descendants().Where(e => e.Name == W + "t" || e.Name == W + "tab" || e.Name == W + "br")
                    .Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : "\n"))))
            .ToList();

    [Fact]
    public void TheFile_IsAZip_WithTheFourPartsAndTheRelationshipOfTheStyles()
    {
        var docx = Build();

        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(docx, 0, 2));
        Assert.Equal("[Content_Types].xml", names[0]);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("word/document.xml", names);
        Assert.Contains("word/styles.xml", names);
        Assert.Contains("word/_rels/document.xml.rels", names);
    }

    [Fact]
    public void EveryPart_IsWellFormedXml_AndTheTypesAndRelationshipsPointAtTheRightParts()
    {
        var docx = Build();

        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            Assert.NotNull(XDocument.Load(stream).Root);
        }

        var types = Part(docx, "[Content_Types].xml").Root!.Elements().ToList();
        Assert.Contains(types, e => (string?)e.Attribute("PartName") == "/word/document.xml"
            && ((string?)e.Attribute("ContentType"))!.EndsWith("wordprocessingml.document.main+xml", StringComparison.Ordinal));
        Assert.Contains(types, e => (string?)e.Attribute("PartName") == "/word/styles.xml"
            && ((string?)e.Attribute("ContentType"))!.EndsWith("wordprocessingml.styles+xml", StringComparison.Ordinal));
        Assert.Equal("word/document.xml", (string?)Part(docx, "_rels/.rels").Root!.Elements().Single().Attribute("Target"));
        var styles = Part(docx, "word/_rels/document.xml.rels").Root!.Elements().Single();
        Assert.Equal("styles.xml", (string?)styles.Attribute("Target"));
        Assert.EndsWith("/styles", (string?)styles.Attribute("Type"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStyles_DefineEveryStyleTheDocumentUses()
    {
        var docx = Build();

        var defined = Part(docx, "word/styles.xml").Descendants(W + "style").Select(s => (string?)s.Attribute(W + "styleId")).ToHashSet();
        var used = Paragraphs(docx).Select(p => p.Style).ToHashSet();
        Assert.NotEmpty(used);
        Assert.All(used, style => Assert.Contains(style, defined));
    }

    [Fact]
    public void TheDocument_HasTheHeadingsParagraphsAndBulletsOfTheMarkdownExport()
    {
        var paragraphs = Paragraphs(Build());

        Assert.Equal(("Heading1", "Q4 <roadmap> & \"plans\" 'draft'"), paragraphs[0]);
        Assert.Equal("Meta", paragraphs[1].Style);
        Assert.Contains("2026-10-18 10:30 · 47:12 · en · parakeet", paragraphs[1].Text, StringComparison.Ordinal);
        Assert.Contains(("Heading2", "Summary"), paragraphs);
        Assert.Contains(("Heading3", "Overview"), paragraphs);
        Assert.Contains(("Normal", "Overview with <b> & more."), paragraphs);
        Assert.Contains(("Heading3", "Key points"), paragraphs);
        Assert.Contains(("ListBullet", "•\tPoint one (01:15)"), paragraphs);
        Assert.Contains(("ListBullet", "•\tPoint two"), paragraphs);
        Assert.Contains(("Heading3", "Action items"), paragraphs);
        Assert.Contains(("ListBullet", "☐\tSend the invite (01:35)"), paragraphs);
        Assert.Contains(("Heading2", "Transcript"), paragraphs);
        Assert.Contains(("Normal", "You 11:52\nDoes 1 < 2 && 3 > 2? \"Yes\", it's 'true'."), paragraphs);
        Assert.Contains(("Normal", "Others 12:08\nPlain answer. On two lines."), paragraphs);
    }

    [Fact]
    public void TheOptions_DecideWhatIsInTheDocument()
    {
        var paragraphs = Paragraphs(Build(new ExportOptions(Summary: false, ActionItems: false, Timestamps: false, SpeakerNames: false)));

        Assert.DoesNotContain(paragraphs, p => p.Text is "Summary" or "Action items" or "Key points" or "Overview");
        Assert.Contains(("Normal", "Plain answer. On two lines."), paragraphs);

        var noActions = Paragraphs(Build(new ExportOptions(ActionItems: false)));
        Assert.Contains(("Heading3", "Key points"), noActions);
        Assert.DoesNotContain(noActions, p => p.Text == "Action items");

        var speakersOnly = Paragraphs(Build(new ExportOptions(Timestamps: false)));
        Assert.Contains(("Normal", "Others\nPlain answer. On two lines."), speakersOnly);
        Assert.Contains(("ListBullet", "•\tPoint one"), speakersOnly);
    }

    [Fact]
    public void WithoutASummary_ThereIsNoSummarySection()
    {
        var docx = TranscriptExport.ToDocx(Session, Transcript, null, null, ExportOptions.Default, null);

        Assert.DoesNotContain(Paragraphs(docx), p => p.Text is "Summary" or "Action items");
    }

    [Fact]
    public void TheRange_KeepsTheSegmentsThatOverlapIt()
    {
        var paragraphs = Paragraphs(Build(range: new ExportRange(TimeSpan.FromSeconds(728), TimeSpan.FromSeconds(741))));

        Assert.Contains(paragraphs, p => p.Text.Contains("Plain answer.", StringComparison.Ordinal));
        Assert.DoesNotContain(paragraphs, p => p.Text.Contains("Does 1", StringComparison.Ordinal));
        Assert.DoesNotContain(paragraphs, p => p.Text.Contains("Control", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_IsEscapedByTheWriter_AndTheRawBytesCarryNoMarkupOfTheSpeech()
    {
        var docx = Build();

        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("1 &lt; 2 &amp;&amp; 3 &gt; 2?", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("1 < 2", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void CharactersXmlCannotHold_AreDropped_PairsOfSurrogatesStay()
    {
        var text = Paragraphs(Build()).Single(p => p.Text.Contains("Control", StringComparison.Ordinal)).Text;

        Assert.Equal("You 12:21\nControl  char and a lone  surrogate, and an emoji 😀.", text);
    }

    [Fact]
    public void XmlSafe_KeepsTabsAndNewlines_AndDropsTheRest()
    {
        Assert.Equal("a\tb\nc", DocxWriter.XmlSafe("a\tb\nc"));
        Assert.Equal("ab", DocxWriter.XmlSafe("a\u0000\u001Fb"));
        Assert.Equal("ab", DocxWriter.XmlSafe("a￾b￿"));
        Assert.Equal("x", DocxWriter.XmlSafe("\uDC00x\uD800"));
        Assert.Equal("😀", DocxWriter.XmlSafe("😀"));
    }

    [Fact]
    public void TheSameRecording_GivesTheSameBytes()
    {
        var first = Build();
        Thread.Sleep(30);
        var second = Build();

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheEntries_CarryAFixedTime()
    {
        var bytes = Build();

        // Read the DOS time from the zip headers themselves: ZipArchiveEntry.LastWriteTime would show it in the local zone.
        var eocd = bytes.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        Assert.True(eocd >= 0);
        var count = BitConverter.ToUInt16(bytes, eocd + 10);
        var at = (int)BitConverter.ToUInt32(bytes, eocd + 16);
        var headers = 0;
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0x02014B50u, BitConverter.ToUInt32(bytes, at));
            AssertDosEpoch(bytes, at + 12);
            var local = (int)BitConverter.ToUInt32(bytes, at + 42);
            Assert.Equal(0x04034B50u, BitConverter.ToUInt32(bytes, local));
            AssertDosEpoch(bytes, local + 10);
            at += 46 + BitConverter.ToUInt16(bytes, at + 28) + BitConverter.ToUInt16(bytes, at + 30) + BitConverter.ToUInt16(bytes, at + 32);
            headers++;
        }

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(zip.Entries.Count, headers);
        Assert.All(zip.Entries, e => Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), e.LastWriteTime.DateTime));
    }

    private static void AssertDosEpoch(byte[] bytes, int timeAt)
    {
        Assert.Equal(0, BitConverter.ToUInt16(bytes, timeAt)); // 00:00:00
        Assert.Equal((1 << 5) | 1, BitConverter.ToUInt16(bytes, timeAt + 2)); // 1980-01-01
    }

    [Fact]
    public void ThePageMargins_CarryTheHeaderFooterAndGutterTheSchemaWants()
    {
        var margins = Part(Build(), "word/document.xml").Descendants(W + "pgMar").Single();

        Assert.Equal("709", (string?)margins.Attribute(W + "header"));
        Assert.Equal("709", (string?)margins.Attribute(W + "footer"));
        Assert.Equal("0", (string?)margins.Attribute(W + "gutter"));
        Assert.Equal("1134", (string?)margins.Attribute(W + "top"));
    }

    [Fact]
    public void ActionItemsWithoutTheSummary_AreAHeading2_NoLevelIsSkipped()
    {
        var alone = Paragraphs(Build(new ExportOptions(Summary: false)));
        var both = Paragraphs(Build());

        Assert.Contains(("Heading2", "Action items"), alone);
        Assert.DoesNotContain(alone, p => p.Text == "Summary");
        Assert.Contains(("Heading3", "Action items"), both);
    }

    [Fact]
    public void ASummaryWithoutText_HasNoSummaryHeading()
    {
        var paragraphs = Paragraphs(Build(summary: Summary with { Overview = string.Empty, KeyPoints = [] }));

        Assert.DoesNotContain(paragraphs, p => p.Text is "Summary" or "Overview" or "Key points");
        Assert.Contains(("Heading2", "Action items"), paragraphs);
    }

    [Fact]
    public void ALongTranscript_StillMakesAValidDocument()
    {
        var segments = Enumerable.Range(0, 5000)
            .Select(i => Segment(i % 2 == 0 ? Speaker.Me : Speaker.Others, i * 5, (i * 5) + 4, "Line " + i))
            .ToArray();

        var docx = TranscriptExport.ToDocx(Session, new Transcript(Id, segments, "parakeet", "en"), null, null, ExportOptions.Default, null);

        Assert.Equal(5000, Paragraphs(docx).Count(p => p.Text.Contains("\nLine ", StringComparison.Ordinal)));
    }
}
