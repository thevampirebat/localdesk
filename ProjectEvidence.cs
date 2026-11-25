using System.Diagnostics;
using System.Security.Cryptography;

namespace LocalDesk;

public record VerifiedFile(string Path, string Change, string? Diff)
{
    public string? AfterHash { get; init; }
    public string? BeforeSnapshot { get; init; }
    public string? AfterSnapshot { get; init; }
    public bool Undone { get; set; }
}
public record BuildResult(int ExitCode, string LogPath)
{
    public List<string> Executables { get; init; } = [];
}

public sealed class ProjectEvidence
{
    internal const int ReviewSizeLimit = 256 * 1024;
    private record Stamp(string Hash, byte[]? Bytes);
    private readonly Dictionary<string, Stamp> before;
    private readonly string root;
    private readonly bool beforeLimited;
    private readonly bool scanProject;
    private readonly string[] attachments;
    public bool ScansProject => scanProject;
    public bool Limited { get; private set; }

    public ProjectEvidence(string root, string defaultFolder, IEnumerable<string> attachments)
    {
        this.root = Path.GetFullPath(root);
        scanProject = !WorkspaceScope.IsBroad(this.root, defaultFolder);
        this.attachments = attachments.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        before = Capture();
        beforeLimited = Limited;
    }

    private sealed record BaselineData(string Root,bool Limited,bool Scan,string[] Attachments,Dictionary<string,string> Hashes);
    private ProjectEvidence(BaselineData data)
    {
        root = data.Root;
        beforeLimited = data.Limited;
        scanProject = data.Scan;
        attachments = data.Attachments;
        before = new(StringComparer.OrdinalIgnoreCase);
        foreach(var (path,hash) in data.Hashes)before[path]=new(hash,null);
    }

    public void SaveBaseline(string path)
    {
        var data=new BaselineData(root,beforeLimited,scanProject,attachments,before.ToDictionary(x=>x.Key,x=>x.Value.Hash,StringComparer.OrdinalIgnoreCase));
        File.WriteAllText(path+".tmp",System.Text.Json.JsonSerializer.Serialize(data));
        File.Move(path+".tmp",path,true);
    }

