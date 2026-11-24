using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalDesk;

public sealed partial class MainWindow
{
    async Task ConfigureRun()
    {
        if (busy) return;
        CommitProject();
        var dialog = UtilityWindow("Run setup", 740, 630);
        var panel = new StackPanel { Margin=new Thickness(20), Spacing=9 };
        panel.Children.Add(EvidenceText("Set what this chat should deliver and how LocalDesk will verify it."));
        var criteria = new TextBox { Text=current.AcceptanceCriteria, PlaceholderText="Expected behavior and any manual checks…", AcceptsReturn=true, Height=100, MaxLength=3000, TextWrapping=TextWrapping.Wrap };
        var review = new CheckBox { Content="Pause after investigation, before the first edit or command", IsChecked=current.ReviewBeforeChanges };
        var choices = new[] { "Use saved project check", "Text present", "Text absent", ".NET test filter" };
        var kind = new ComboBox { ItemsSource=choices, SelectedItem=current.AcceptanceCheck?.Kind ?? choices[0] };
        var path = new TextBox { Text=current.AcceptanceCheck?.RelativePath, PlaceholderText="Project-relative file or test .csproj" };
        var expected = new TextBox { Text=current.AcceptanceCheck?.Expected, PlaceholderText="Exact text or test filter", MaxLength=2000 };
        var note = EvidenceText("");
        panel.Children.Add(criteria);
        panel.Children.Add(review);
        panel.Children.Add(EvidenceText("Independent acceptance check"));
        panel.Children.Add(kind);
        panel.Children.Add(path);
        panel.Children.Add(expected);
        panel.Children.Add(note);
        void Update() { path.IsVisible=expected.IsVisible=kind.SelectedIndex>0; }
        kind.SelectionChanged+=(_,_)=>Update();
        Update();
        var actions=new WrapPanel { Orientation=Orientation.Horizontal };
        actions.Children.Add(Button("Save setup",(_,_)=>
        {
            var check=kind.SelectedIndex<=0?null:new FocusedCheck(kind.SelectedItem!.ToString()!,path.Text?.Trim()??"",expected.Text??"");
            if(check!=null&&(!Directory.Exists(current.Project)||Verification.Target(current.Project,check,allowMissingText:true)==null))
            {note.Text="Choose a valid project-relative path and enter the expected text or filter.";return;}
            current.AcceptanceCriteria=criteria.Text?.Trim()??"";
            current.AcceptanceCheck=check;
            current.ReviewBeforeChanges=review.IsChecked==true;
            Save();
            dialog.Close();
        }));
        actions.Children.Add(Button("Cancel",(_,_)=>dialog.Close()));
        panel.Children.Add(actions);
        dialog.Content=new ScrollViewer {Content=panel};
        await dialog.ShowDialog(this);
    }

    async Task EditProjectPermissions(SavedProject entry,Window owner)
    {
        var dialog=UtilityWindow("Project permissions",620,340);
        var panel=new StackPanel {Margin=new Thickness(20),Spacing=12};
        panel.Children.Add(EvidenceText(entry.Folder));
        var edits=new CheckBox {Content="Allow supported edits inside this source folder",IsChecked=entry.ApproveProjectEdits};
        var checks=new CheckBox {Content="Allow LocalDesk's configured .NET build and focused test",IsChecked=entry.ApproveConfiguredChecks};
        panel.Children.Add(edits);
        panel.Children.Add(checks);
        panel.Children.Add(EvidenceText("Other tool actions follow the chat's approval settings. Use this for a project whose edits and build scripts you trust."));
        panel.Children.Add(Button("Save permissions",(_,_)=>
        {
            var saved=Projects.Remember(entry.Folder);
            saved.ApproveProjectEdits=entry.ApproveProjectEdits=edits.IsChecked==true;
            saved.ApproveConfiguredChecks=entry.ApproveConfiguredChecks=checks.IsChecked==true;
            Projects.Save();
            dialog.Close();
        }));
        dialog.Content=panel;
        await dialog.ShowDialog(owner);
    }

