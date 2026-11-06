using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Murmur.Core.Sessions;

/// <summary>One piece of a paragraph: a text with its look. A tab in the text is a tab stop, <see cref="BreakBefore"/> starts a new line.</summary>
internal readonly record struct DocxRun(string Text, bool Bold = false, bool Mono = false, bool BreakBefore = false);

/// <summary>A paragraph of the document: the id of its style (<see cref="DocxWriter"/>) and its runs.</summary>
internal readonly record struct DocxParagraph(string Style, IReadOnlyList<DocxRun> Runs);

/// <summary>
/// Writes a minimal .docx by hand: WordprocessingML through <see cref="XmlWriter"/> into a <see cref="ZipArchive"/>. No Open XML SDK (it uses
/// reflection and is not AOT-safe). Every text goes through the writer, so <c>&lt; &amp; &gt; " '</c> are escaped, characters XML 1.0 cannot
/// hold are dropped, and the zip entries carry a fixed time, so the same paragraphs always give the same bytes.
/// </summary>
internal static class DocxWriter
{
    public const string Heading1 = "Heading1";
    public const string Heading2 = "Heading2";
    public const string Heading3 = "Heading3";
    public const string Normal = "Normal";
    public const string Meta = "Meta";
    public const string Bullet = "ListBullet";

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string StylesRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

