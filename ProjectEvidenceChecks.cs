using System.Text;

namespace LocalDesk;

static class ProjectEvidenceChecks
{
    public static void Run(string root)
    {
        var workspace = Path.Combine(root, "evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var tracked = Path.Combine(workspace, "tracked.cs");
        var unrelated = Path.Combine(workspace, "unrelated.cs");
        File.WriteAllText(tracked, "old");
        File.WriteAllText(unrelated, "untouched");
        var evidence = new ProjectEvidence(workspace, workspace, [tracked]);
        File.WriteAllText(tracked, "new");
        File.WriteAllText(unrelated, "changed");
        var changes = evidence.Finish();
        if (changes is not [{ Change: "Modified", Diff: var diff }] || diff == null || !diff.Contains("- old\n+ new"))
            throw new Exception("Default workspace evidence should cover the dropped file only and show the diff.");
        var project = new ProjectEvidence(workspace, root, []);
        File.Delete(tracked);
        var created = Path.Combine(workspace, "new.cs");
        File.WriteAllText(created, "added");
        changes = project.Finish();
        if (!changes.Any(f => f.Path == tracked && f.Change == "Deleted") ||
            !changes.Any(f => f.Path == created && f.Change == "Added"))
            throw new Exception("Selected project evidence missed added or deleted files.");

        var loop = new ToolLoop();
        for (int i = 0; i < 3; i++)
            if (loop.Observe("search alpha") != null) throw new Exception("Loop paused early.");
        if (loop.Observe("search beta") != null) throw new Exception("Different search paused the request.");
        for (int i = 0; i < 3; i++) loop.Observe("search beta");
        if (loop.Observe("search beta") == null) throw new Exception("Repeated calls were not paused.");
        if (loop.Outcome("failed") != null || loop.Outcome("failed") != null || loop.Outcome("completed") != null ||
            loop.Outcome("failed") != null || loop.Outcome("failed") != null || loop.Outcome("failed") == null)
            throw new Exception("Loop failure streak did not reset on a successful tool.");

        var progress = new EditRunProgress(new ProjectEvidence(workspace, root, []));
        for (int i = 0; i < 3; i++)
        {
            progress.ToolAllowed("read_files");
            progress.ToolAllowed("run_commands");
            if (progress.BeforeCompaction() != null) throw new Exception("Edit progress guard paused too early.");
        }
        if (progress.BeforeCompaction() != null) throw new Exception("Compactions alone paused an edit.");
        progress.ToolAllowed("search_files");
        progress.ToolAllowed("read_files");
        File.WriteAllText(created, "edited after inspecting the project");
        if (progress.BeforeCompaction() != null) throw new Exception("An edit with verified progress paused.");
        for (int i = 0; i < 8; i++) progress.ToolAllowed(i % 2 == 0 ? "read_files" : "run_commands");
        for (int i = 0; i < 2; i++)
            if (progress.BeforeCompaction() != null) throw new Exception("Verified progress did not reset the guard.");
        if (progress.BeforeCompaction() == null) throw new Exception("Repeated exploration and compaction did not pause a stalled edit.");

        var editRequest = "I'd like to add some things to this firefox extension. I'd like you to put it into its own folder.";
        foreach (var report in new[] { "It still doesn't have the delete option", "It's still not showing that option.", "The slow middle mouse click scroll is still there in the watchlist", "Ok it is showing up now but doesn't allow me to confirm deletion" })
            if (!EditRunOutcome.ExpectsChanges(report, true) || EditRunOutcome.ExpectsChanges(report))
                throw new Exception("Incorrect edit follow-up classification: " + report);
        var prior = new ChatMessage { Role = "assistant", ProjectUsed = workspace, Successful = true, Summary = "Here is the plan." };
        var store = new ChatStore(Path.Combine(workspace, "data"));
        store.Chats.Add(new Conversation { Messages = [new() { Text = editRequest }, prior,
            new() { Text = "Explain how to fix this error without touching the files." },
            new() { Role = "assistant", ProjectUsed = workspace, Successful = true, Summary = "The error is in the config." }] });
        store.Save();
        var history = new ChatStore(store.Root).Chats.Single().Messages;
        if (history[1].Successful || history[1].EvidenceNote != EditRunOutcome.NoVerifiedChanges || !history[3].Successful)
            throw new Exception("Previously saved edit results or read-only results were classified incorrectly.");
        prior.VerifiedFiles.Add(new(created, "Modified", null));
        if (EditRunOutcome.MarkUnverified(prior, true)) throw new Exception("An edit with verified files was marked incomplete.");
    }
}