    public static ProjectEvidence Restore(string path,string expectedRoot)
    {
        var data=System.Text.Json.JsonSerializer.Deserialize<BaselineData>(File.ReadAllText(path))??throw new IOException("The recovery baseline is invalid.");
        if(!Path.GetFullPath(expectedRoot).Equals(data.Root,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The recovery baseline belongs to a different project.");
        return new(data);
    }

    public string FocusedEditExcerpt(string path)
    {
        try
        {
            if(!before.TryGetValue(path,out var original)||original.Bytes==null||!File.Exists(path)||
                new FileInfo(path).Length>ReviewSizeLimit||WorkspaceScope.HasReparsePoint(path))return "";
            var start=Decode(original.Bytes)!;
            var now=Decode(File.ReadAllBytes(path))!;
            if(start==now)return "";
            int prefix=0,suffix=0;
            while(prefix<start.Length&&prefix<now.Length&&start[prefix]==now[prefix])prefix++;
            while(suffix<start.Length-prefix&&suffix<now.Length-prefix&&start[^(suffix+1)]==now[^(suffix+1)])suffix++;
            int begin=Math.Max(0,prefix-100);
            string Excerpt(string content)
            {
                int count=Math.Min(620, Math.Min(content.Length-begin,content.Length-suffix-begin+105));
                return content.Substring(begin,count);
            }
            return "\nObserved edit in "+Path.GetFileName(path)+" (reference data):\nBefore: "+Excerpt(start)+"\nAfter: "+Excerpt(now);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){return "";}
    }

    public List<VerifiedFile> Finish(string? snapshots = null)
    {
        var after = Capture();
        var changes = new List<VerifiedFile>();
        // If enumeration was capped or inaccessible, absence is not proof of deletion.
        foreach (var path in after.Keys.Union(before.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            before.TryGetValue(path, out var old);
            after.TryGetValue(path, out var current);
            if (old == null && beforeLimited && !attachments.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            if (old?.Hash == current?.Hash || (current == null && (Limited || File.Exists(path)))) continue;
            var kind = old == null ? "Added" : current == null ? "Deleted" : "Modified";
            var result = new VerifiedFile(path, kind, Preview(Decode(old?.Bytes), Decode(current?.Bytes)));
            if (snapshots != null && changes.Count < 80 &&
                (old == null || old.Bytes != null) && (current == null || current.Bytes != null))
            {
                try
                {
                    Directory.CreateDirectory(snapshots);
                    var key = Guid.NewGuid().ToString("N");
                    string? SaveSnapshot(byte[]? bytes, string suffix)
                    {
                        if (bytes == null) return null;
                        var snapshot = Path.Combine(snapshots, key + suffix);
                        File.WriteAllBytes(snapshot, bytes);
                        return snapshot;
                    }
                    result = result with { BeforeSnapshot = SaveSnapshot(old?.Bytes, ".before"), AfterSnapshot = SaveSnapshot(current?.Bytes, ".after"), AfterHash = current?.Hash };
                }
                catch (IOException) { Limited = true; }
                catch (UnauthorizedAccessException) { Limited = true; }
            }
            changes.Add(result);
        }
        return changes;
    }

    internal Dictionary<string, string> ProgressStamp(bool starting = false) =>
        (starting ? before : Capture(keepBytes: false)).ToDictionary(entry => entry.Key, entry => entry.Value.Hash, StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, Stamp> Capture(bool keepBytes = true)
    {
        var files = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        var paths = attachments.AsEnumerable();
        if (scanProject) paths = paths.Concat(ProjectPaths());
        long total = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (info.Length > 48L * 1024 * 1024) { Limited = true; continue; }
                if (files.Count >= 3000 || total + info.Length > 192L * 1024 * 1024)
                {
                    Limited = true;
                    break;
                }
                using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[]? bytes = null;
                string hash;
                if (keepBytes && info.Length <= ReviewSizeLimit && AttachedFiles.IsText(path))
                {
                    bytes = new byte[(int)info.Length];
                    stream.ReadExactly(bytes);
                    if (stream.ReadByte() != -1) { Limited = true; continue; }
                    hash = Convert.ToHexString(SHA256.HashData(bytes));
                }
                else hash = Convert.ToHexString(SHA256.HashData(stream));
                total += info.Length;
                files[path] = new(hash, bytes);
            }
            catch (IOException) { Limited = true; }
            catch (UnauthorizedAccessException) { Limited = true; }
        }
        return files;
    }

    private IEnumerable<string> ProjectPaths()
    {
        var excluded = new HashSet<string>([".git", ".svn", ".vs", "node_modules", "obj", "vendor", "packages", ".venv"], StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>();
        folders.Push(root);
        var seen = 0;
        while (folders.TryPop(out var folder))
        {
            if (++seen > 2000) { Limited = true; yield break; }
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(folder);
                dirs = Directory.GetDirectories(folder);
            }
            catch (IOException) { Limited = true; continue; }
            catch (UnauthorizedAccessException) { Limited = true; continue; }
            foreach (var path in files)
                if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || (!Path.GetRelativePath(root,folder).Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase)
                        && (AttachedFiles.IsText(path) || Path.GetExtension(path).ToLowerInvariant() is ".axaml" or ".csproj" or ".sln" or ".props" or ".targets")))
                    yield return path;
            foreach (var dir in dirs)
            {
                try
                {
                    if (!excluded.Contains(Path.GetFileName(dir)) && !File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                        folders.Push(dir);
                }
                catch (IOException) { Limited = true; }
                catch (UnauthorizedAccessException) { Limited = true; }
            }
        }
    }

    internal static string? Decode(byte[]? bytes)
    {
        if (bytes == null) return null;
        using var reader = new StreamReader(new MemoryStream(bytes));
        return reader.ReadToEnd();
    }

    private static string? Preview(string? old, string? current)
    {
        if (old == current || (old == null && current == null)) return null;
        var left = old?.Replace("\r\n", "\n").Split('\n') ?? [];
        var right = current?.Replace("\r\n", "\n").Split('\n') ?? [];
        int start = 0, suffix = 0;
        while (start < left.Length && start < right.Length && left[start] == right[start]) start++;
        while (suffix < left.Length - start && suffix < right.Length - start && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var removed = left.Length - start - suffix;
        var added = right.Length - start - suffix;
        var lines = left.Skip(start).Take(Math.Min(removed, 12)).Select(line => "- " + Short(line))
            .Concat(right.Skip(start).Take(Math.Min(added, 12)).Select(line => "+ " + Short(line)));
        return $"From line {start + 1} · {removed} removed · {added} added\n" + string.Join("\n", lines)
            + (removed > 12 || added > 12 ? "\n… preview shortened" : "");
    }

    private static string Short(string line) => line.Length > 450 ? line[..450] + "…" : line;

    public static Task<BuildResult> Build(string project, string workingFolder, string logPath, CancellationToken ct) =>
        RunDotnet(workingFolder, ["build", project, "-c", "Release", "--nologo"], logPath, ct);

    public static async Task<BuildResult> RunDotnet(string workingFolder, IReadOnlyList<string> args, string logPath, CancellationToken ct)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingFolder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start dotnet.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var stdout = await output;
        await File.WriteAllTextAsync(logPath, stdout + "\n" + await errors, ct);
        var executables = new List<string>();
        if (process.ExitCode == 0)
        {
            foreach (var line in stdout.Split('\n'))
            {
                var separator = line.IndexOf(" -> ", StringComparison.Ordinal);
                if (separator < 0) continue;
                var outputPath = line[(separator + 4)..].Trim();
                if (!Path.IsPathFullyQualified(outputPath) || !File.Exists(outputPath)) continue;
                var exe = Path.ChangeExtension(outputPath, ".exe");
                if (File.Exists(exe) && !executables.Contains(exe, StringComparer.OrdinalIgnoreCase)) executables.Add(exe);
            }
        }
        return new(process.ExitCode, logPath) { Executables = executables };
    }
}

public sealed class ToolLoop
{
    private string? last;
    private int repeated;
    private int failed;
    public string? Observe(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) { last = null; repeated = 0; return null; }
        repeated = signature == last ? repeated + 1 : 1;
        last = signature;
        return repeated >= 4 ? "The agent repeated the same tool call four times in a row." : null;
    }
    public string? Outcome(string status)
    {
        if (status == "completed") failed = 0;
        if (status == "failed" && ++failed >= 3) return "Three tools failed in a row.";
        return null;
    }
}
