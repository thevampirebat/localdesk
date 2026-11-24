using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json;

namespace LocalDesk;

public sealed class SavedProject
{
    public string Folder { get; set; } = "";
    public string? BuildTarget { get; set; }
    public FocusedCheck? Verification { get; set; }
    public bool RepairChecks { get; set; } = true;
    public bool ApproveProjectEdits { get; set; }
    public bool ApproveConfiguredChecks { get; set; }
    public string Instructions { get; set; } = "";
    public List<string> PinnedFiles { get; set; } = [];
    public List<string> LastExecutables { get; set; } = [];
    public DateTime? LastSuccessfulBuild { get; set; }
    public override string ToString() => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) + "   ·   " + Folder;
}

public sealed class ProjectLibrary
{
    private readonly string path;
    public List<SavedProject> Items { get; }

    public ProjectLibrary(string data)
    {
        path = Path.Combine(data, "projects.json");
        if (!File.Exists(path)) { Items = []; return; }
        try { Items = JsonSerializer.Deserialize<List<SavedProject>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { Items = []; }
    }

    public SavedProject? Find(string folder) => Items.FirstOrDefault(p =>
        Path.TrimEndingDirectorySeparator(p.Folder).Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase));

    public SavedProject Remember(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var entry = Find(folder);
        if (entry != null) return entry;
        entry = new SavedProject { Folder = folder };
        Items.Add(entry);
        return entry;
    }

    public void Save()
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

public sealed record ProjectRun(Conversation Chat, ChatMessage Reply, string Request);

public static class ProjectRunHistory
{
    public static IReadOnlyList<ProjectRun> Recent(IEnumerable<Conversation> chats, string folder, int limit)
    {
        var runs = new List<ProjectRun>();
        var normalized = Path.TrimEndingDirectorySeparator(folder);
        foreach (var chat in chats)
        {
            var request = "";
            foreach (var message in chat.Messages)
            {
                if (message.Role == "user") request = message.Text;
                else if (message.ProjectUsed is { } used && message.Summary != null &&
                    Path.TrimEndingDirectorySeparator(used).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    runs.Add(new(chat, message, request));
            }
        }
        return runs.AsEnumerable().Reverse().OrderByDescending(run => run.Reply.CompletedAt ?? run.Chat.Updated)
            .Take(limit).ToList();
    }
}

public sealed partial class MainWindow
{
    ProjectLibrary? projectLibrary;
    ProjectLibrary Projects => projectLibrary ??= new ProjectLibrary(store.Root);

    void RememberBuildTarget()
    {
        if (!Directory.Exists(current.Project) || WorkspaceScope.IsBroad(current.Project, preferences.Folder)) return;
        var entry = Projects.Remember(current.Project);
        entry.BuildTarget = current.BuildProject;
        Projects.Save();
    }

    void RememberBuild(BuildResult build)
    {
        if (build.ExitCode != 0 || WorkspaceScope.IsBroad(current.Project, preferences.Folder)) return;
        var entry = Projects.Remember(current.Project);
        entry.BuildTarget = current.BuildProject;
        entry.LastExecutables = build.Executables.Where(p => WorkspaceScope.RelativeFile(entry.Folder, p) != null).ToList();
        entry.LastSuccessfulBuild = DateTime.Now;
        Projects.Save();
    }

