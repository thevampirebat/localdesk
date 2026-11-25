using System.IO.Compression;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace LocalDesk;

internal static class Release13Checks
{
    public static void Run(string root, CancellationToken ct)
    {
        var folder = Path.Combine(root, "document-questions");
        Directory.CreateDirectory(folder);
        var note = Path.Combine(folder, "guide.md");
        File.WriteAllText(note, "An upstream cedar connector requires a restart.");
        using (var doc = ZipFile.Open(Path.Combine(folder, "guide.docx"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(doc.CreateEntry("word/document.xml").Open()))
            writer.Write("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>The reference microlens is removable.</w:t></w:r></w:p></w:body></w:document>""");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddText("Replace the azimuth bearing.", 14, new PdfPoint(30, 620), font);
        File.WriteAllBytes(Path.Combine(folder, "guide.pdf"), builder.Build());

        var index = new DocumentIndex();
        Require(index.Search(folder, "cedar", ct).Excerpts.Single().Location.StartsWith("lines"), "Markdown citation failed.");
        Require(index.Search(folder, "microlens", ct).Excerpts.Single().Location == "paragraph 1", "DOCX citation failed.");
        Require(index.Search(folder, "azimuth", ct).Excerpts.Single().Location == "page 1", "PDF citation failed.");
        File.WriteAllText(note, "A granite connector requires no restart.");
        Require(index.Search(folder, "cedar", ct).Excerpts.Count == 0 &&
            index.Search(folder, "granite", ct).Excerpts.Count == 1, "Document index did not refresh a changed file.");
        Require(OllamaClient.TryOrigin("http://192.168.1.99:11434", out _) &&
            !OllamaClient.TryOrigin("https://name:secret@example.test/v1", out _) &&
            LocalMcp.TryEndpoint("http://127.0.0.1:8080/mcp", out _) &&
            !LocalMcp.TryEndpoint("http://192.168.1.99:8080/mcp", out _), "Endpoint validation failed.");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
