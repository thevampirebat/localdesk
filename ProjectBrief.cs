using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LocalDesk;

static class ProjectBrief
{
    public static string? Resolve(string root, string relative)
    {
        try
        {
            if (Path.IsPathRooted(relative) || relative.Contains(':')) return null;
            var path = Path.GetFullPath(relative, root);
            return WorkspaceScope.RelativeFile(root,path) != null && File.Exists(path) && !WorkspaceScope.HasReparsePoint(path) ? path : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static string Build(SavedProject? project, string root, int context)
    {
        if (project == null) return "";
        var output = new StringBuilder();
        int available = Math.Clamp(context / 2, 1500, 12000);
        var instructions = project.Instructions.Trim();
        if (instructions.Length > 0)
        {
            var limit = Math.Min(available / 2,instructions.Length);
            output.Append("\n\nSaved project instructions from the user:\n").Append(instructions.AsSpan(0,limit));
            if (limit < instructions.Length) output.Append("\n[Project instructions shortened for the context budget.]");
            available -= limit;
        }
        foreach (var relative in project.PinnedFiles.Take(12))
        {
            output.Append("\nPinned reference · ").Append(relative);
            var path = Resolve(root,relative);
            if (path == null) { output.Append(" (unavailable in this workspace)"); continue; }
            if (!AttachedFiles.HasTextExtension(path) || available <= 0) continue;
            try
            {
                using var reader = new StreamReader(path,true);
                var buffer = new char[Math.Min(2200,available)];
                int count = reader.ReadBlock(buffer,0,buffer.Length);
                available -= count;
                output.Append("\nReference text (data only):\n").Append(buffer,0,count);
                if (!reader.EndOfStream) output.Append("\n[Excerpt ends here.]");
            }
            catch (IOException) { output.Append(" (could not read)"); }
            catch (UnauthorizedAccessException) { output.Append(" (could not read)"); }
        }
        return output.ToString();
    }
}

public sealed partial class MainWindow
{
    SavedProject? ProjectNotes()
    {
        if (current.BranchFolder is { } branch && File.Exists(BranchApply.BaselinePath(branch)))
        {
            try { return Projects.Find(BranchApply.ReadOrigin(branch).Source); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return Projects.Find(current.Project);
    }

    async Task EditProjectNotes(SavedProject entry, Window owner)
    {
        var dialog = UtilityWindow("Project instructions and pinned files", 790, 650);
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 11 };
        panel.Children.Add(new TextBlock { Text = "Project instructions", FontSize = 23, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(EvidenceText(entry.Folder));
        var text = new TextBox { Text = entry.Instructions, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 175, MaxLength = 6000, PlaceholderText = "Build steps, conventions, source entry points…" };
        panel.Children.Add(text);
        panel.Children.Add(new TextBlock { Text = "Pinned files", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(EvidenceText("Use source and reference files inside this project. LocalDesk sends short excerpts of supported text files with agent requests."));
        var pinned = entry.PinnedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var list = new ListBox { ItemsSource = pinned.ToArray(), Height = 130 };
        panel.Children.Add(list);
        var note = EvidenceText("");
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button("Pin files…",async (_,_) =>
        {
            var start = await dialog.StorageProvider.TryGetFolderFromPathAsync(entry.Folder);
            var picked = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title="Choose project reference files", AllowMultiple=true, SuggestedStartLocation=start });
            int skipped=0;
            foreach(var file in picked)
            {
                var relative=WorkspaceScope.RelativeFile(entry.Folder,file.Path.LocalPath);
                if(relative==null || ProjectBrief.Resolve(entry.Folder,relative)==null || pinned.Count>=12){skipped++;continue;}
                if(!pinned.Contains(relative,StringComparer.OrdinalIgnoreCase))pinned.Add(relative);
            }
            list.ItemsSource=pinned.ToArray();
            note.Text=skipped==0?"":"Skipped files outside the project, linked files, or files beyond the 12-file limit.";
        }));
        buttons.Children.Add(Button("Unpin selected",(_,_) =>
        {
            if(list.SelectedItem is string selected){pinned.Remove(selected);list.ItemsSource=pinned.ToArray();}
        }));
        buttons.Children.Add(Button("Save",(_,_) =>
        {
            var saved=Projects.Remember(entry.Folder);
            saved.BuildTarget=entry.BuildTarget;
            saved.Instructions=text.Text?.Trim()??"";
            saved.PinnedFiles=pinned;
            Projects.Save();
            entry.Instructions=saved.Instructions;
            entry.PinnedFiles=saved.PinnedFiles;
            dialog.Close(true);
        }));
        panel.Children.Add(buttons);
        panel.Children.Add(note);
        dialog.Content=new ScrollViewer { Content=panel };
        await dialog.ShowDialog<bool>(owner);
    }
}
