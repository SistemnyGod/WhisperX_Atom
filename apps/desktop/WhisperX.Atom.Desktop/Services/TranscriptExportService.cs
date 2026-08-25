using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>
/// Client-only transcript exporters. The service works on the already loaded
/// transcript and never mutates the server model or writes an intermediate file.
/// </summary>
public static class TranscriptExportService
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static string BuildText(string? title, IEnumerable<DesktopTranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) builder.AppendLine(title.Trim()).AppendLine();
        foreach (var segment in Ordered(segments))
        {
            var speaker = Speaker(segment);
            builder.Append(segment.TimeLabel).Append(" — ").Append(speaker).Append(": ")
                .AppendLine(segment.Text?.Trim() ?? string.Empty);
        }
        return builder.ToString();
    }

    public static string BuildSrt(IEnumerable<DesktopTranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var segment in Ordered(segments))
        {
            builder.AppendLine(index.ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatSrtTime(segment.StartMs)).Append(" --> ")
                .AppendLine(FormatSrtTime(segment.EndMs));
            builder.Append(Speaker(segment)).Append(": ")
                .AppendLine(segment.Text?.Trim() ?? string.Empty);
            builder.AppendLine();
            index++;
        }
        return builder.ToString();
    }

    public static byte[] BuildDocx(
        string? title,
        string? date,
        string? status,
        string? version,
        string? quality,
        IEnumerable<DesktopTranscriptSegment> segments)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", writer =>
            {
                writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                writer.WriteStartElement("Default"); writer.WriteAttributeString("Extension", "rels"); writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml"); writer.WriteEndElement();
                writer.WriteStartElement("Default"); writer.WriteAttributeString("Extension", "xml"); writer.WriteAttributeString("ContentType", "application/xml"); writer.WriteEndElement();
                writer.WriteStartElement("Override"); writer.WriteAttributeString("PartName", "/word/document.xml"); writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"); writer.WriteEndElement();
                writer.WriteStartElement("Override"); writer.WriteAttributeString("PartName", "/word/styles.xml"); writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"); writer.WriteEndElement();
                writer.WriteEndElement();
            });
            WriteEntry(archive, "_rels/.rels", writer =>
            {
                writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                writer.WriteStartElement("Relationship"); writer.WriteAttributeString("Id", "rId1"); writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"); writer.WriteAttributeString("Target", "word/document.xml"); writer.WriteEndElement();
                writer.WriteEndElement();
            });
            WriteEntry(archive, "word/_rels/document.xml.rels", writer =>
            {
                writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                writer.WriteEndElement();
            });
            WriteEntry(archive, "word/styles.xml", writer =>
            {
                WStart(writer, "styles");
                WStart(writer, "docDefaults"); writer.WriteEndElement();
                writer.WriteEndElement();
            });
            WriteEntry(archive, "word/document.xml", writer => WriteDocument(writer, title, date, status, version, quality, segments));
        }
        return stream.ToArray();
    }

    private static IEnumerable<DesktopTranscriptSegment> Ordered(IEnumerable<DesktopTranscriptSegment> segments) =>
        segments.OrderBy(item => item.Ordinal);

    private static string Speaker(DesktopTranscriptSegment segment) =>
        string.IsNullOrWhiteSpace(segment.Speaker) ? "Спикер не определён" : segment.Speaker.Trim();

    private static string FormatSrtTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    private static void WriteEntry(ZipArchive archive, string path, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, Indent = false });
        write(writer);
    }

    private static void WriteDocument(XmlWriter writer, string? title, string? date, string? status, string? version, string? quality, IEnumerable<DesktopTranscriptSegment> segments)
    {
        WStart(writer, "document");
        WStart(writer, "body");
        Paragraph(writer, title ?? "Стенограмма", bold: true, size: "30");
        Paragraph(writer, $"Дата: {date ?? "—"}");
        Paragraph(writer, $"Статус: {status ?? "—"} · Версия: {version ?? "—"} · Качество: {quality ?? "—"}");
        foreach (var segment in Ordered(segments))
            Paragraph(writer, $"{segment.TimeLabel} — {Speaker(segment)}: {segment.Text?.Trim() ?? string.Empty}");
        WStart(writer, "sectPr");
        WStart(writer, "pgSz"); WAttr(writer, "w", "11906"); WAttr(writer, "h", "16838"); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Paragraph(XmlWriter writer, string text, bool bold = false, string? size = null)
    {
        WStart(writer, "p");
        WStart(writer, "r");
        if (bold || size is not null)
        {
            WStart(writer, "rPr");
            if (bold) { WStart(writer, "b"); writer.WriteEndElement(); }
            if (size is not null) { WStart(writer, "sz"); WAttr(writer, "val", size); writer.WriteEndElement(); }
            writer.WriteEndElement();
        }
        WStart(writer, "t");
        writer.WriteString(text);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WStart(XmlWriter writer, string localName) => writer.WriteStartElement("w", localName, WordNamespace);

    private static void WAttr(XmlWriter writer, string localName, string value) => writer.WriteAttributeString("w", localName, WordNamespace, value);
}
