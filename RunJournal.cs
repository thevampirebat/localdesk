using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace LocalDesk;

public sealed class RunCheckpoint
{
    public string Request { get; set; } = "";
    public string Reply { get; set; } = "";
    public string Project { get; set; } = "";
    public string? BuildTarget { get; set; }
    public bool AgentDone { get; set; }
    public BuildResult? Build { get; set; }
    public FocusedCheck? FocusedCheck { get; set; }
    public FocusedResult? Focused { get; set; }
    public int RepairAttempts { get; set; }
    public string? RepairFeedback { get; set; }
    public string? HandoffText { get; set; }
    public string? LastTool { get; set; }
    public string AcceptanceCriteria { get; set; } = "";
    public bool ReviewAcknowledged { get; set; }
    public bool ReviewBeforeChanges { get; set; }
    public string Steering { get; set; } = "";
}

internal static class RunJournal
{
    private static string Folder(string root) => Path.Combine(root,"recovery");
    private static string PathFor(string root,string chat) => Path.Combine(Folder(root),chat+".json");
    public static string Baseline(string root,string chat,string reply) => Path.Combine(Folder(root),chat+"-"+reply+".baseline");

    public static void Save(string root,string chat,RunCheckpoint state)
    {
        Directory.CreateDirectory(Folder(root));
        var target=PathFor(root,chat);
        File.WriteAllText(target+".tmp",JsonSerializer.Serialize(state));
        File.Move(target+".tmp",target,true);
    }

    public static RunCheckpoint? Load(string root,string chat)
    {
        try
        {
            var path=PathFor(root,chat);
            return File.Exists(path)?JsonSerializer.Deserialize<RunCheckpoint>(File.ReadAllText(path)):null;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){return null;}
    }

    public static void Complete(string root,string chat,RunCheckpoint state)
    {
        File.Delete(PathFor(root,chat));
        RemoveBaseline(root,chat,state);
    }

    public static void RemoveBaseline(string root,string chat,RunCheckpoint state)
    {
        try{File.Delete(Baseline(root,chat,state.Reply));}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}

public sealed partial class MainWindow
{
    readonly StackPanel recoveryPanel=new(){Orientation=Orientation.Horizontal,Spacing=10,IsVisible=false};
    readonly TextBlock recoveryStatus=EvidenceText("");
    Button recoveryButton=null!;
    RunCheckpoint? activeCheckpoint;
    bool closeAfterRun;

    void AddRecoveryPanel(StackPanel composer)
    {
        recoveryButton=Button("Resume saved request",async(_,_)=>await ResumeSaved());
        recoveryPanel.Children.Add(recoveryButton);
        recoveryStatus.VerticalAlignment=VerticalAlignment.Center;
        recoveryPanel.Children.Add(recoveryStatus);
        recoveryPanel.Children.Add(Button("Edit next step…",async(_,_)=>await EditSavedSteering()));
        recoveryPanel.Children.Add(Button("Dismiss",(_,_) =>
        {
            if(busy || RunJournal.Load(store.Root,current.Id) is not { } pending)return;
            try
            {
                RunJournal.Complete(store.Root,current.Id,pending);
                status.Text="Saved request dismissed. Its chat and project files are still available.";
                ShowRecovery();
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){status.Text=error.Message;}
        }));
        composer.Children.Add(recoveryPanel);
    }

    void ShowRecovery()
    {
        var checkpoint=RunJournal.Load(store.Root,current.Id);
        recoveryPanel.IsVisible=checkpoint!=null&&!busy;
        if(checkpoint!=null)
            recoveryStatus.Text=checkpoint.AgentDone
                ? (checkpoint.BuildTarget!=null&&checkpoint.Build==null || checkpoint.FocusedCheck!=null&&checkpoint.Focused==null?"Checks pending":"File verification pending")
                : "Project run interrupted";
    }

    void SaveToolCheckpoint(string outcome)
    {
        var checkpoint=activeCheckpoint;
        if(checkpoint==null)return;
        Dispatcher.UIThread.Post(()=>
        {
            if(!ReferenceEquals(activeCheckpoint,checkpoint))return;
            checkpoint.LastTool=outcome.Length>450?outcome[..450]+"…":outcome;
            try{RunJournal.Save(store.Root,current.Id,checkpoint);}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            { status.Text="Could not save recovery progress: "+error.Message; }
        });
    }