    /// <summary>The earliest time a zip entry can carry (the DOS epoch).</summary>
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Build(IReadOnlyList<DocxParagraph> paragraphs)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", WriteContentTypes);
            WriteEntry(zip, "_rels/.rels", writer => WriteRelationship(writer, "rId1", OfficeDocumentRelationship, "word/document.xml"));
            WriteEntry(zip, "word/document.xml", writer => WriteDocument(writer, paragraphs));
            WriteEntry(zip, "word/_rels/document.xml.rels", writer => WriteRelationship(writer, "rId1", StylesRelationship, "styles.xml"));
            WriteEntry(zip, "word/styles.xml", WriteStyles);
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using var output = entry.Open();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false,
        };
        using var writer = XmlWriter.Create(output, settings);
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static void WriteContentTypes(XmlWriter writer)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        writer.WriteStartElement("Types", ns);
        Default(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default(writer, "xml", "application/xml");
        Override(writer, "/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        Override(writer, "/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
        writer.WriteEndElement();

        static void Default(XmlWriter w, string extension, string contentType)
        {
            w.WriteStartElement("Default");
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", contentType);
            w.WriteEndElement();
        }

        static void Override(XmlWriter w, string partName, string contentType)
        {
            w.WriteStartElement("Override");
            w.WriteAttributeString("PartName", partName);
            w.WriteAttributeString("ContentType", contentType);
            w.WriteEndElement();
        }
    }

    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationships", RelationshipsNamespace);
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteDocument(XmlWriter writer, IReadOnlyList<DocxParagraph> paragraphs)
    {
        writer.WriteStartElement("w", "document", W);
        writer.WriteStartElement("w", "body", W);
        foreach (var paragraph in paragraphs)
        {
            writer.WriteStartElement("w", "p", W);
            writer.WriteStartElement("w", "pPr", W);
            writer.WriteStartElement("w", "pStyle", W);
            writer.WriteAttributeString("w", "val", W, paragraph.Style);
            writer.WriteEndElement();
            writer.WriteEndElement();
            foreach (var run in paragraph.Runs)
            {
                WriteRun(writer, run);
            }

            writer.WriteEndElement();
        }

        // A4, 2 cm margins.
        writer.WriteStartElement("w", "sectPr", W);
        writer.WriteStartElement("w", "pgSz", W);
        writer.WriteAttributeString("w", "w", W, "11906");
        writer.WriteAttributeString("w", "h", W, "16838");
        writer.WriteEndElement();
        writer.WriteStartElement("w", "pgMar", W);
        foreach (var side in new[] { "top", "right", "bottom", "left" })
        {
            writer.WriteAttributeString("w", side, W, "1134");
        }

        // The schema wants the header, footer and gutter distances too.
        writer.WriteAttributeString("w", "header", W, "709");
        writer.WriteAttributeString("w", "footer", W, "709");
        writer.WriteAttributeString("w", "gutter", W, "0");

        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteRun(XmlWriter writer, DocxRun run)
    {
        writer.WriteStartElement("w", "r", W);
        if (run.Bold || run.Mono)
        {
            writer.WriteStartElement("w", "rPr", W);
            if (run.Mono)
            {
                writer.WriteStartElement("w", "rFonts", W);
                foreach (var attribute in new[] { "ascii", "hAnsi", "cs" })
                {
                    writer.WriteAttributeString("w", attribute, W, "Consolas");
                }

                writer.WriteEndElement();
            }

            if (run.Bold)
            {
                writer.WriteStartElement("w", "b", W);
                writer.WriteEndElement();
            }

            if (run.Mono)
            {
                writer.WriteStartElement("w", "color", W);
                writer.WriteAttributeString("w", "val", W, "777169");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        if (run.BreakBefore)
        {
            writer.WriteStartElement("w", "br", W);
            writer.WriteEndElement();
        }

        // A tab is its own element; the rest is text (spaces kept).
        var text = XmlSafe(run.Text);
        var start = 0;
        while (start <= text.Length)
        {
            var tab = text.IndexOf('\t', start);
            var end = tab < 0 ? text.Length : tab;
            if (end > start)
            {
                writer.WriteStartElement("w", "t", W);
                writer.WriteAttributeString("xml", "space", null, "preserve");
                writer.WriteString(text[start..end]);
                writer.WriteEndElement();
            }

            if (tab < 0)
            {
                break;
            }

            writer.WriteStartElement("w", "tab", W);
            writer.WriteEndElement();
            start = tab + 1;
        }

        writer.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter writer)
    {
        writer.WriteStartElement("w", "styles", W);

        writer.WriteStartElement("w", "docDefaults", W);
        writer.WriteStartElement("w", "rPrDefault", W);
        writer.WriteStartElement("w", "rPr", W);
        Fonts(writer, "Calibri");
        Val(writer, "sz", "22");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("w", "pPrDefault", W);
        writer.WriteStartElement("w", "pPr", W);
        Spacing(writer, after: "120", line: "276");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        Style(writer, Normal, "Normal", basedOn: null, isDefault: true, paragraph: null, run: null);
        Style(writer, Heading1, "heading 1", Normal, false, p => { Val(p, "keepNext", null); Spacing(p, after: "160", before: "0"); Val(p, "outlineLvl", "0"); }, r => { Val(r, "b", null); Val(r, "sz", "40"); });
        Style(writer, Heading2, "heading 2", Normal, false, p => { Val(p, "keepNext", null); Spacing(p, after: "120", before: "320"); Val(p, "outlineLvl", "1"); }, r => { Val(r, "b", null); Val(r, "sz", "30"); });
        Style(writer, Heading3, "heading 3", Normal, false, p => { Val(p, "keepNext", null); Spacing(p, after: "80", before: "200"); Val(p, "outlineLvl", "2"); }, r => { Val(r, "b", null); Val(r, "sz", "24"); });
        Style(writer, Meta, "Meta", Normal, false, p => Spacing(p, after: "240"), r => { Val(r, "color", "777169"); Val(r, "sz", "20"); });
        Style(writer, Bullet, "List Bullet", Normal, false, p =>
        {
            Spacing(p, after: "60");
            p.WriteStartElement("w", "ind", W);
            p.WriteAttributeString("w", "left", W, "425");
            p.WriteAttributeString("w", "hanging", W, "425");
            p.WriteEndElement();
        }, run: null);

        writer.WriteEndElement();
    }

    private static void Style(XmlWriter writer, string id, string name, string? basedOn, bool isDefault, Action<XmlWriter>? paragraph, Action<XmlWriter>? run)
    {
        writer.WriteStartElement("w", "style", W);
        writer.WriteAttributeString("w", "type", W, "paragraph");
        if (isDefault)
        {
            writer.WriteAttributeString("w", "default", W, "1");
        }

        writer.WriteAttributeString("w", "styleId", W, id);
        Val(writer, "name", name);
        if (basedOn is not null)
        {
            Val(writer, "basedOn", basedOn);
            Val(writer, "qFormat", null);
        }

        if (paragraph is not null)
        {
            writer.WriteStartElement("w", "pPr", W);
            paragraph(writer);
            writer.WriteEndElement();
        }

        if (run is not null)
        {
            writer.WriteStartElement("w", "rPr", W);
            run(writer);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void Val(XmlWriter writer, string element, string? value)
    {
        writer.WriteStartElement("w", element, W);
        if (value is not null)
        {
            writer.WriteAttributeString("w", "val", W, value);
        }

        writer.WriteEndElement();
    }

    private static void Fonts(XmlWriter writer, string font)
    {
        writer.WriteStartElement("w", "rFonts", W);
        foreach (var attribute in new[] { "ascii", "hAnsi", "cs", "eastAsia" })
        {
            writer.WriteAttributeString("w", attribute, W, font);
        }

        writer.WriteEndElement();
    }

    private static void Spacing(XmlWriter writer, string after, string? before = null, string? line = null)
    {
        writer.WriteStartElement("w", "spacing", W);
        if (before is not null)
        {
            writer.WriteAttributeString("w", "before", W, before);
        }

        writer.WriteAttributeString("w", "after", W, after);
        if (line is not null)
        {
            writer.WriteAttributeString("w", "line", W, line);
            writer.WriteAttributeString("w", "lineRule", W, "auto");
        }

        writer.WriteEndElement();
    }

    /// <summary>The text without the characters XML 1.0 cannot hold (control characters, unpaired surrogates, U+FFFE/FFFF).</summary>
    internal static string XmlSafe(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            bool keep;
            var pair = false;
            if (char.IsHighSurrogate(c))
            {
                pair = i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                keep = pair;
            }
            else if (char.IsLowSurrogate(c))
            {
                keep = false;
            }
            else
            {
                keep = c is '\t' or '\n' or '\r' or (>= ' ' and <= '\uD7FF') or (>= '\uE000' and <= '\uFFFD');
            }

            if (!keep && builder is null)
            {
                builder = new StringBuilder(text.Length).Append(text, 0, i);
            }

            if (keep)
            {
                builder?.Append(c);
                if (pair)
                {
                    builder?.Append(text[i + 1]);
                    i++;
                }
            }
            else if (pair)
            {
                i++;
            }
        }

        return builder?.ToString() ?? text;
    }
}