    async Task ManageProjects()
    {
        if (busy) return;
        CommitProject();
        var dialog = UtilityWindow("Projects", 1040, 750);
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Projects", FontSize = 24 });
        var listing = Projects.Items.ToList();
        if (Projects.Find(current.Project) == null && Directory.Exists(current.Project) &&
            !WorkspaceScope.IsBroad(current.Project, preferences.Folder))
            listing.Insert(0, new SavedProject { Folder = current.Project, BuildTarget = current.BuildProject });
        var list = new ListBox { ItemsSource = listing, SelectedItem = listing.FirstOrDefault(p => p.Folder.Equals(current.Project, StringComparison.OrdinalIgnoreCase)), Height = 130 };
        var details = new StackPanel { Spacing = 6 };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var note = EvidenceText("");
        void UpdateDetails()
        {
            details.Children.Clear();
            if (list.SelectedItem is not SavedProject entry)
            {
                details.Children.Add(EvidenceText("Save the current source folder or select one from the list."));
                return;
            }
            details.Children.Add(EvidenceText("Source folder · " + entry.Folder));
            details.Children.Add(EvidenceText("Build target · " + (entry.BuildTarget ?? "None selected")));
            details.Children.Add(Button(entry.PinnedFiles.Count==0 ? "Instructions and pinned files…" : $"Instructions and pinned files ({entry.PinnedFiles.Count})…",
                async (_,_)=>{await EditProjectNotes(entry,dialog);UpdateDetails();}));
            details.Children.Add(Button("Focused verification…",async(_,_)=>{await EditFocusedCheck(entry,dialog);UpdateDetails();}));
            details.Children.Add(Button("Permission presets…",async(_,_)=>await EditProjectPermissions(entry,dialog)));
            if(entry.Verification is { } check)details.Children.Add(EvidenceText(check.Kind+" · "+check.RelativePath));
            foreach(var relative in entry.PinnedFiles)
                if(ProjectBrief.Resolve(entry.Folder,relative) is { } path)details.Children.Add(PreviewLink("Pinned · "+relative,path,dialog));
            var recent = ProjectRunHistory.Recent(store.Chats, entry.Folder, 12);
            var lastSuccessful = recent.FirstOrDefault(run => run.Reply.BuildCheck?.ExitCode == 0)?.Reply;
            var savedBuild = lastSuccessful?.BuildCheck;
            var builtAt = entry.LastSuccessfulBuild ?? lastSuccessful?.CompletedAt;
            details.Children.Add(EvidenceText(builtAt is { } when
                ? "Last successful build · " + when.ToString("g") : savedBuild != null ? "Successful build recorded" : "No successful build recorded"));
            var executables = entry.LastExecutables.Count > 0 ? entry.LastExecutables : savedBuild?.Executables ?? [];
            if (executables.Count > 0) details.Children.Add(new TextBlock { Text = "Latest successful build outputs", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var exe in executables)
            {
                if (File.Exists(exe)) details.Children.Add(EvidenceLink("Show " + Path.GetFileName(exe), exe));
                else details.Children.Add(EvidenceText("Build output is missing · " + exe));
            }

            details.Children.Add(new TextBlock { Text = "Recent runs", FontSize = 18, Margin = new Thickness(0, 12, 0, 3) });
            if (recent.Count == 0) details.Children.Add(EvidenceText("There are no recorded agent runs for this project."));
            foreach (var run in recent)
            {
                var reply = run.Reply;
                var build = reply.BuildCheck;
                string state = reply.PauseReason != null ? "Paused" : reply.Successful ? "Finished" : "Incomplete";
                string buildState = build == null ? "No build check" : build.ExitCode == 0 ? "Build passed" : $"Build failed ({build.ExitCode})";
                string request = string.Join(" ", run.Request.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (request.Length > 120) request = request[..117] + "…";
                var card = new StackPanel { Spacing = 5 };
                card.Children.Add(new TextBlock { Text = request.Length == 0 ? run.Chat.Title : request, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
                var metadata = new List<string> { state, buildState, $"{reply.VerifiedFiles.Count} verified file changes" };
                if (reply.CompletedAt is { } completed) metadata.Insert(0, completed.ToString("g"));
                if(reply.Focused is { } focused)metadata.Add(focused.Passed?"Focused check passed":"Focused check failed");
                if (reply.RunModel is { } modelId) metadata.Add(LocalModel.FromTag(modelId).Name);
                if (reply.RunSeconds is { } elapsed) metadata.Add(TimeSpan.FromSeconds(elapsed).ToString(@"m\:ss"));
                card.Children.Add(EvidenceText(string.Join(" · ", metadata)));
                var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(Button("Open chat", (_, _) =>
                {
                    if (!ReferenceEquals(current, run.Chat)) { Show(run.Chat); RefreshHistory(); }
                    dialog.Close();
                }));
                if (build != null && File.Exists(build.LogPath)) buttons.Children.Add(EvidenceLink("View build log", build.LogPath, dialog));
                card.Children.Add(buttons);
                details.Children.Add(new Border { Child = card, Background = Brush("#252E38"), Padding = new Thickness(10), CornerRadius = new CornerRadius(8) });
            }
        }
        list.SelectionChanged += (_, _) => UpdateDetails();
        var save = Button("Save current folder", (_, _) =>
        {
            if (!Directory.Exists(current.Project) || WorkspaceScope.IsBroad(current.Project, preferences.Folder))
            {
                note.Text = "Choose a specific source folder before saving it as a project.";
                return;
            }
            RememberBuildTarget();
            list.ItemsSource = Projects.Items.ToList();
            list.SelectedItem = Projects.Find(current.Project);
            note.Text = "Project saved.";
        });
        var use = Button("Use project", (_, _) =>
        {
            if (list.SelectedItem is not SavedProject entry) return;
            if(current.BranchFolder!=null){note.Text="This chat is tied to its branch copy. Use a new chat for another project.";return;}
            if (!Directory.Exists(entry.Folder)) { note.Text = "The source folder no longer exists."; return; }
            project.Text = entry.Folder;
            CommitProject();
            current.BuildProject = entry.BuildTarget;
            mode.SelectedIndex = 1;
            Save();
            dialog.Close();
        });
        foreach (var b in new[] { save, use }) { b.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(b); }
        panel.Children.Add(actions);
        panel.Children.Add(list);
        panel.Children.Add(details);
        panel.Children.Add(note);
        UpdateDetails();
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(this);
    }
}
