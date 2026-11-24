using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocalDesk;

internal sealed record MergeFile(string Relative, string? Before, string? After, string? Conflict)
{
    public string Kind => Before == null ? "Added" : After == null ? "Deleted" : "Modified";
}

internal sealed record BranchOrigin(string Source, Dictionary<string,string> Baseline);

internal static class BranchApply
{
    public static string BaselinePath(string folder) => Path.Combine(Path.GetDirectoryName(folder)!, "origin.json");

    public static void CaptureOrigin(string source, string copy, CancellationToken ct)
    {
        var origin = new BranchOrigin(Path.GetFullPath(source), Inventory(copy, ct));
        File.WriteAllText(BaselinePath(copy), JsonSerializer.Serialize(origin));
    }

    public static BranchOrigin ReadOrigin(string copy) =>
        JsonSerializer.Deserialize<BranchOrigin>(File.ReadAllText(BaselinePath(copy))) ?? throw new IOException("The branch origin is unavailable.");

    private static Dictionary<string,string> Inventory(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder) || WorkspaceScope.HasReparsePoint(folder)) throw new IOException("The branch folder is missing or linked.");
        var files = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>();
        folders.Push(folder);
        int folderCount = 0;
        while (folders.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            if (++folderCount > 6000) throw new IOException("Too many folders to compare safely.");
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (!WorkspaceScope.Excluded.Contains(Path.GetFileName(child)) &&
                    !File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) folders.Push(child);
            }
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                if (!AttachedFiles.HasTextExtension(path) || new FileInfo(path).Length > 1024 * 1024 ||
                    File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
                if (files.Count == 18000) throw new IOException("Too many eligible files to compare safely.");
                files.Add(Path.GetRelativePath(folder, path), HashFile(path)!);
            }
        }
        return files;
    }

    private static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("Invalid project path.");
        var path = Path.GetFullPath(relative, root);
        if (WorkspaceScope.RelativeFile(root,path) is not { } inner || inner == "." || WorkspaceScope.HasReparsePoint(path))
            throw new IOException("The path is outside the project or uses a link.");
        return path;
    }

    private static string? HashFile(string path)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new IOException("A folder is using this path.");
            return null;
        }
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("This path now exceeds the 1 MiB merge limit.");
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public static List<MergeFile> Compare(string branch, BranchOrigin origin, CancellationToken ct)
    {
        if (!Directory.Exists(origin.Source)) throw new IOException("The original project folder is missing.");
        var baseline = new Dictionary<string,string>(origin.Baseline, StringComparer.OrdinalIgnoreCase);
        var current = Inventory(branch, ct);
        var changes = new List<MergeFile>();
        foreach (var relative in baseline.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string? before = baseline.GetValueOrDefault(relative);
            string? after = current.GetValueOrDefault(relative);
            if (before == after) continue;
            string? conflict = null;
            try
            {
                var branchPath = SafePath(branch, relative);
                if (after == null && (File.Exists(branchPath) || Directory.Exists(branchPath)))
                    conflict = "Branch path is no longer an eligible text file.";
                var actual = HashFile(SafePath(origin.Source, relative));
                if (actual == after && conflict == null) continue;
                if (actual != before) conflict ??= "Original changed since the branch was created.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { conflict = error.Message; }
            changes.Add(new(relative,before,after,conflict));
        }
        return changes;
    }

    public static void Apply(string branch, BranchOrigin origin, MergeFile file, string backup)
    {
        if (file.Conflict != null) throw new IOException(file.Conflict);
        var from = SafePath(branch, file.Relative);
        var to = SafePath(origin.Source, file.Relative);
        if (HashFile(from) != file.After) throw new IOException("The branch changed after review.");
        if (HashFile(to) != file.Before) throw new IOException("The original changed after review.");

        byte[]? content = file.After == null ? null : File.ReadAllBytes(from);
        if (content != null && Convert.ToHexString(SHA256.HashData(content)) != file.After)
            throw new IOException("The branch changed after review.");
        var originalParent = Path.GetDirectoryName(to)!;
        if (content != null) Directory.CreateDirectory(originalParent);
        string? temp = null;
        try
        {
            if (content != null)
            {
                temp = Path.Combine(originalParent, ".localdesk-apply-" + Guid.NewGuid().ToString("N"));
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(content);
            }
            if (file.Before == null)
            {
                File.Move(temp!, to, overwrite: false);
                return;
            }
            var saved = SafePath(backup, file.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            using (var prior = File.Open(to, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (Convert.ToHexString(SHA256.HashData(prior)) != file.Before)
                    throw new IOException("The original changed during apply.");
                prior.Position = 0;
                using var copy = new FileStream(saved, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                prior.CopyTo(copy);
            }
            if (HashFile(to) != file.Before) throw new IOException("The original changed during apply.");
            // Windows requires the destination handle to close before replacing it.
            // Retain the exact displaced file so we can check for a change during that gap.
            var displaced = Path.Combine(originalParent, ".localdesk-displaced-" + Guid.NewGuid().ToString("N"));
            if (temp == null) File.Move(to,displaced,overwrite:false);
            else File.Replace(temp,to,displaced);
            bool matches;
            try { matches=HashFile(displaced)==file.Before; }
            catch(IOException) { matches=false; }
            if (matches)
            {
                File.Delete(displaced);
                return;
            }
            try
            {
                if(temp==null)File.Move(displaced,to,overwrite:false);
                else if(HashFile(to)==file.After)File.Replace(displaced,to,null);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            { throw new IOException("The original changed during apply. Its displaced copy is at "+displaced,error); }
            if(File.Exists(displaced))
                throw new IOException("The destination changed concurrently. Its displaced copy is at "+displaced);
            throw new IOException("The original changed during apply and was restored.");
        }
        finally { if (temp != null && File.Exists(temp)) File.Delete(temp); }
    }

    public static (string Original,string Branch) Preview(string branch, BranchOrigin origin, MergeFile file)
    {
        string Read(string root)
        {
            var path = SafePath(root, file.Relative);
            if (!File.Exists(path)) return "";
            if (new FileInfo(path).Length > 1024 * 1024) return "File is too large to preview.";
            return File.ReadAllText(path);
        }
        return ChangeReview.Diff(Read(origin.Source), Read(branch));
    }
}

public sealed partial class MainWindow
{
    async Task ApplyBranchChanges()
    {
        if (busy) return;
        if (current.BranchFolder is not { } branch) { status.Text = "Open a project branch to apply its changes."; return; }
        if (!File.Exists(BranchApply.BaselinePath(branch))) { status.Text = "This branch was created before baseline tracking. Create a new branch to use conflict-checked apply."; return; }
        var dialog = UtilityWindow("Apply branch changes", 1070, 760);
        var panel = new Grid { Margin = new Thickness(22), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var heading = new TextBlock { Text = "Apply branch changes", FontSize = 24, FontWeight = FontWeight.SemiBold };
        panel.Children.Add(heading);
        var details = EvidenceText("Comparing files…");
        Grid.SetRow(details,1);
        panel.Children.Add(details);
        var files = new StackPanel { Spacing = 7 };
        var fileScroll = new ScrollViewer { Content = files, Height = 220, Margin = new Thickness(0,10,0,10) };
        Grid.SetRow(fileScroll,2);
        panel.Children.Add(fileScroll);
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        Grid.SetRow(panes,3);
        panel.Children.Add(panes);
        SelectableTextBlock Side(string label, int column)
        {
            var block = new SelectableTextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13 };
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = Brush("#202832"), Margin = new Thickness(2), };
            grid.Children.Add(new TextBlock { Text = label, Margin = new Thickness(10) });
            var scroll = new ScrollViewer { Content = block, Margin = new Thickness(10) };
            Grid.SetRow(scroll,1);
            grid.Children.Add(scroll);
            Grid.SetColumn(grid,column);
            panes.Children.Add(grid);
            return block;
        }
        var originalText = Side("Original project",0);
        var branchText = Side("Branch",2);
        var actions = new StackPanel { Spacing = 7, Margin = new Thickness(0,12,0,0) };
        Grid.SetRow(actions,4);
        panel.Children.Add(actions);
        var note = EvidenceText("");
        var apply = Button("Apply selected files",null);
        apply.IsEnabled = false;
        actions.Children.Add(apply);
        actions.Children.Add(note);
        using var lifetime = new CancellationTokenSource();
        dialog.Closed += (_,_)=>lifetime.Cancel();
        bool writing = false;
        dialog.Closing += (_,e)=>{if(writing)e.Cancel=true;};
        BranchOrigin? origin = null;
        var rows = new List<(MergeFile File,CheckBox Check)>();
        dialog.Opened += async (_,_) =>
        {
            try
            {
                origin = BranchApply.ReadOrigin(branch);
                details.Text = "Apply to · " + origin.Source + "\nChoose files to apply. Conflicts are blocked and originals are backed up.";
                var changes = await Task.Run(()=>BranchApply.Compare(branch,origin,lifetime.Token),lifetime.Token);
                if(lifetime.IsCancellationRequested)return;
                if(changes.Count==0)note.Text="There are no eligible changes to apply.";
                foreach(var change in changes)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0,0,10,0) };
                    var check = new CheckBox { IsEnabled = change.Conflict==null, Margin = new Thickness(0,0,6,0) };
                    check.IsCheckedChanged += (_,_)=>apply.IsEnabled=rows.Any(r=>r.Check.IsChecked==true);
                    row.Children.Add(check);
                    var desc = EvidenceText(change.Kind + " · " + change.Relative + (change.Conflict==null?"":"\nConflict · "+change.Conflict));
                    Grid.SetColumn(desc,1);
                    row.Children.Add(desc);
                    var preview = Button("Review",async (_,_) =>
                    {
                        try
                        {
                            var view=await Task.Run(()=>BranchApply.Preview(branch,origin,change));
                            if(!lifetime.IsCancellationRequested){originalText.Text=view.Original;branchText.Text=view.Branch;}
                        }
                        catch(Exception error){if(!lifetime.IsCancellationRequested)note.Text=error.Message;}
                    });
                    Grid.SetColumn(preview,2);
                    row.Children.Add(preview);
                    rows.Add((change,check));
                    files.Children.Add(row);
                }
            }
            catch(OperationCanceledException){ }
            catch(Exception error){note.Text=error.Message;}
        };
        apply.Click += async (_,_) =>
        {
            if(writing||origin==null)return;
            var selection=rows.Where(row=>row.Check.IsChecked==true).ToList();
            if(selection.Count==0)return;
            writing=true;
            apply.IsEnabled=false;
            files.IsEnabled=false;
            var backup=Path.Combine(store.Root,"branch-backups",current.Id,Guid.NewGuid().ToString("N"));
            var errors=new List<string>();
            int done=0;
            foreach(var row in selection)
            {
                try{await Task.Run(()=>BranchApply.Apply(branch,origin,row.File,backup));done++;row.Check.IsChecked=false;row.Check.IsEnabled=false;}
                catch(Exception error){errors.Add(row.File.Relative+" · "+error.Message);}
            }
            writing=false;
            files.IsEnabled=true;
            note.Text=$"Applied {done} of {selection.Count} files."+ (errors.Count==0?"":"\n"+string.Join("\n",errors));
            if(done>0)
            {
                AddActivity("branch apply",$"Applied {done} files to {origin.Source}. Original file backups: {backup}");
                agent.Dispose();
                foreach(var chat in store.Chats.Where(chat=>chat.Project.Equals(origin.Source,StringComparison.OrdinalIgnoreCase)))
                {
                    chat.AgentSession=null;chat.BridgeSummary="";chat.BridgeBoundary="";
                    chat.ContextSummary+="\nLocalDesk applied reviewed branch changes to the project. Re-read affected files before editing.";
                }
                store.Save();
            }
        };
        dialog.Content=panel;
        await dialog.ShowDialog(this);
    }
}
