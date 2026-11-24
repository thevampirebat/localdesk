using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocalDesk;

public sealed record FocusedCheck(string Kind, string RelativePath, string Expected);
public sealed record FocusedResult(bool Passed, string Summary, string? LogPath = null);

internal static class Verification
{
    public const int MaxRepairs = 2;

    public static string? Target(string root, FocusedCheck check,bool allowMissingText=false)
    {
        if(check.Kind is not ("Text present" or "Text absent" or ".NET test filter") ||
            string.IsNullOrWhiteSpace(check.Expected)||check.Expected.Length>2000)return null;
        var path=ProjectBrief.Resolve(root,check.RelativePath);
        if(path==null && allowMissingText && check.Kind!=".NET test filter")
        {
            try
            {
                if(!Path.IsPathRooted(check.RelativePath) && !check.RelativePath.Contains(':'))
                {
                    var future=Path.GetFullPath(check.RelativePath,root);
                    if(WorkspaceScope.RelativeFile(root,future)!=null&&!WorkspaceScope.HasReparsePoint(future)&&!Directory.Exists(future))
                        path=future;
                }
            }
            catch(Exception error) when(error is IOException or ArgumentException or UnauthorizedAccessException){return null;}
        }
        if(path==null)return null;
        if(check.Kind==".NET test filter")
            return Path.GetExtension(path).Equals(".csproj",StringComparison.OrdinalIgnoreCase)?path:null;
        return AttachedFiles.HasTextExtension(path)?path:null;
    }

    public static async Task<FocusedResult> CheckText(string root,FocusedCheck check,CancellationToken ct)
    {
        var path=Target(root,check);
        if(path==null)return new(false,"The focused check file is missing or outside the project.");
        const long limit=2L*1024*1024;
        using var stream=File.OpenRead(path);
        if(stream.Length>limit)return new(false,"The focused check file exceeds the 2 MiB limit.");
        using var reader=new StreamReader(stream);
        var text=await reader.ReadToEndAsync(ct);
        var present=text.Contains(check.Expected,StringComparison.Ordinal);
        var passed=check.Kind=="Text present"?present:!present;
        return new(passed, check.RelativePath+
            (check.Kind=="Text present"?" should contain ":" should omit ")+check.Expected);
    }

    static readonly Regex ErrorLine=new(@"\berror\s+[A-Z]+\d+\s*:|\berror\s*:|\bFailed\s+[^\s]",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    public static async Task<string> Errors(string path,CancellationToken ct)
    {
        if(!File.Exists(path))return "The check failed without a readable log.";
        const int cap=8, chars=700;
        var unique=new HashSet<string>(StringComparer.Ordinal);
        var errors=new List<string>();
        var tail=new Queue<string>();
        using var stream=File.OpenRead(path);
        if(stream.Length>4*1024*1024)return "Check log is too large to summarize. Inspect specific errors from the log.";
        using var reader=new StreamReader(stream);
        int scanned=0;
        while(scanned++<15000 && await reader.ReadLineAsync(ct) is { } line)
        {
            line=line.Trim();
            if(line.Length==0)continue;
            if(line.Length>chars)line=line[..chars]+"…";
            tail.Enqueue(line);
            if(tail.Count>6)tail.Dequeue();
            if(ErrorLine.IsMatch(line)&&unique.Add(line))errors.Add(line);
            if(errors.Count==cap)break;
        }
        return string.Join("\n", errors.Count>0?errors:tail.ToList());
    }

    public static (int Passed,int Failed) TestCounts(string results)
    {
        if(!Directory.Exists(results))return (0,0);
        int passed=0,failed=0;
        foreach(var path in Directory.EnumerateFiles(results,"*.trx").Take(24))
        {
            if(new FileInfo(path).Length>8*1024*1024)continue;
            try
            {
                using var reader=System.Xml.XmlReader.Create(path,new System.Xml.XmlReaderSettings{
                    DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=12*1024*1024});
                while(reader.Read())
                {
                    if(reader.NodeType!=System.Xml.XmlNodeType.Element || reader.LocalName!="Counters" || reader.Depth!=2)continue;
                    if(int.TryParse(reader.GetAttribute("passed"),out int p))passed+=Math.Clamp(p,0,100000);
                    if(int.TryParse(reader.GetAttribute("failed"),out int f))failed+=Math.Clamp(f,0,100000);
                    break;
                }
            }
            catch(System.Xml.XmlException){return (0,1);}
        }
        return (passed,failed);
    }
}

public sealed partial class MainWindow
{
    async Task EditFocusedCheck(SavedProject entry,Window owner)
    {
        var dialog=UtilityWindow("Focused verification",760,495);
        var panel=new StackPanel { Margin=new Thickness(22),Spacing=12 };
        panel.Children.Add(new TextBlock { Text="Focused verification",FontSize=24,FontWeight=FontWeight.SemiBold });
        panel.Children.Add(EvidenceText(entry.Folder));
        var kinds=new[]{"Text present","Text absent",".NET test filter"};
        var kind=new ComboBox { ItemsSource=kinds,SelectedItem=entry.Verification?.Kind??kinds[0] };
        var file=new TextBox { Text=entry.Verification?.RelativePath,PlaceholderText="Project-relative file or test .csproj path" };
        var expected=new TextBox { Text=entry.Verification?.Expected,PlaceholderText="Exact text or test filter",MaxLength=2000,
            AcceptsReturn=true, Height=105,TextWrapping=TextWrapping.Wrap };
        panel.Children.Add(kind);
        panel.Children.Add(file);
        panel.Children.Add(expected);
        var repair=new CheckBox { Content="Offer up to two repair attempts when a build or focused check fails",IsChecked=entry.RepairChecks };
        panel.Children.Add(repair);
        var note=EvidenceText("A text check verifies exact content. A .NET test filter must match at least one passing test.");
        panel.Children.Add(note);
        var actions=new WrapPanel { Orientation=Orientation.Horizontal };
        void Save(FocusedCheck? check)
        {
            var saved=Projects.Remember(entry.Folder);
            if(!ReferenceEquals(saved,entry)&&saved.BuildTarget==null)saved.BuildTarget=entry.BuildTarget;
            saved.Verification=entry.Verification=check;
            saved.RepairChecks=entry.RepairChecks=repair.IsChecked==true;
            Projects.Save();
            dialog.Close();
        }
        actions.Children.Add(Button("Save check",(_,_) =>
        {
            var check=new FocusedCheck(kind.SelectedItem?.ToString()??"", file.Text?.Trim()??"", expected.Text??"");
            if(Verification.Target(entry.Folder,check,allowMissingText:true)==null)
            {
                note.Text="Enter a project-relative text file or existing test .csproj, and a nonempty expected value.";
                return;
            }
            Save(check);
        }));
        actions.Children.Add(Button("Save without a focused check",(_,_)=>Save(null)));
        actions.Children.Add(Button("Cancel",(_,_)=>dialog.Close()));
        panel.Children.Add(actions);
        dialog.Content=new ScrollViewer { Content=panel };
        await dialog.ShowDialog(owner);
    }

