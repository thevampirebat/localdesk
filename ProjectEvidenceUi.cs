using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LocalDesk;

public sealed partial class MainWindow
{
    bool IsBuildProject(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !new[] { ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return false;
        var relative = Path.GetRelativePath(current.Project, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    async Task ChooseBuildCheck()
    {
        if (busy) return;
        if(current.BranchFolder!=null){status.Text="Automatic builds are disabled in project branches.";return;}
        CommitProject();
        var dialog = new Window { Title = "Build check", Width = 530, Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#1C232B") };
        var content = new StackPanel { Margin = new Avalonia.Thickness(22), Spacing = 12 };
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        label.Text = current.BuildProject is { } path && IsBuildProject(path)
            ? "After each completed agent run, LocalDesk builds " + Path.GetRelativePath(current.Project, path) + " in Release mode."
            : "Select a .NET project or solution in this workspace to build after each completed agent run. The result card will show the exit code and log.";
        content.Children.Add(label);
        content.Children.Add(Button("Choose project or solution…", async (_, _) =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a .NET build target",
                FileTypeFilter = [new FilePickerFileType(".NET project or solution") { Patterns = ["*.csproj", "*.fsproj", "*.vbproj", "*.sln", "*.slnx"] }]
            });
            if (files.Count == 0) return;
            var file = files[0].Path.LocalPath;
            if (!IsBuildProject(file))
            {
                label.Text = "Choose an existing .NET project or solution inside the current working folder.";
                return;
            }
            current.BuildProject = file;
            RememberBuildTarget();
            Save();
            dialog.Close();
        }));
        content.Children.Add(Button("Turn off build check", (_, _) =>
        {
            current.BuildProject = null;
            RememberBuildTarget();
            Save();
            dialog.Close();
        }));
        content.Children.Add(Button("Close", (_, _) => dialog.Close()));
        dialog.Content = content;
        await dialog.ShowDialog(this);
    }

    void AddEvidence(StackPanel body, ChatMessage message)
    {
        if (message.ProjectUsed is not { } folder) return;
        if (message.BuildCheck is { } build)
            foreach (var exe in build.Executables.Where(File.Exists))
                body.Children.Add(EvidenceLink("Build output · " + Path.GetFileName(exe), exe));
        if (message.EvidenceNote is { } note) body.Children.Add(EvidenceText(note));
        if (message.PauseReason is { } reason)
        {
            body.Children.Add(EvidenceText(reason));
            var index = current.Messages.IndexOf(message);
            var user = current.Messages.Take(index).LastOrDefault(m => m.Role == "user");
            if (user != null && index == current.Messages.Count - 1)
                body.Children.Add(Button("Resume from here", async (_, _) =>
                {
                    if (busy) return;
                    CommitProject();
                    if (current.Mode != "agent" || !string.Equals(current.Project, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        status.Text = "Switch back to Project agent and restore " + folder + " to resume.";
                        return;
                    }
                    if(RunJournal.Load(store.Root,current.Id)?.Reply==message.Marker)await ResumeSaved();
                    else await Send(user,message);
                }));
        }
    }

    static TextBlock EvidenceText(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    Button EvidenceLink(string title, string path, Window? owner = null)
    {
        if (AttachedFiles.HasTextExtension(path) || AttachedFiles.IsImage(path)) return PreviewLink(title,path,owner);
        var link = Button(title, (_, _) => RevealFile(path));
        ToolTip.SetTip(link, path);
        EnableFileDrag(link, path);
        return link;
    }
}
