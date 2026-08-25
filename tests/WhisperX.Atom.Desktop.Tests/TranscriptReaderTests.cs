using System.IO.Compression;
using System.Xml.Linq;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using WhisperX_Atom_Desktop.ViewModels;
using Xunit;

public sealed class TranscriptReaderTests
{
    [Fact]
    public void SegmentRowMatchesTextAndSpeakerWithoutChangingSource()
    {
        var segment = Segment("Саша", "Обсудили ремонт насоса");
        var row = new TranscriptSegmentRowViewModel(segment, "насоса", isCurrent: false);

        Assert.True(row.IsMatch);
        Assert.Equal("Саша", row.SpeakerText);
        Assert.Equal("00:01", row.DurationText);
        Assert.Same(segment, row.Segment);

        row.UpdateSearch("Саша");
        Assert.True(row.IsMatch);
        Assert.Equal("Обсудили ремонт насоса", segment.Text);
    }

    [Fact]
    public void TextExportUsesReaderFormatAndSrtKeepsTimecodes()
    {
        var segment = Segment("Саша", "Обсудили ремонт насоса");

        var text = TranscriptExportService.BuildText("План", new[] { segment });
        var srt = TranscriptExportService.BuildSrt(new[] { segment });

        Assert.Contains("00:01 — Саша: Обсудили ремонт насоса", text);
        Assert.Contains("00:00:01,000 --> 00:00:02,500", srt);
        Assert.Contains("Саша: Обсудили ремонт насоса", srt);
    }

    [Fact]
    public void DocxExportIsValidOpenXmlAndContainsMetadataAndSegment()
    {
        var bytes = TranscriptExportService.BuildDocx(
            "Встреча <важная>", "25.08.2026 10:00", "Готово", "ASR V1", "Высокое",
            new[] { Segment("Саша", "Текст & детали") });

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        var document = archive.GetEntry("word/document.xml");
        Assert.NotNull(document);
        using var documentStream = document!.Open();
        var xml = XDocument.Load(documentStream);
        var text = string.Concat(xml.Descendants(XName.Get("t", "http://schemas.openxmlformats.org/wordprocessingml/2006/main")).Select(node => node.Value));
        Assert.Contains("Встреча <важная>", text);
        Assert.Contains("Текст & детали", text);
    }

    private static DesktopTranscriptSegment Segment(string speaker, string text) =>
        new("segment-1", 1, 1_000, 2_500, speaker, text, 0.9, null);
}
