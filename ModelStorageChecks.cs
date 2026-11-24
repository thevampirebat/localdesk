using System.Security.Cryptography;
using System.Text.Json;

namespace LocalDesk;

static class ModelStorageChecks
{
    private sealed class NoProgress : IProgress<ModelStorage.TransferProgress>
    {
        public void Report(ModelStorage.TransferProgress value) { }
    }

    public static async Task Run(string root, bool live, CancellationToken ct)
    {
        var area = Path.Combine(root, "migration-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(area, "original");
        var target = Path.Combine(area, "moved");
        string manifestDir = Path.Combine(source, "manifests", "registry.ollama.ai", "library", "localdesk-fixture");
        string blobs = Path.Combine(source, "blobs");
        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(blobs);
        var config = MakeBlob(blobs, "application/vnd.docker.container.image.v1+json",
            """{"model_format":"gguf","model_family":"localdesk-fixture","model_families":["localdesk-fixture"],"model_type":"fixture","file_type":"fixture"}""");
        var weights = MakeBlob(blobs, "application/vnd.ollama.image.model", "synthetic validation weights");
        string manifest = Path.Combine(manifestDir, "latest");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            schemaVersion = 2, mediaType = "application/vnd.docker.distribution.manifest.v2+json",
            config, layers = new[] { weights }
        }));
        File.WriteAllText(Path.Combine(source, "unrelated.txt"), "Keep this file");
        int activate = 0;
        bool rejected = false;
        try
        {
            await ModelStorage.Move(source, Path.Combine(area, "rejected"), false, () => { },
                (_, _, _) => throw new IOException("Simulated Ollama rejection"), _ => activate++, new NoProgress(), ct);
        }
        catch (IOException ex) when (ex.Message == "Simulated Ollama rejection") { rejected = true; }
        if (!rejected || !File.Exists(manifest) || activate != 0)
            throw new Exception("A failed verification changed the active model library.");

        var info = await ModelStorage.Move(source, target, false, () => { },
            live ? ModelStorage.VerifyWithOllama : (_, count, _) => count == 1 ? Task.CompletedTask : throw new Exception("Incorrect model count."),
            newFolder =>
            {
                if (newFolder != target || !File.Exists(manifest)) throw new Exception("The original library was removed before switching.");
                activate++;
            }, new NoProgress(), ct);
        if (activate != 1 || info.Models != 1 || info.OldFilesRetained != 0 || File.Exists(manifest) ||
            !File.Exists(Path.Combine(source, "unrelated.txt")) ||
            !File.Exists(Path.Combine(target, "manifests", "registry.ollama.ai", "library", "localdesk-fixture", "latest")))
            throw new Exception("Verified move did not switch libraries and preserve unrelated files.");
    }

    private static object MakeBlob(string folder, string mediaType, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(folder, "sha256-" + hash), bytes);
        return new { mediaType, digest = "sha256:" + hash, size = bytes.Length };
    }
}
