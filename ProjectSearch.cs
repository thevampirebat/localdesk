using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LocalDesk;

internal sealed record ProjectHit(string Path,string Relative,int Line,string Excerpt,int Score)
{
    public override string ToString()=>$"{Relative}:{Line}\n{Excerpt}";
}

internal sealed class ProjectSearchIndex(string root)
{
    const int MaxFiles=1000;
    const long MaxBytes=18L*1024*1024;
    sealed record Document(long Bytes,DateTime Modified,string[] Lines);
    readonly Dictionary<string,Document> documents=new(StringComparer.OrdinalIgnoreCase);

    internal int FileCount=>documents.Count;
    internal IReadOnlyList<ProjectHit> Search(string query,CancellationToken ct)
    {
        Refresh(ct);
        var terms=Regex.Matches(query,"[\\p{L}\\p{N}_]{2,}").Select(m=>m.Value)
            .Where(term=>!StopWords.Contains(term)).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        if(terms.Length==0)return [];
        var results=new List<ProjectHit>();
        foreach(var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            var relative=Path.GetRelativePath(root,document.Key);
            var hits=new List<ProjectHit>();
            for(int i=0;i<document.Value.Lines.Length;i++)
            {
                var line=document.Value.Lines[i];
                var matches=terms.Count(term=>line.Contains(term,StringComparison.OrdinalIgnoreCase));
                if(matches==0)continue;
                int score=3*matches+(line.Contains(query,StringComparison.OrdinalIgnoreCase)?16:0);
                hits.Add(new(document.Key,relative,i+1,line.Length>300?line[..300]+"…":line,score));
            }
            if(hits.Count==0&&terms.Any(term=>relative.Contains(term,StringComparison.OrdinalIgnoreCase)))
                hits.Add(new(document.Key,relative,1,"File name matches",2));
            results.AddRange(hits.OrderByDescending(item=>item.Score).Take(4));
        }
        return results.OrderByDescending(item=>item.Score).ThenBy(item=>item.Relative,StringComparer.OrdinalIgnoreCase).ThenBy(item=>item.Line).Take(32).ToArray();
    }

    static readonly HashSet<string> StopWords=new(["the","and","for","with","this","that","can","please","from","file","add","make"],StringComparer.OrdinalIgnoreCase);

    private void Refresh(CancellationToken ct)
    {
        var folders=new Queue<string>();
        folders.Enqueue(root);
        var kept=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int visited=0,entries=0;
        long bytes=0;
        while(folders.TryDequeue(out var folder)&&visited++<300&&entries<8000&&kept.Count<MaxFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if(WorkspaceScope.HasReparsePoint(folder))continue;
                foreach(var file in Directory.EnumerateFiles(folder))
                {
                    if(entries++>=8000||kept.Count>=MaxFiles)break;
                    ct.ThrowIfCancellationRequested();
                    if(!AttachedFiles.HasTextExtension(file)||WorkspaceScope.HasReparsePoint(file))continue;
                    var name=Path.GetFileName(file);
                    if(name.StartsWith('.')||name.Contains("secret",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".min.js",StringComparison.OrdinalIgnoreCase)||
                        name.EndsWith(".lock",StringComparison.OrdinalIgnoreCase)||name.Equals("package-lock.json",StringComparison.OrdinalIgnoreCase))continue;
                    try
                    {
                        var info=new FileInfo(file);
                        if(info.Length>192*1024||bytes+info.Length>MaxBytes)continue;
                        if(!documents.TryGetValue(file,out var prior)||prior.Bytes!=info.Length||prior.Modified!=info.LastWriteTimeUtc)
                            documents[file]=new(info.Length,info.LastWriteTimeUtc,File.ReadAllLines(file));
                        kept.Add(file);
                        bytes+=info.Length;
                    }
                    catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
                }
                foreach(var child in Directory.EnumerateDirectories(folder).Take(300))
                    if(folders.Count<300&&!WorkspaceScope.Excluded.Contains(Path.GetFileName(child))&&!Path.GetFileName(child).StartsWith('.'))folders.Enqueue(child);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
        }
        foreach(var file in documents.Keys.Where(path=>!kept.Contains(path)).ToArray())documents.Remove(file);
    }
}

public sealed partial class MainWindow
{
    ProjectSearchIndex? projectIndex;
    string? indexRoot;

    async Task ShowProjectSearch()
    {
        if(busy)return;
        CommitProject();
        if(!Directory.Exists(current.Project)||WorkspaceScope.IsBroad(current.Project,preferences.Folder))
        {status.Text="Choose a specific source folder in Project agent before indexing.";return;}
        var root=current.Project;
        if(!string.Equals(root,indexRoot,StringComparison.OrdinalIgnoreCase)){indexRoot=root;projectIndex=new(root);}
        var index=projectIndex!;
        var dialog=UtilityWindow("Search project files",850,650);
        var layout=new Grid {Margin=new Thickness(20),RowDefinitions=new RowDefinitions("Auto,*,Auto")};
        var top=new StackPanel {Spacing=7};
        var query=new TextBox {PlaceholderText="Search source and notes",MaxLength=350};
        var search=Button("Search and refresh index",null);
        var info=EvidenceText(root);
        top.Children.Add(query);top.Children.Add(search);top.Children.Add(info);
        layout.Children.Add(top);
        var list=new ListBox {SelectionMode=SelectionMode.Multiple};
        Grid.SetRow(list,1);layout.Children.Add(list);
        using var lifetime=new CancellationTokenSource();
        dialog.Closed+=(_,_)=>lifetime.Cancel();
        async Task Search()
        {
            var term=query.Text?.Trim();
            if(!search.IsEnabled||string.IsNullOrWhiteSpace(term))return;
            search.IsEnabled=false;
            info.Text="Updating the local file index…";
            try
            {
                var results=await Task.Run(()=>{lock(index)return index.Search(term,lifetime.Token);},lifetime.Token);
                list.ItemsSource=results;
                info.Text=$"{index.FileCount:N0} text files indexed · {results.Count:N0} matches";
            }
            catch(OperationCanceledException) { }
            finally {search.IsEnabled=true;}
        }
        search.Click+=async(_,_)=>await Search();
        query.KeyDown+=async(_,e)=>{if(e.Key==Avalonia.Input.Key.Enter){e.Handled=true;await Search();}};
        var actions=new WrapPanel {Orientation=Orientation.Horizontal};
        actions.Children.Add(Button("Preview selected file",async(_,_)=>{if(list.SelectedItem is ProjectHit hit)await PreviewFile(hit.Path,dialog,name:hit.Relative+":"+hit.Line,firstLine:hit.Line);}));
        actions.Children.Add(Button("Add citations to draft",(_,_)=>
        {
            var citations=list.SelectedItems?.OfType<ProjectHit>().Take(12).Select(hit=>$"{hit.Relative}:{hit.Line} · {hit.Excerpt}").ToArray()??[];
            if(citations.Length>0)input.Text=(input.Text??"").TrimEnd()+"\n\nProject excerpts (reference data):\n"+string.Join("\n",citations);
            dialog.Close();
        }));
        Grid.SetRow(actions,2);layout.Children.Add(actions);
        dialog.Content=layout;
        await dialog.ShowDialog(this);
    }
}
