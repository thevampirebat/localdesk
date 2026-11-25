using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalDesk;

internal sealed record DocumentExcerpt(string File, string Location, string Text);

internal sealed class DocumentIndex
{
    readonly Dictionary<string, (long Size, DateTime Updated, List<DocumentExcerpt> Excerpts)> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly string[] Extensions = [".txt", ".md", ".pdf", ".docx"];

    public (List<DocumentExcerpt> Excerpts, int Skipped) Search(string folder, string query, CancellationToken ct)
    {
        var terms = Words(query).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length == 0) return ([], 0);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System, MaxRecursionDepth = 14 };
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", options)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).Take(300))
        {
            ct.ThrowIfCancellationRequested();
            active.Add(path);
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 25 * 1024 * 1024) { skipped++; cache.Remove(path); continue; }
                if (cache.TryGetValue(path, out var known) && known.Size == info.Length && known.Updated == info.LastWriteTimeUtc) continue;
                var extracted = Extract(path, Path.GetRelativePath(folder, path), ct).ToList();
                if (extracted.Count == 0) { skipped++; cache.Remove(path); continue; }
                cache[path] = (info.Length, info.LastWriteTimeUtc, extracted);
            }
            catch (Exception error) when (error is not OperationCanceledException) { skipped++; cache.Remove(path); }
        }
        foreach (var stale in cache.Keys.Where(path => !active.Contains(path)).ToArray()) cache.Remove(stale);
        var matches = cache.Values.SelectMany(item => item.Excerpts).Select(item =>
        {
            var words = Words(item.File + " " + item.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (item, score: terms.Count(words.Contains));
        }).Where(match => match.score > 0).OrderByDescending(match => match.score).Take(5)
          .Select(match => match.item).ToList();
        return (matches, skipped);
    }

    static IEnumerable<string> Words(string text) => Regex.Matches(text, @"[\p{L}\p{N}]{3,}")
        .Select(match => match.Value).Where(word => word.ToLowerInvariant() is not ("what" or "where" or "which" or "does" or "the" or "are" or "and" or "about"));

    static IEnumerable<DocumentExcerpt> Extract(string path, string relative, CancellationToken ct)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf")
        {
            using var pdf = PdfDocument.Open(path);
            for (int page = 1; page <= Math.Min(pdf.NumberOfPages, 250); page++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var chunk in Chunks(ContentOrderTextExtractor.GetText(pdf.GetPage(page))))
                    yield return new(relative, $"page {page}", chunk);
            }
        }
        else if (extension == ".docx")
        {
            using var zip = ZipFile.OpenRead(path);
            var xml = zip.GetEntry("word/document.xml");
            if (xml == null || xml.Length > 10_000_000) yield break;
            using var content = xml.Open();
            XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            int index = 0;
            foreach (var paragraph in XDocument.Load(content).Descendants(word + "p").Take(2000))
            {
                ct.ThrowIfCancellationRequested();
                index++;
                foreach (var chunk in Chunks(string.Concat(paragraph.Descendants(word + "t").Select(t => t.Value))))
                    yield return new(relative, $"paragraph {index}", chunk);
            }
        }
        else
        {
            int line = 0;
            foreach (var group in File.ReadLines(path).Take(10000).Chunk(14))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var chunk in Chunks(string.Join("\n", group)))
                    yield return new(relative, $"lines {line + 1}–{line + group.Length}", chunk);
                line += group.Length;
            }
        }
    }

    static IEnumerable<string> Chunks(string text)
    {
        text = text.Trim();
        for (int i = 0; i < Math.Min(text.Length, 50000); i += 850)
            yield return text.Substring(i, Math.Min(1000, text.Length - i));
    }
}

public sealed partial class MainWindow
{
    async Task AskDocuments()
    {
        if (busy || sendingRequest) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a document folder" });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
        var dialog = UtilityWindow("Ask a document folder", 830, 680);
        var index = new DocumentIndex();
        var question = new TextBox { PlaceholderText = "Ask a question about this folder", MaxLength = 2000 };
        var note = EvidenceText($"Model endpoint {ollama.Http.BaseAddress!.Authority}. Search refreshes changed files on each question.");
        var answer = new StackPanel { Spacing = 9 };
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 10, Children = { EvidenceText(folder), question, note } };
        var query = Button("Search and ask", null);
        using var closed = new CancellationTokenSource();
        dialog.Closed += (_, _) => closed.Cancel();
        query.Click += async (_, _) =>
        {
            var prompt = question.Text?.Trim();
            if (string.IsNullOrEmpty(prompt)) return;
            query.IsEnabled = false;
            answer.Children.Clear();
            using var run = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
            run.CancelAfter(TimeSpan.FromMinutes(4));
            try
            {
                note.Text = "Finding relevant passages…";
                var (excerpts, skipped) = await Task.Run(() => index.Search(folder, prompt, run.Token), run.Token);
                if (excerpts.Count == 0) { note.Text = $"No matching text found. {skipped} unreadable or oversized files."; return; }
                for (var i = 0; i < excerpts.Count; i++)
                {
                    var entry = excerpts[i];
                    answer.Children.Add(new Expander { Header = $"[{i + 1}] {entry.File} · {entry.Location}",
                        Content = new SelectableTextBlock { Text = entry.Text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } });
                }
                note.Text = $"Generating from {excerpts.Count} passages. {skipped} unreadable or oversized files.";
                var context = string.Join("\n\n", excerpts.Select((entry, i) => $"[{i + 1}] {entry.File}, {entry.Location}\n{entry.Text}"));
                var chat = new Conversation { Model = current.Model, ContextTokens = Math.Max(4096, current.ContextTokens),
                    OutputTokens = Math.Min(1024, current.OutputTokens), DocumentContext = context,
                    Messages = [new ChatMessage { Text = prompt }] };
                await ollama.Load(chat.Model, run.Token, chat.ContextTokens);
                var result = new System.Text.StringBuilder();
                await ollama.Chat(chat, text => result.Append(text), run.Token);
                answer.Children.Insert(0, RenderMarkdown(result.ToString(), ChatTextSize));
                note.Text = $"Answer ready. Expand a numbered source to read the matching passage. {skipped} unreadable or oversized files.";
            }
            catch (OperationCanceledException) { if (!closed.IsCancellationRequested) note.Text = "The document question timed out."; }
            catch (Exception error) { note.Text = error.Message; }
            finally { query.IsEnabled = true; }
        };
        panel.Children.Add(query);
        panel.Children.Add(answer);
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(this);
        loadedModel = null;
    }
}