    async Task<FocusedResult> RunFocusedCheck(ChatMessage reply,FocusedCheck check,int attempt,CancellationToken ct)
    {
        if(check.Kind!=".NET test filter")
            return await Verification.CheckText(current.Project,check,ct);
        var path=Verification.Target(current.Project,check);
        if(path==null)return new(false,"The selected test project is unavailable.");
        var log=Path.Combine(store.Root,"build-checks",current.Id,reply.Marker+$"-test-{attempt}.log");
        var results=Path.Combine(Path.GetDirectoryName(log)!,reply.Marker+"-results-"+Guid.NewGuid().ToString("N"));
        var args=new[]{"test",path,"-c","Release","--nologo","--filter",check.Expected,"--results-directory",results,"--logger","trx;LogFilePrefix=focused"};
        var proposal=new ToolProposal("focused .NET test",System.Text.Json.JsonSerializer.Serialize(new {
            working_folder=current.Project,executable="dotnet",arguments=args
        }));
        if(!await TimedReview(proposal,ct,true))throw new AgentClient.LoopException("Focused test declined.");
        status.Text="Running the focused test…";
        var result=await ProjectEvidence.RunDotnet(current.Project,args,log,ct);
        var counts=Verification.TestCounts(results);
        bool passed=result.ExitCode==0 && counts.Passed>0 && counts.Failed==0;
        return new(passed, passed ? $"{counts.Passed} filtered test(s) passed." :
            result.ExitCode==0?"No passing tests could be confirmed for this filter.":$"Filtered tests failed (exit {result.ExitCode}).",log);
    }

    async Task RunChecks(ChatMessage reply,RunCheckpoint state,CancellationToken ct)
    {
        if(current.BranchFolder==null && state.BuildTarget is { } target && state.Build==null)
        {
            if(!IsBuildProject(target))throw new IOException("The saved build target is no longer available.");
            state.Build=reply.BuildCheck=await RunBuildCheck(reply,target,ct,state.RepairAttempts);
            RunJournal.Save(store.Root,current.Id,state);
            Save();
        }
        if(state.FocusedCheck!=null && state.Focused==null && (current.BranchFolder==null || state.FocusedCheck.Kind!=".NET test filter") && (state.Build?.ExitCode is null or 0 ||
            state.FocusedCheck.Kind!=".NET test filter"))
        {
            state.Focused=reply.Focused=await RunFocusedCheck(reply,state.FocusedCheck,state.RepairAttempts,ct);
            AddActivity("check",state.Focused.Summary);
            RunJournal.Save(store.Root,current.Id,state);
            Save();
        }
    }

    static bool ChecksFailed(RunCheckpoint state) => state.Build is { ExitCode:not 0 } || state.Focused is { Passed:false };

    static async Task<string> CheckFeedback(BuildResult? build,FocusedResult? focused,CancellationToken ct)
    {
        var details=new List<string>();
        if(build is { ExitCode:not 0 })details.Add("Build errors ·\n"+await Verification.Errors(build.LogPath,ct));
        if(focused is { Passed:false })
        {
            details.Add(focused.Summary);
            if(focused.LogPath is { } log)details.Add(await Verification.Errors(log,ct));
        }
        return string.Join("\n",details);
    }
}
