using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LocalDesk;

static class ModelStorage
{
    private static readonly string[] Directories = ["blobs", "manifests", "metadata"];

    public static string CurrentFolder
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("OLLAMA_MODELS");
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models")
                : configured);
        }
    }

    private record Item(string Relative, long Length, DateTime WriteTime);
    public record TransferProgress(string Text, long Done, long Total);
    public record TransferResult(int Models, int OldFilesRetained);

    public static async Task<TransferResult> Move(string source, string destination, bool keepOriginals,
        Action requireOffline, Func<string, int, CancellationToken, Task> verify,
        Action<string> activate, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            WorkspaceScope.RelativeFile(source, destination) != null || WorkspaceScope.RelativeFile(destination, source) != null ||
            Path.GetPathRoot(destination) == destination || WorkspaceScope.HasReparsePoint(source) || WorkspaceScope.HasReparsePoint(destination))
            throw new IOException("Choose a separate, empty folder on a local drive. Linked folders cannot be used.");
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Choose a folder on a local drive.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The destination must be empty. Create a new folder for the model library.");

        if (!Directory.Exists(source)) throw new IOException("The current model folder does not exist.");
        requireOffline();
        var entries = await Task.Run(() => Inventory(source, ct), ct);
        int modelCount = entries.Count(f => f.Relative.StartsWith("manifests" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        var total = entries.Sum(item => item.Length);
        if (drive.AvailableFreeSpace < total)
            throw new IOException($"The destination needs at least {total / 1073741824d:F1} GiB free for the verified copy.");

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".localdesk-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool published = false;
        try
        {
            long done = 0;
            foreach (var file in entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(stage, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var name = Path.GetFileName(file.Relative);
                await Transfer(Path.Combine(source, file.Relative), target, file.Length,
                    bytes => progress.Report(new("Copying " + name, done + bytes, total)),
                    () => progress.Report(new("Verifying " + name, done + file.Length, total)), ct);
                done += file.Length;
                progress.Report(new("Verified " + name, done, total));
            }
            progress.Report(new("Checking the copied model catalog with Ollama…", total, total));
            requireOffline();
            ValidateManifestBlobs(stage, entries);
            await verify(stage, modelCount, ct);
            requireOffline();
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: false);
            Directory.Move(stage, destination);
            published = true;
            // A failure here leaves the verified copy and the original library intact.
            activate(destination);
        }
        finally
        {
            if (!published)
            {
                foreach (var item in entries)
                {
                    try { File.Delete(Path.Combine(stage, item.Relative)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                PruneEmpty(stage, entries, includeRoot: true);
            }
        }

        if (keepOriginals) return new(modelCount, entries.Count);
        progress.Report(new("Removing verified originals…", total, total));
        int retained = 0;
        foreach (var item in entries)
        {
            var path = Path.Combine(source, item.Relative);
            try
            {
                var info = new FileInfo(path);
                if (WorkspaceScope.HasReparsePoint(path) || info.Length != item.Length || info.LastWriteTimeUtc != item.WriteTime)
                { retained++; continue; }
                info.Delete();
            }
            catch (IOException) { retained++; }
            catch (UnauthorizedAccessException) { retained++; }
        }
        PruneEmpty(source, entries, includeRoot: false);
        return new(modelCount, retained);
    }

    private static List<Item> Inventory(string root, CancellationToken ct)
    {
        var result = new List<Item>();
        var dirs = new Stack<string>(Directories.Select(name => Path.Combine(root, name)).Where(Directory.Exists));
        while (dirs.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The source library contains a linked folder.");
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The source library contains a linked file.");
                if (info.Name.Contains("-partial", StringComparison.Ordinal))
                    throw new IOException("A model download is incomplete. Finish or cancel it in Ollama before moving the library.");
                result.Add(new(Path.GetRelativePath(root, file), info.Length, info.LastWriteTimeUtc));
            }
            foreach (var child in Directory.EnumerateDirectories(folder)) dirs.Push(child);
        }
        return result.OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ValidateManifestBlobs(string root, IEnumerable<Item> files)
    {
        foreach (var file in files.Where(f => f.Relative.StartsWith("manifests" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, file.Relative))) ?? throw new IOException("Invalid model manifest.");
            var layers = manifest["layers"]?.AsArray() ?? throw new IOException("A model manifest is missing its layers.");
            foreach (var layer in layers.Append(manifest["config"]))
            {
                string digest = layer?["digest"]?.GetValue<string>() ?? "";
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
                    throw new IOException("A model manifest contains an invalid blob digest.");
                if (!File.Exists(Path.Combine(root, "blobs", "sha256-" + digest[7..])))
                    throw new IOException("A model manifest references a missing file. Repair the original library before moving it.");
            }
        }
    }

    private static async Task Transfer(string source, string target, long length, Action<long> progress, Action verifying, CancellationToken ct)
    {
        const int chunk = 1024 * 1024;
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(chunk);
        try
        {
            long bytes = 0, notified = 0;
            await using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, chunk, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int size;
                while ((size = await original.ReadAsync(buffer.AsMemory(0, chunk), ct)) > 0)
                {
                    await copy.WriteAsync(buffer.AsMemory(0, size), ct);
                    hash.AppendData(buffer, 0, size);
                    bytes += size;
                    if (bytes - notified >= 32 * chunk) { progress(bytes); notified = bytes; }
                }
            }
            verifying();
            var originalHash = hash.GetHashAndReset();
            string name = Path.GetFileName(source);
            if (name.StartsWith("sha256-", StringComparison.Ordinal) && name.Length == 71 &&
                !name[7..].Equals(Convert.ToHexString(originalHash), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The original blob failed its digest check: " + name);
            await using var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var copiedHash = await SHA256.HashDataAsync(check, ct);
            if (bytes != length || !originalHash.AsSpan().SequenceEqual(copiedHash))
                throw new IOException("File verification failed for " + Path.GetFileName(source) + ".");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void PruneEmpty(string root, IEnumerable<Item> items, bool includeRoot)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includeRoot) folders.Add(root);
        foreach (var item in items)
            for (var parent = Path.GetDirectoryName(Path.Combine(root, item.Relative)); parent != null && parent != root; parent = Path.GetDirectoryName(parent))
                folders.Add(parent);
        foreach (var path in folders.OrderByDescending(path => path.Length))
        {
            try { if (!WorkspaceScope.HasReparsePoint(path)) Directory.Delete(path, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void RequireOffline()
    {
        var processes = Process.GetProcessesByName("ollama").Concat(Process.GetProcessesByName("ollama app")).ToList();
        foreach (var process in processes) process.Dispose();
        if (processes.Count > 0)
            throw new IOException("Quit Ollama from its Windows tray icon, then click Move library again. This prevents changes while files are copied.");
    }

    public static void Activate(string folder)
    {
        Environment.SetEnvironmentVariable("OLLAMA_MODELS", folder, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("OLLAMA_MODELS", folder, EnvironmentVariableTarget.Process);
    }

    public static async Task VerifyWithOllama(string folder, int expected, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var server = OllamaClient.StartServer(folder, port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(4) };
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(40))
            {
                ct.ThrowIfCancellationRequested();
                if (server.HasExited) throw new IOException("Ollama could not start with the new model library.");
                try
                {
                    using var response = await client.GetAsync("/api/tags", ct);
                    response.EnsureSuccessStatusCode();
                    var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["models"]?.AsArray();
                    if (models?.Count != expected)
                        throw new IOException($"The copied library reports {models?.Count ?? 0} of {expected} expected models. The original library was kept.");
                    return;
                }
                catch (HttpRequestException) { await Task.Delay(500, ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await Task.Delay(500, ct); }
            }
            throw new IOException("Ollama did not answer when verifying the copied library.");
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
