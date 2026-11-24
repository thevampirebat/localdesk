using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LocalDesk;

public sealed partial class MainWindow
{
    internal static MainWindow CreateWorkspaceCheck(string directory, IClassicDesktopStyleApplicationLifetime app, bool agentCheck = false)
    {
        var root = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var data = new ChatStore(root);
        new DeskSettings { WorkingFolder = root, RunNotifications = false }.Save(root);
        var model = new WorkspaceCheckModel();
        var window = new MainWindow(true, new OllamaClient(model), data) { ShowInTaskbar = false };
        var report = Path.Combine(directory, agentCheck ? "agent-answer-ui-result.txt" : "workspace-ui-result.txt");
        window.Opened += async (_, _) =>
        {
            try
            {
                if (agentCheck) await window.CheckAgentAnswerRecovery(model);
                else await window.CheckWorkspace(model);
                File.WriteAllText(report, agentCheck ? "PASS: bundled Cline recovered a reasoning-only reply and marked an empty retry incomplete."
                    : "PASS: workspace, queue, rich chat, result review, chat approvals, project presets and investigation checkpoints.");
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(report, error.ToString());
                app.Shutdown(1);
            }
        };
        return window;
    }

    async Task CheckAgentAnswerRecovery(WorkspaceCheckModel backend)
    {
        foreach (var recover in new[] { true, false })
        {
            NewChat();
            backend.AgentRequests.Clear();
            backend.ProduceFinalAnswer = recover;
            input.Text = "Explain whether the evidence supports the requested conclusion.";
            await Send();
            var result = current.Messages.LastOrDefault();
            Require(backend.AgentRequests.Count == 2 && !backend.AgentRequests[1].ContainsKey("tools") &&
                backend.AgentRequests[1]["think"]?.GetValue<bool>() == false,
                "The agent did not make exactly one tool-free retry: " + status.Text);
            Require(result?.Successful == recover && (recover
                ? result.Summary == "The evidence was inconclusive."
                : result.PauseReason?.Contains("without a visible final answer", StringComparison.Ordinal) == true),
                "LocalDesk did not report the actual outcome: " + result?.Summary);
        }
    }

