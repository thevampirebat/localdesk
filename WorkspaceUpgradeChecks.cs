using System.Text.Json.Nodes;
namespace LocalDesk;

static class WorkspaceUpgradeChecks
{
    public static void Run(string validation)
    {
        var root = Path.Combine(validation, "upgrade-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "project");
        var logs = Path.Combine(root, "tool-output");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "AboutPage.cs");
        File.WriteAllText(file, "first");
        var evidence = new ProjectEvidence(source, root, []);
        File.WriteAllText(file, "second");
        var change = evidence.Finish(Path.Combine(root, "snapshots")).Single();
        File.WriteAllText(file, "later");
        bool blocked = false;
        try { ChangeReview.Undo(change); }
        catch (IOException) { blocked = true; }
        if (!blocked || File.ReadAllText(file) != "later") throw new Exception("Undo overwrote a newer edit.");
        File.WriteAllText(file, "second");
        ChangeReview.Undo(change);
        if (!change.Undone || File.ReadAllText(file) != "first") throw new Exception("Undo did not restore the saved bytes.");

        var full = "BEGIN" + new string('X', 50000) + "END";
        var request = new JsonObject { ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "result-1" }) },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "result-1", ["content"] = full }) };
        ToolOutputArchive.Clip(request, 10000, text => ToolOutputArchive.Save(logs, text));
        var msg = request["messages"]![1]!;
        var clipped = msg["content"]!.ToString();
        if (msg["tool_call_id"]!.ToString() != "result-1" || clipped.Length >= 7500 ||
            !clipped.StartsWith("BEGIN") || !clipped.EndsWith("END") || File.ReadAllText(Directory.GetFiles(logs).Single()) != full)
            throw new Exception("Oversized output did not retain the full log and tool ID.");

        var other = Path.Combine(source, "Generic.cs");
        File.WriteAllText(other, "generic");
        if (!WorkspaceScope.Focus(source, root, "Update the about page", [], CancellationToken.None).Contains("AboutPage.cs"))
            throw new Exception("Project shortlist missed a matching source file.");
        if (WorkspaceScope.Focus(source, source, "Update the about page", [], CancellationToken.None).Contains("AboutPage.cs"))
            throw new Exception("Project shortlist scanned the broad working folder.");

        var library = new ProjectLibrary(root);
        library.Remember(source).BuildTarget = file;
        library.Save();
        if (new ProjectLibrary(root).Find(source)?.BuildTarget != file) throw new Exception("Saved project target was lost.");
        var copy = Path.Combine(root, "copy");
        ModelComparison.CopyProject(source, copy, CancellationToken.None);
        var copiedFile = Path.Combine(copy, "AboutPage.cs");
        File.WriteAllText(copiedFile, ModelComparison.ReplaceOnce(File.ReadAllText(copiedFile), "first", "different"));
        if (File.ReadAllText(file) != "first" || File.ReadAllText(copiedFile) != "different")
            throw new Exception("Comparison copy was not independent.");
    }

    public static async Task Live(string root)
    {
        var workspace = Path.Combine(root, "live-compare-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(workspace, "fixture");
        Directory.CreateDirectory(source);
        var code = Path.Combine(source, "Program.cs");
        const string original = "System.Console.WriteLine(\"Old label\");";
        await File.WriteAllTextAsync(code, original);
        await File.WriteAllTextAsync(Path.Combine(source, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>""");
        var trials = new List<ModelTrial>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        await ModelComparison.Run(new OllamaClient(), source, "Program.cs", "Sample.csproj",
            "Change the displayed text from Old label to New label.", "Old label", "New label",
            [LocalModel.Fast, LocalModel.Gemma], Path.Combine(workspace, "results"), 3, _ => { }, trials.Add, timeout.Token);
        if (trials.Count != 2 || !trials.Any(row => row.CorrectEdit && row.BuildExit == 0) || await File.ReadAllTextAsync(code) != original)
            throw new Exception("Model comparison did not verify an edit and build on a separate copy.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "comparison-test-path.txt"), Path.Combine(workspace, "results", "results.json"));
    }
}
