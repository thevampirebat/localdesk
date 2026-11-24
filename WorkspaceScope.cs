using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LocalDesk;

static class WorkspaceScope
{
    internal static readonly HashSet<string> Excluded = new(
        [".git", ".svn", ".vs", "node_modules", "bin", "obj", "vendor", "packages", ".venv"], StringComparer.OrdinalIgnoreCase);

    public static bool IsBroad(string folder, string fallback)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return new[] { fallback, @"D:\Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetPathRoot(root) ?? root }
            .Where(Path.IsPathFullyQualified)
            .Any(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    public static string? RelativeFile(string folder, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        var relative = Path.GetRelativePath(folder, path);
        return Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : relative;
    }

    public static bool HasReparsePoint(string path)
    {
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
        {
            try { if (File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint)) return true; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }

    public static string Focus(string root, string fallback, string prompt, IEnumerable<string> attachments, CancellationToken ct)
    {
        var paths = attachments.Where(Path.IsPathFullyQualified).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        var names = new List<(string Path, int Score)>();
        var broad = IsBroad(root, fallback);
        var folders = new Queue<(string Path, int Depth)>();
        foreach (var file in paths) folders.Enqueue((Path.GetDirectoryName(file)!, 3));
        if (!broad) folders.Enqueue((root, 0));
        var terms = Regex.Matches(prompt.ToLowerInvariant(), "[a-z][a-z0-9]{2,}")
            .Select(m => m.Value).Where(w => w is not ("the" or "and" or "for" or "with" or "this" or "that" or "file" or "please" or "change" or "update"))
            .Distinct().Take(24).ToList();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesSeen = 0;
        while (folders.TryDequeue(out var entry) && visited.Count < 220 && filesSeen < 2500)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(entry.Path)) continue;
            try
            {
                if (HasReparsePoint(entry.Path)) continue;
                foreach (var file in Directory.EnumerateFiles(entry.Path))
                {
                    if (++filesSeen > 2500) break;
                    var name = Path.GetFileName(file).ToLowerInvariant();
                    int matches = terms.Count(name.Contains);
                    bool manifest = Path.GetExtension(name) is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj"
                        || name is "package.json" or "pyproject.toml" or "cargo.toml" or "readme.md" or "agents.md";
                    if (matches > 0 || manifest) names.Add((file, matches * 10 + (manifest ? 3 : 0)));
                }
                if (!broad && entry.Depth < 4)
                    foreach (var child in Directory.EnumerateDirectories(entry.Path).Take(300))
                        if (!Excluded.Contains(Path.GetFileName(child)) && folders.Count < 300) folders.Enqueue((child, entry.Depth + 1));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var hints = names.OrderByDescending(n => n.Score).ThenBy(n => n.Path.Length).Select(n => n.Path)
            .Where(p => !paths.Contains(p, StringComparer.OrdinalIgnoreCase)).Take(10);
        var shortlist = paths.Concat(hints).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => "- " + (RelativeFile(root, p) ?? p));
        var guidance = broad
            ? "This is a broad working folder. Do not search it recursively. Start with the attached files. If there are no relevant attachments or known paths, ask for a source folder."
            : "Start with the shortlist below. Search nearby source or a likely subfolder first. Expand scope only if necessary. Read specific line ranges.";
        return guidance + "\nFile hints from LocalDesk (filenames only):\n" + string.Join("\n", shortlist);
    }
}

static class ToolOutputArchive
{
    public static void Clip(JsonObject request, int budget, Func<string, string?> archive)
    {
        if (request["messages"] is not JsonArray messages) return;
        var tools = messages.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool").ToList();
        int largeResults = tools.Count(m => (m["content"]?.ToString().Length ?? 0) > 1500);
        int limit = Math.Clamp(budget * 2 / 3 / Math.Max(1, largeResults), 900, 9000);
        foreach (var message in tools)
        {
            var content = message["content"];
            var text = content is JsonValue value && value.TryGetValue<string>(out var plain) ? plain : content?.ToJsonString() ?? "";
            if (text.Length <= limit) continue;
            var path = archive(text);
            var head = limit * 2 / 3;
            var suffix = limit - head;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            message["content"] = text[..head] +
                $"\n[LocalDesk shortened this {text.Length:N0}-character tool result. Full output {(path == null ? "could not be archived." : "is available in Activity.")} Result fingerprint: {fingerprint}. Request a narrower range for omitted details.]\n" +
                text[^suffix..];
        }
    }

    private static string LogPath(string folder, string text) => Path.Combine(folder,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) + ".txt");

    public static string Save(string folder, string text) => Save(folder, text, out _);

    public static string Save(string folder, string text, out bool created)
    {
        created = false;
        var path = LogPath(folder, text);
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, text);
            try { File.Move(temporary, path, overwrite: false); created = true; }
            catch (IOException) when (File.Exists(path)) { }
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