    async Task ResumeSaved()
    {
        if(busy)return;
        var state=RunJournal.Load(store.Root,current.Id);
        if(state==null){ShowRecovery();return;}
        CommitProject();
        if(current.Mode!="agent" || !state.Project.Equals(current.Project,StringComparison.OrdinalIgnoreCase))
        {status.Text="Use Project agent mode in "+state.Project+" to resume this request.";return;}
        var request=current.Messages.FirstOrDefault(m=>m.Marker==state.Request&&m.Role=="user");
        var reply=current.Messages.FirstOrDefault(m=>m.Marker==state.Reply&&m.Role=="assistant");
        if(request==null||reply==null){status.Text="The saved request is no longer in this chat.";return;}
        var baseline=RunJournal.Baseline(store.Root,current.Id,state.Reply);
        var chat=current;
        using var recover=new CancellationTokenSource();
        cancellation=recover;
        activeCheckpoint=state;
        SetBusy(true);
        SetMiniPhase("Recovering saved run");
        status.Text="Checking work already on disk…";
        try
        {
            if(!state.AgentDone && reply.VerifiedFiles.Count==0 && File.Exists(baseline))
            {
                var files=await Task.Run(()=>ProjectEvidence.Restore(baseline,state.Project).Finish(),recover.Token);
                reply.VerifiedFiles=files;
            }
            if(reply.Summary==null)
            {
                reply.Summary="LocalDesk recovered the saved request after an interruption.";
                reply.PauseReason=state.AgentDone?"File verification or checks were interrupted.":"The project run was interrupted.";
            }
            reply.ProjectUsed=state.Project;
            if(state.Build!=null)reply.BuildCheck=state.Build;
            if(state.Focused!=null)reply.Focused=state.Focused;
            reply.RepairAttempts=state.RepairAttempts;
            Save();
            if(state.AgentDone)
            {
                await RunChecks(reply,state,recover.Token);
                if(File.Exists(baseline))
                {
                    var recovered=await Task.Run(()=>ProjectEvidence.Restore(baseline,state.Project).Finish(),recover.Token);
                    reply.VerifiedFiles=MergeRecoveredFiles(reply.VerifiedFiles,recovered);
                }
                else if(reply.VerifiedFiles.Count==0)reply.EvidenceNote="The saved file baseline is unavailable.";
                reply.Successful=!ChecksFailed(state);
                reply.PauseReason=null;
                reply.CompletedAt=DateTime.Now;
                if(reply.Text.Length>0){reply.Summary=reply.Text.Trim();reply.SummaryOffset=0;}
                Save();
                RunJournal.Complete(store.Root,chat.Id,state);
                Show(chat);
                status.Text="Recovered the finished agent turn and completed its remaining checks.";
                return;
            }
            if(state.LastTool!=null)reply.EvidenceNote="Last recorded tool · "+state.LastTool;
        }
        catch(OperationCanceledException){status.Text="Recovery stopped. The saved request is still available.";return;}
        catch(AgentClient.LoopException error){status.Text=error.Message+" The saved request is still available.";return;}
        catch(Exception error){status.Text="Could not recover the request: "+error.Message;return;}
        finally{activeCheckpoint=null;cancellation=null;SetBusy(false);FinishClosing();}
        if(closeAfterRun)return;
        await Send(request,reply,state);
    }

    static List<VerifiedFile> MergeRecoveredFiles(IEnumerable<VerifiedFile> known,IEnumerable<VerifiedFile> recovered)
    {
        var older=known.ToDictionary(file=>file.Path,StringComparer.OrdinalIgnoreCase);
        return recovered.Select(file=>older.TryGetValue(file.Path,out var previous) &&
            previous.Change==file.Change && previous.AfterHash!=null && previous.AfterHash==file.AfterHash ? previous : file).ToList();
    }

    async Task<BuildResult> RunBuildCheck(ChatMessage reply,string target,CancellationToken ct,int attempt=0)
    {
        var proposal=new ToolProposal("build check",JsonSerializer.Serialize(new{working_folder=current.Project,
            executable="dotnet",arguments=new[]{"build",target,"-c","Release","--nologo"}}));
        if(!await TimedReview(proposal,ct,true))throw new AgentClient.LoopException("Configured build check declined.");
        status.Text="Running the configured .NET build check…";
        AddActivity("check","dotnet build "+target+" -c Release");
        var log=Path.Combine(store.Root,"build-checks",current.Id,reply.Marker+(attempt==0?"":$"-repair-{attempt}")+".log");
        var result=await ProjectEvidence.Build(target,current.Project,log,ct);
        try{RememberBuild(result);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {AddActivity("status","Could not save project build history: "+error.Message);}
        AddActivity("check",$"Build exited with code {result.ExitCode}.");
        return result;
    }

    void FinishClosing()
    {
        if(closeAfterRun)Dispatcher.UIThread.Post(Close);
    }
}
