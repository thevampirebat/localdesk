using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalDesk;

public sealed record ModelHandoff(string PriorModel,string Notes,IReadOnlyList<string> FilePaths);

public sealed partial class MainWindow
{
    async Task<bool> OfferRepair(int attempt,string feedback,CancellationToken ct)
    {
        if(ct.IsCancellationRequested)return false;
        var dialog=UtilityWindow("Repair failed check",880,545);
        var panel=new Grid { RowDefinitions=new RowDefinitions("Auto,*,Auto"),Margin=new Thickness(22) };
        var heading=new StackPanel{Spacing=7,Margin=new Thickness(0,0,0,10)};
        heading.Children.Add(new TextBlock{Text=$"Repair attempt {attempt} of {Verification.MaxRepairs}",FontSize=24,FontWeight=FontWeight.SemiBold});
        heading.Children.Add(EvidenceText("Send these errors to the current model. Proposed edits and commands still need your approval."));
        panel.Children.Add(heading);
        var scroll=new ScrollViewer{Content=new SelectableTextBlock{Text=feedback,FontFamily=new FontFamily("Consolas"),TextWrapping=TextWrapping.Wrap,
            Margin=new Thickness(14)},Background=Brush("#11151C")};
        Grid.SetRow(scroll,1);
        panel.Children.Add(scroll);
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=9,Margin=new Thickness(0,12,0,0)};
        row.Children.Add(Button("Finish with failed check",(_,_)=>dialog.Close(false)));
        row.Children.Add(Button("Ask model to repair",(_,_)=>dialog.Close(true)));
        Grid.SetRow(row,2);
        panel.Children.Add(row);
        dialog.Content=panel;
        using var registration=ct.Register(()=>Dispatcher.UIThread.Post(()=>dialog.Close(false)));
        var decision=await ShowRunApproval(dialog);
        ct.ThrowIfCancellationRequested();
        return decision;
    }

    async Task Handoff(ChatMessage reply)
    {
        if(busy || reply.ProjectUsed==null)return;
        var chat=current;
        CommitProject();
        if(current.Mode!="agent" || !current.Project.Equals(reply.ProjectUsed,StringComparison.OrdinalIgnoreCase))
        {
            status.Text="Select the original project folder to hand off this run.";
            return;
        }
        int index=current.Messages.IndexOf(reply);
        if(index<0||index!=current.Messages.Count-1)
        {
            status.Text="Hand off the latest run in this chat.";
            return;
        }
        var user=current.Messages.Take(index).LastOrDefault(m=>m.Role=="user");
        if(user==null)return;
        List<FitChoice> fits;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
        {
            try { fits = await FindModelFits(timeout.Token); }
            catch (Exception) { fits = []; }
        }
        if (busy || !ReferenceEquals(chat, current)) return;
        var needsVision = user.Images.Count > 0;
        var recommended = fits.Where(item => item.Model.Id != current.Model && item.Tools && (!needsVision || item.Vision) && item.Fits)
            .OrderByDescending(item => item.Measured).ThenByDescending(item => item.Context).FirstOrDefault();
        var choices=installedModels.Where(item=>item.Id!=current.Model).ToList();
        if(choices.Count==0){status.Text="Install another local model to use handoff.";return;}
        var dialog=UtilityWindow("Hand off to another model",690,400);
        var panel=new StackPanel{Margin=new Thickness(22),Spacing=11};
        panel.Children.Add(new TextBlock{Text="Hand off the current task",FontSize=23,FontWeight=FontWeight.SemiBold});
        panel.Children.Add(EvidenceText("The next model gets the original request, verified file changes, recent tool notes, and failed checks. It starts a fresh agent session and asks before edits or commands."));
        var selected=new ComboBox{ItemsSource=choices,SelectedItem=choices.FirstOrDefault(item => item.Id == recommended?.Model.Id) ?? choices[0]};
        panel.Children.Add(EvidenceText(recommended == null ? "No alternative with a verified capability and a likely memory fit was found."
            : $"Suggested: {recommended.Model.Name} supports project tools{(needsVision ? " and images" : "")}. {recommended.Reason}."));
        panel.Children.Add(selected);
        panel.Children.Add(EvidenceText("Working folder · "+current.Project));
        panel.Children.Add(EvidenceText($"{reply.VerifiedFiles.Count} verified file changes in the previous run."));
        var row=new WrapPanel{Orientation=Orientation.Horizontal};
        row.Children.Add(Button("Cancel",(_,_)=>dialog.Close(false)));
        row.Children.Add(Button("Start handoff",(_,_)=>dialog.Close(true)));
        panel.Children.Add(row);
        dialog.Content=panel;
        if(!await dialog.ShowDialog<bool>(this)||selected.SelectedItem is not LocalModel next)return;

        string verified=string.Join("\n",reply.VerifiedFiles.Take(16).Select(file=>file.Change+" · "+file.Path));
        string logNotes=string.Join("\n",current.Activity.Where(item=>item.Kind is "tool" or "diagnostic" or "paused").TakeLast(6)
            .Select(item=>item.Kind+" · "+(item.Text.Length>220?item.Text[..220]+"…":item.Text)));
        var checkpoint=RunJournal.Load(store.Root,current.Id);
        string failures;
        try
        {
            using var feedbackTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            failures=await CheckFeedback(reply.BuildCheck,reply.Focused,feedbackTimeout.Token);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or OperationCanceledException){failures="Check details could not be loaded: "+error.Message;}
        if(checkpoint?.RepairFeedback is { } persisted)failures+="\n"+persisted;
        if(failures.Length>7000)failures=failures[..7000]+"…";
        var beat=reply.Summary??reply.Text;
        if(beat.Length>1600)beat=beat[^1600..];
        var notes="Prior model report (check this against disk):\n"+beat+
            "\nVerified changes from that run:\n"+(verified.Length==0?"None recorded.":verified)+
            "\nRecorded tool activity:\n"+logNotes+"\nFailed checks:\n"+failures;
        var handoff=new ModelHandoff(reply.RunModel??current.Model,notes,reply.VerifiedFiles.Select(file=>file.Path).Take(16).ToArray());

        if(busy || !ReferenceEquals(chat,current) || !current.Project.Equals(reply.ProjectUsed,StringComparison.OrdinalIgnoreCase))return;
        current.Model=next.Id;
        ApplyProfile(next.Id);
        if (next.Id == recommended?.Model.Id)
        {
            current.ContextTokens = recommended.Context;
            current.OutputTokens = Math.Min(current.OutputTokens, recommended.Context / 3);
        }
        changing=true;
        model.SelectedItem=next;
        ToolTip.SetTip(model,next.Note);
        changing=false;
        Save();
        AddActivity("handoff",LocalModel.FromTag(handoff.PriorModel).Name+" → "+next.Name);
        await Send(user,reply,checkpoint,handoff);
    }
}