    bool ReviewProjectPreset(string name,JsonObject args,bool configuredCheck)
    {
        if(current.BranchFolder!=null||Projects.Find(current.Project) is not { } entry)return false;
        var allowed=configuredCheck?entry.ApproveConfiguredChecks:
            entry.ApproveProjectEdits&&ToolReviewGate.SafeBranchEdit(name,args,current.Project);
        if(allowed)AddActivity("review","Allowed by project preset · "+name);
        return allowed;
    }

    async Task<bool> ReviewInvestigation(ToolProposal proposal,CancellationToken ct)
    {
        if(activeCheckpoint is not { ReviewBeforeChanges:true,ReviewAcknowledged:false } checkpoint ||
            proposal.Name is not ("editor" or "write_to_file" or "replace_in_file" or "apply_patch" or "run_commands" or "execute_command" or "build check" or "focused .NET test"))return true;
        var dialog=UtilityWindow("Review investigation",790,640);
        var panel=new StackPanel {Margin=new Thickness(20),Spacing=9};
        panel.Children.Add(new TextBlock {Text="Before the first change",FontSize=22});
        if(checkpoint.AcceptanceCriteria.Length>0)panel.Children.Add(EvidenceText(checkpoint.AcceptanceCriteria));
        if(activeMessage?.Text is { Length:>0 } progress) panel.Children.Add(ChatText(progress.Length>1800?progress[^1800..]:progress,13));
        foreach(var entry in current.Activity.Where(a=>a.Kind is "tool" or "search").TakeLast(5))
            panel.Children.Add(EvidenceText(entry.Text.Length>320?entry.Text[..320]+"…":entry.Text));
        panel.Children.Add(EvidenceText("Proposed action · "+proposal.Name));
        panel.Children.Add(new Expander {Header="Inspect proposed arguments",Content=ChatText(proposal.Arguments,12)});
        var steering=new TextBox {Text=checkpoint.Steering,PlaceholderText="Optional instructions for resuming…",AcceptsReturn=true,Height=85,MaxLength=3000};
        panel.Children.Add(steering);
        panel.Children.Add(Button("Continue to tool review",(_,_)=>dialog.Close(true)));
        panel.Children.Add(Button("Pause here",(_,_)=>dialog.Close(false)));
        dialog.Content=new ScrollViewer {Content=panel};
        using var registration=ct.Register(()=>Dispatcher.UIThread.Post(()=>dialog.Close(false)));
        if(ct.IsCancellationRequested)return false;
        bool proceed=await ShowRunApproval(dialog);
        if(ct.IsCancellationRequested)return false;
        if(proceed)checkpoint.ReviewAcknowledged=true;
        else if(!string.IsNullOrWhiteSpace(steering.Text))checkpoint.Steering=steering.Text.Trim();
        RunJournal.Save(store.Root,current.Id,checkpoint);
        if(!proceed)AddActivity("checkpoint","Paused after investigation. Resume with the saved next step.");
        return proceed;
    }

    async Task EditSavedSteering()
    {
        if(busy||RunJournal.Load(store.Root,current.Id) is not { } checkpoint)return;
        var dialog=UtilityWindow("Edit next step",640,335);
        var text=new TextBox {Text=checkpoint.Steering,PlaceholderText="What should the agent do when it resumes?",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=128,MaxLength=3000};
        var panel=new StackPanel {Margin=new Thickness(20),Spacing=10};
        panel.Children.Add(EvidenceText(checkpoint.LastTool??"No completed tool action was recorded yet."));
        panel.Children.Add(text);
        panel.Children.Add(Button("Save next step",(_,_)=>{checkpoint.Steering=text.Text?.Trim()??"";RunJournal.Save(store.Root,current.Id,checkpoint);dialog.Close();}));
        dialog.Content=panel;
        await dialog.ShowDialog(this);
    }
}