    async Task CheckWorkspace(WorkspaceCheckModel backend)
    {
        ShowMiniWindow();
        var monitor=miniWindow??throw new InvalidOperationException("The mini-window did not open.");
        Require(monitor.Update().State==MiniRunState.Idle&&monitor.Topmost&&monitor.Owner==null,"The mini-window is not an independent pinned monitor.");
        SetBusy(true);
        Require(workingHighlight.IsVisible && workingHighlight.IsIndeterminate, "The activity highlight did not start.");
        SetMiniPhase("Checking fixture");
        Require(monitor.Update() is {State:MiniRunState.Working,Phase:"Checking fixture",Elapsed:not null},"The mini-window lost its active phase or timer.");
        var approval=UtilityWindow("Fixture approval",380,220);
        var approvalReview=ShowRunApproval(approval);
        Require(monitor.IsVisible&&monitor.Update().State==MiniRunState.Waiting,"The mini-window did not show a waiting approval.");
        OpenMiniDestination(false);
        Require(approval.IsVisible,"The mini-window lost the approval dialog.");
        approval.Close(false);
        await approvalReview;
        SetBusy(false);
        Require(!workingHighlight.IsVisible && !workingHighlight.IsIndeterminate, "The activity highlight kept running while idle.");
        var source = current;
        var path = Path.Combine(store.Root, "fixture.txt");
        File.WriteAllText(path, "workspace evidence");
        input.Text = "A saved draft";
        ImportFiles([path]);
        CaptureDraft();
        workspace.ColumnDefinitions[2].Width = new GridLength(365);
        SaveActivityWidth();
        PersistWorkspace(includeChats: true);
        var saved = new ChatStore(store.Root).Chats.Single(c => c.Id == source.Id);
        var layoutState = WorkspaceSession.Load(store.Root);
        Require(saved.Draft == "A saved draft" && saved.DraftFiles.Single().SourcePath == path &&
            layoutState.ChatId == source.Id && layoutState.ActivityWidth == 365, "Workspace did not persist the draft and divider.");
        NewChat();
        Show(source);
        Require(input.Text == saved.Draft && pendingFiles.Single().SourcePath == path, "Draft or attachment did not restore.");

        mode.SelectedIndex = 0;
        current.ContextTokens = 8192;
        input.Text = "First queued request";
        EnqueueDraft();
        var first = store.Chats.Single(c => c.Id == session.Pending.Single());
        input.Text = "Second queued request";
        current.ContextTokens = 12288;
        current.Model = LocalModel.Large.Id;
        EnqueueDraft();
        var second = store.Chats.Single(c => c.Id == session.Pending[1]);
        Require(first.DraftFiles.Count == 1 && second.DraftFiles.Count == 0 && first.Model != second.Model &&
            first.ContextTokens == 8192 && second.ContextTokens == 12288, "Queue did not capture independent settings.");
        Require(WorkspaceSession.Load(store.Root).Pending.SequenceEqual(new[] { first.Id, second.Id }), "Queue order was not persisted.");
        Require(monitor.Update() is {Queued:2,QueuePaused:true},"The mini-window lost the paused queue count.");
        ShowQueue();
        var queueLayout = (Grid)queueWindow!.Content!;
        var queueList = queueLayout.Children.OfType<ListBox>().Single();
        var queueActions = queueLayout.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToList();
        queueList.SelectedIndex = 1;
        queueActions.Single(b => Equals(b.Content, "Move up")).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(WorkspaceSession.Load(store.Root).Pending.SequenceEqual(new[] { second.Id, first.Id }), "Move up did not persist queue order.");
        queueActions.Single(b => Equals(b.Content, "Move down")).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        queueWindow.Close();
        queueEnabled = true;
        await DrainQueue();
        if (session.Pending.Count > 0) await DrainQueue();
        Require(backend.Prompts.Count == 2 && backend.Prompts[0].Contains("First queued request") &&
            backend.Prompts[0].Contains("workspace evidence") && backend.Prompts[1].Contains("Second queued request") &&
            first.Messages.Last().Successful && second.Messages.Last().Successful && session.Pending.Count == 0, "Queue did not complete both independent requests in order.");

        var rendered = (StackPanel)RenderMarkdown("# Heading\n\n**Bold** and `inline`\n\n```cs\nreturn 42;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |");
        Require(rendered.Children.Count == 4 && rendered.Children[2] is Border && rendered.Children[3] is Grid, "Markdown did not render heading, prose, code and table.");
        var result = second.Messages.Last();
        Require(monitor.Update() is {State:MiniRunState.Finished,HasResult:true},"The mini-window did not retain the completion.");
        OpenMiniDestination(false);
        Require(!monitor.Update().HasResult&&current==second,"The mini-window did not acknowledge the right result.");
        monitor.Topmost=false;
        monitor.Close();
        Require(!WorkspaceSession.Load(store.Root).MiniTopmost&&miniWindow==null,"The mini-window did not remember its pin setting or close cleanly.");
        Require(ChatMarkdownExport(second).Contains("Second queued request") && ChatMarkdownExport(second).Contains("return 42;"), "Chat export omitted a visible turn.");
        result.ProjectUsed = store.Root;
        result.BuildCheck = new(0, path);
        result.VerifiedFiles.Add(new(path, "Added", "sample change") { AfterSnapshot = path });
        Require(CheckOutcome(result) == "Build passed", "Independent check badge was incorrect.");
        var review = ReviewResults(result);
        var reviewWindow = OwnedWindows.Single(w => w.Title == "Review results");
        var panel = (StackPanel)((ScrollViewer)reviewWindow.Content!).Content!;
        var row = panel.Children.OfType<Border>().Single();
        Require(((StackPanel)row.Child!).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Any(b => Equals(b.Content, "Open diff")), "Verified file has no diff action.");
        reviewWindow.Close();
        await review;

        var palette = ShowCommands();
        var paletteWindow = OwnedWindows.Single(w => w.Title == "Quick actions");
        var list = ((Grid)paletteWindow.Content!).Children.OfType<ListBox>().Single();
        Require(list.Items.Cast<string>().Any(label => label.StartsWith("Switch model")) &&
            list.Items.Cast<string>().Contains("Review latest results"), "Palette did not expose model and results actions.");
        paletteWindow.Close();
        await palette;
        await CheckSearchApproval();
        await CheckApproveAll();
        await CheckRunSetup();
    }

