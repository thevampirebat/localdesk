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
    }
}