    async Task CheckRunSetup()
    {
        var root=Path.Combine(store.Root,"ProjectSetup");
        Directory.CreateDirectory(root);
        current.Project=root;
        var projectEntry=Projects.Remember(root);
        projectEntry.ApproveProjectEdits=true;
        projectEntry.ApproveConfiguredChecks=true;
        Projects.Save();
        var valid=JsonNode.Parse("""{"path":"file.cs","new_text":"hello"}""")!.AsObject();
        var escape=JsonNode.Parse("""{"path":"../file.cs","new_text":"hello"}""")!.AsObject();
        Require(ReviewProjectPreset("editor",valid,false)&&!ReviewProjectPreset("editor",escape,false)&&
            !ReviewProjectPreset("build check",valid,false)&&ReviewProjectPreset("build check",valid,true),"Project preset exceeded its allowed scope.");
        var checkpoint=new RunCheckpoint{Project=root,Reply="fixture",ReviewBeforeChanges=true,AcceptanceCriteria="Confirm the project still builds."};
        activeCheckpoint=checkpoint;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var waiting=ReviewTool(new("editor",valid.ToJsonString()),timeout.Token);
        await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.Background);
        var dialog=OwnedWindows.Single(w=>w.Title=="Review investigation");
        var panel=(StackPanel)((ScrollViewer)dialog.Content!).Content!;
        panel.Children.OfType<TextBox>().Single().Text="First inspect the button handler.";
        panel.Children.OfType<Button>().Single(button=>Equals(button.Content,"Pause here")).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(!await waiting&&RunJournal.Load(store.Root,current.Id)?.Steering=="First inspect the button handler.","A paused edit escaped the checkpoint or lost its steering note.");
        activeCheckpoint=null;
        RunJournal.Complete(store.Root,current.Id,checkpoint);
    }

    async Task CheckApproveAll()
    {
        var trusted = current;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = new ToolProposal("run_commands", """{"command":"echo fixture"}""");
        var decision = ReviewTool(command, timeout.Token);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var approval = OwnedWindows.Single(w => w.Title == "Review agent action");
        var actions = ((Grid)approval.Content!).Children.OfType<StackPanel>().Last();
        actions.Children.OfType<Button>().Single(b => Equals(b.Content, "Approve all in this chat"))
            .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(await decision && await ReviewTool(command, timeout.Token), "Approve all did not remember tool approvals in the selected chat.");
        NewChat();
        Require(!trustedToolChats.Contains(current.Id) && !webSearchPermission.IsVisible, "The new chat inherited tool approval.");
        Show(trusted);
        webSearchPermission.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(!trustedToolChats.Contains(trusted.Id) && !webSearchPermission.IsVisible, "Revocation did not restore individual approvals.");
    }

    async Task CheckSearchApproval()
    {
        var chat = current;
        var action = new ToolProposal(ToolReviewGate.WebFetchTool, """{"requests":[{"url":"https://www.bing.com/search?q=fixture","prompt":"Find relevant articles"}]}""");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var decision = ReviewTool(action, timeout.Token);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var approval = OwnedWindows.Single(w => w.Title == "Review agent action");
        var buttons = ((Grid)approval.Content!).Children.OfType<StackPanel>().Last();
        buttons.Children.OfType<Button>().Single(b => Equals(b.Content, "Allow searches in this chat"))
            .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(await decision && await ReviewTool(action, timeout.Token), "Search permission did not apply to the following search.");
        Require(new ChatStore(store.Root).Chats.Single(c => c.Id == chat.Id).AllowWebSearch, "Search permission was not saved with the chat.");
        NewChat();
        Require(!current.AllowWebSearch, "A new chat inherited search permission.");
        Show(chat);
        webSearchPermission.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Require(!chat.AllowWebSearch && !webSearchPermission.IsVisible, "Revocation did not clear search permission.");
    }

    static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    sealed class WorkspaceCheckModel : HttpMessageHandler
    {
        public List<string> Prompts { get; } = [];
        public List<JsonObject> AgentRequests { get; } = [];
        public bool ProduceFinalAnswer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path switch
            {
                "/api/tags" => "{\"models\":[]}",
                "/api/ps" => "{\"models\":[]}",
                "/api/show" => "{\"capabilities\":[\"vision\",\"tools\"]}",
                _ => "{}"
            };
            if (path == "/api/chat")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
                Prompts.Add(body!["messages"]!.AsArray().Last()!["content"]!.ToString());
                response = JsonSerializer.Serialize(new { message = new { content = "**Done.**\n\n```cs\nreturn 42;\n```" }, done = true, eval_count = 9, prompt_eval_count = 20 });
            }
            if (path == "/v1/chat/completions")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                AgentRequests.Add(body);
                string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new
                {
                    id = "fixture-completion", @object = "chat.completion.chunk", model = LocalModel.Fast.Id, created = 1,
                    choices = new[] { new { index = 0, delta, finish_reason = finish } }
                }) + "\n\n";
                var stream = Chunk(new { role = "assistant" }) + (ProduceFinalAnswer && AgentRequests.Count == 2
                    ? Chunk(new { content = "The evidence was inconclusive." }) : Chunk(new { reasoning_content = "Test reasoning without an answer." })) +
                    Chunk(new { }, "stop") + "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
