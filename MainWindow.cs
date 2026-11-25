using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;

namespace LocalDesk;
public sealed partial class MainWindow:Window
{
    static IBrush Brush(string hex)=>DeskPalette.Get(hex);
    readonly ChatStore store;readonly OllamaClient ollama;readonly AgentClient agent=new();
    readonly ListBox history=new(){Background=Brush("#11151C"),BorderThickness=new Thickness(0),MinHeight=100};
    readonly StackPanel messages=new(){Spacing=24,MaxWidth=880,Margin=new Thickness(28,28,28,20)};
    readonly ScrollViewer scroller=new(){VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
    readonly TextBox input=new(){PlaceholderText="Message LocalDesk…",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=62,MaxHeight=180,BorderThickness=new Thickness(0),Background=Brush("#212A36"),FontSize=16,Padding=new Thickness(8)};
    readonly ComboBox model=new(){ItemsSource=new[]{LocalModel.Fast,LocalModel.Large},SelectedIndex=0,MinWidth=190};
    readonly ComboBox mode=new(){ItemsSource=new[]{"Chat","Project agent"},SelectedIndex=0,MinWidth=130};
    sealed record TimeOption(int Minutes,string Label){public override string ToString()=>Label;}
    readonly ComboBox limit=new(){MinWidth=145};
    readonly TextBox project=new(){PlaceholderText="Choose a project folder",BorderThickness=new Thickness(0),Background=Brush("#202832"),FontSize=13,MinWidth=180};
    readonly TextBlock status=new(){Text="Connecting to local models…",FontSize=12,Foreground=Brush("#A2AAA7"),TextWrapping=TextWrapping.Wrap};
    readonly TextBlock speed=new(){Text="— tok/s",FontSize=12,Foreground=Brush("#B8D6CF"),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(16,0,0,0)};
    readonly TextBlock contextUsed=new(){FontSize=12,Foreground=Brush("#AFC9BA"),TextWrapping=TextWrapping.Wrap};
    readonly ProgressBar contextBar=new(){Minimum=0,Maximum=1,Height=4,Margin=new Thickness(0,6,16,0),Foreground=Brush("#6EBA8A"),Background=Brush("#333D36")};
    readonly ProgressBar workingHighlight=new(){Height=3,IsVisible=false,Foreground=Brush("#A5E7D8"),Background=Brush("#263941"),IsHitTestVisible=false};
    readonly Button compactButton=Button("Compact",null);
    readonly TextBlock title=new(){Text="New chat",FontSize=17,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock connection=new(){Text="●  Connecting",FontSize=12,Foreground=Brush("#A8B9B2")};
    readonly StackPanel activity=new(){Spacing=12,Margin=new Thickness(18)};
    readonly ScrollViewer activityScroll=new(){VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
    readonly Grid workspace=new(){ColumnDefinitions=new ColumnDefinitions("*,6,300")};
    readonly TextBlock runState = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    Button reviewLatest = null!;
    readonly WrapPanel attachments=new(){Orientation=Orientation.Horizontal};readonly List<string> pendingImages=[];readonly List<FileAttachment> pendingFiles=[];int summaryOffset;
    readonly Button tokenSettings=Button("Profile",null);
    readonly Button send=Button("↑",null,"#B8D6CF");readonly Button stop=Button("■ Stop",null);
    readonly Grid projectBar=new(){ColumnDefinitions=new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),IsVisible=false};
    readonly Button webSearchPermission=Button("Web search allowed · Revoke",null);
    DeskSettings preferences=null!;List<LocalModel> availableModels=[LocalModel.Fast,LocalModel.Large];
    LocalBridge? bridge;Conversation current=new();CancellationTokenSource? cancellation;bool busy,changing;string? loadedModel;
    SelectableTextBlock? streaming;ChatMessage? activeMessage;
    readonly DispatcherTimer paintReply = new() { Interval = TimeSpan.FromMilliseconds(50) };
    bool manuallyStopped;
    public MainWindow(bool preview=false) : this(preview, new OllamaClient(), new ChatStore()) { }

    internal MainWindow(bool preview, OllamaClient client, ChatStore chatStore)
    {
        ollama = client;
        store = chatStore;
        preferences=DeskSettings.Load(store.Root);
        if (OllamaClient.TryOrigin(preferences.ServerOrigin, out var origin))
            ollama.UseOrigin(origin, preferences.CompatibleServer);
        ApplyAppearance();
        ActualThemeVariantChanged += (_, _) => DeskPalette.Apply(ActualThemeVariant);
        ollama.MemoryBudget = new(preferences.ModelRamGiB, preferences.ModelVramGiB);
        InitializeSession();
        paintReply.Tick += (_, _) =>
        {
            if (streaming != null && activeMessage is { } reply && streaming.Text != reply.Text)
            { streaming.Text = reply.Text; Scroll(); }
        };
        Icon=new WindowIcon(Path.Combine(AppContext.BaseDirectory,"Assets","LocalDesk.ico"));
        Title="LocalDesk";Width=1440;Height=900;MinWidth=1150;MinHeight=650;Background=Brush("#181E27");FontFamily=new FontFamily("Segoe UI Variable, Segoe UI");Foreground=Brush("#ECEEEC");
        var root=new Grid{ColumnDefinitions=new ColumnDefinitions("246,*")};Content=root;
        var side=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto,*,Auto"),Margin=new Thickness(14,24,14,18)};
        var sideBorder=new Border{Background=Brush("#11151C"),BorderBrush=Brush("#2A3441"),BorderThickness=new Thickness(0,0,1,0),Child=side};root.Children.Add(sideBorder);
        var brand = new StackPanel { Margin = new Thickness(11, 1, 0, 23), Orientation = Orientation.Horizontal, Spacing = 10 };
        var brandMark = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = Brush("#263A3B"),
            Child = new TextBlock { Text = "◈", FontSize = 20, Foreground = Brush("#B6E0D6"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        brand.Children.Add(brandMark);
        brand.Children.Add(new TextBlock { Text = "LocalDesk", FontSize = 22, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        side.Children.Add(brand);
        var newChat = WorkspaceNav("＋  New chat", (_, _) => NewChat());
        newChat.Foreground = Brush("#D4EDE8");
        newChat.Background = Brush("#284345");
        newChat.Margin = new Thickness(0, 0, 0, 8);
        ToolTip.SetTip(newChat, "New chat · Ctrl+N");
        var topActions = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 14) };
        topActions.Children.Add(newChat);
        topActions.Children.Add(WorkspaceNav("Open a project…", async (_, _) => await OpenProjectFolder()));
        var commands = WorkspaceNav("Quick actions     Ctrl+K", async (_, _) => await ShowCommands());
        topActions.Children.Add(commands);
        topActions.Children.Add(WorkspaceNav("Browse history", async (_, _) => await BrowseHistory()));
        topActions.Children.Add(chatSearch);
        chatSearch.TextChanged+=(_,_)=>RefreshHistory();
        Grid.SetRow(topActions, 1);
        side.Children.Add(topActions);
        Grid.SetRow(history,2);side.Children.Add(history);history.SelectionChanged+=(_,_)=>{if(!changing&&!busy&&history.SelectedItem is Conversation chat)Show(chat);};
        var bottom = new StackPanel { Spacing = 5, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(WorkspaceNav("Models", async (_, _) => await ManageModels()));
        bottom.Children.Add(WorkspaceNav("Projects", async (_, _) => await ManageProjects()));
        bottom.Children.Add(WorkspaceNav("Compare models", async (_, _) => await CompareModels()));
        queueMenu = WorkspaceNav("Run queue", (_, _) => ShowQueue());
        bottom.Children.Add(queueMenu);
        bottom.Children.Add(WorkspaceNav("Mini-window", (_, _) => ShowMiniWindow()));
        bottom.Children.Add(WorkspaceNav("Settings", async (_, _) => await Preferences()));
        pinChat=WorkspaceNav("Pin chat",(_,_)=>TogglePin());bottom.Children.Add(pinChat);
        bottom.Children.Add(WorkspaceNav("Delete selected chat", async (_, _) => await DeleteChat()));
        bottom.Children.Add(new Border { Padding = new Thickness(11, 10), Margin = new Thickness(0, 5, 0, 0),
            CornerRadius = new CornerRadius(10), Background = Brush("#1D2830"), Child = connection });
        Grid.SetRow(bottom, 3);side.Children.Add(bottom);
        var main=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto,*")};Grid.SetColumn(main,1);root.Children.Add(main);
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto,Auto,Auto"),Margin=new Thickness(30,18,26,16)};header.Children.Add(title);Grid.SetColumn(mode,1);header.Children.Add(mode);model.Margin=new Thickness(12,0,0,0);Grid.SetColumn(model,2);header.Children.Add(model);
        ActivityToggle = Button("Hide activity", (_, _) => ToggleActivity());
        ActivityToggle.Margin = new Thickness(12,0,0,0);
        Grid.SetColumn(ActivityToggle,3);
        header.Children.Add(ActivityToggle);
        main.Children.Add(header);
        projectBar.Margin=new Thickness(30,0,28,8);projectBar.Children.Add(project);var browse=Button("Choose folder",async(_,_)=>await Browse());Grid.SetColumn(browse,1);projectBar.Children.Add(browse);var buildCheck=Button("Build check…",async(_,_)=>await ChooseBuildCheck());Grid.SetColumn(buildCheck,2);projectBar.Children.Add(buildCheck);Grid.SetRow(projectBar,1);main.Children.Add(projectBar);
        webSearchPermission.IsVisible=false;
        webSearchPermission.Margin=new Thickness(6,0,0,0);
        webSearchPermission.Click+=(_,_)=>
        {
            if(trustedToolChats.Remove(current.Id)) status.Text="Revoked all-tool approval for this chat.";
            else {current.AllowWebSearch=false;Save();status.Text="Web searches will ask for approval in this chat.";}
            UpdateWebSearchPermission();
        };
        Grid.SetColumn(webSearchPermission,3);
        projectBar.Children.Add(webSearchPermission);
        var setup=Button("Run setup…",async(_,_)=>await ConfigureRun());
        Grid.SetColumn(setup,4);
        projectBar.Children.Add(setup);
        scroller.Content=messages;Grid.SetRow(workspace,2);main.Children.Add(workspace);workspace.ColumnDefinitions[0].MinWidth=470;workspace.ColumnDefinitions[2].MinWidth=220;
        var chatPane=new Grid{RowDefinitions=new RowDefinitions("*,Auto")};workspace.Children.Add(chatPane);chatPane.Children.Add(scroller);
        ConfigureChatScroll(chatPane);
        var splitter=new GridSplitter{ResizeDirection=GridResizeDirection.Columns,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=Brush("#293540"),HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};Grid.SetColumn(splitter,1);workspace.Children.Add(splitter);ToolTip.SetTip(splitter,"Drag to resize chat and activity");
        var activityPane=new Grid{RowDefinitions=new RowDefinitions("Auto,*"),Background=Brush("#141B24")};Grid.SetColumn(activityPane,2);workspace.Children.Add(activityPane);activityPane.Children.Add(new TextBlock{Text="ACTIVITY",FontSize=12,LetterSpacing=1.5,FontWeight=FontWeight.SemiBold,Margin=new Thickness(18,18,18,12),Foreground=Brush("#AFC9BA")});activityScroll.Content=activity;Grid.SetRow(activityScroll,1);activityPane.Children.Add(activityScroll);
        ActivitySplitter=splitter;
        ActivityPanel=activityPane;
        splitter.DragCompleted += (_, _) => SaveActivityWidth();
        ToggleActivity(preferences.ShowActivity);
        var compose=new StackPanel{MaxWidth=900,Spacing=10,Margin=new Thickness(28,8,28,18)};Grid.SetRow(compose,1);chatPane.Children.Add(compose);
        
        AddRecoveryPanel(compose);
        var box=new StackPanel{Spacing=6,Margin=new Thickness(12)};box.Children.Add(attachments);box.Children.Add(input);
        var controls=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,*,Auto")};
        dictateButton = Button("Dictate", (_, _) => ToggleDictation());
        controls.Children.Add(new WrapPanel { Children = { Button("＋  File",async(_,_)=>await Attach()), dictateButton } });
        send.Width=42;send.Height=36;send.Foreground=Brush("#14231C");send.FontSize=22;send.Click+=async(_,_)=>await Send();
        Grid.SetColumn(send,2);controls.Children.Add(send);
        stop.IsVisible=false;stop.Margin=new Thickness(8,0,0,0);stop.Click+=(_,_)=>Stop();
        Grid.SetColumn(stop,2);controls.Children.Add(stop);box.Children.Add(controls);
        queueDraft = Button("Queue", (_, _) => EnqueueDraft());
        queueDraft.Margin = new Thickness(8, 0);
        queueDraft.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(queueDraft, "Save this draft as a separate job with the current project and model.");
        Grid.SetColumn(queueDraft, 1);
        controls.Children.Add(queueDraft);
        var dropZone=new Border{Background=Brush("#212A36"),CornerRadius=new CornerRadius(18),BorderBrush=Brush("#3A4858"),BorderThickness=new Thickness(1),Child=box};compose.Children.Add(dropZone);
        DragDrop.SetAllowDrop(this,true);DragDrop.AddDragOverHandler(this,(_,e)=>{e.DragEffects=CanEditDraft&&e.DataTransfer.Contains(DataFormat.File)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;});DragDrop.AddDropHandler(this,(_,e)=>{if(!CanEditDraft)return;var files=e.DataTransfer.TryGetFiles();if(files!=null)ImportFiles(files.OfType<IStorageFile>().Select(f=>f.Path.LocalPath));e.Handled=true;});
        ToolTip.SetTip(dropZone,"Drop images, text, source code or other files here");
        var footer=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        var runSummary = new StackPanel { Spacing = 4, Children = { runState, status } };
        footer.Children.Add(runSummary);
        reviewLatest = Button("Review changes and checks", async (_, _) =>
        {
            if (LatestProjectResult() is { } result) await ReviewResults(result);
        });
        var footerActions = new StackPanel { Spacing = 5, Children = { speed, reviewLatest } };
        Grid.SetColumn(footerActions,1);footer.Children.Add(footerActions);compose.Children.Add(footer);
        compose.Children.Add(workingHighlight);
        ollama.Speed+=UpdateSpeed;
        ollama.Inference+=timing=>runCollector?.Inference(timing);
        agent.ToolFinished+=seconds=>runCollector?.Tool(seconds);
        agent.ToolCheckpoint+=SaveToolCheckpoint;
        var meter = new StackPanel { Children = { contextUsed, contextBar } };
        var memoryButton = Button("Memory", (_, _) => ShowMemory());
        var advancedActions = new WrapPanel { Children = { tokenSettings, limit, compactButton, memoryButton } };
        tokenSettings.Click += async (_, _) => await ConfigureTokens();
        compactButton.Click += async (_, _) => await CompactContext();
        ToolTip.SetTip(compactButton,"Summarize older context without deleting the visible chat or project files.");
        var advanced = new StackPanel { Spacing = 8, Children = { meter, advancedActions } };
        compose.Children.Insert(compose.Children.Count-1, new Expander { Header = "Run settings and context", Content = advanced });
        ollama.ContextUsed+=UpdateContext;ollama.Status+=text=>Dispatcher.UIThread.Post(()=>{status.Text=text;AddActivity("compaction",text);});
        KeyDown+=async(_,e)=>
        {
            if(!e.KeyModifiers.HasFlag(KeyModifiers.Control))return;
            if(e.Key==Key.M){e.Handled=true;ShowMiniWindow();return;}
            if(busy)return;
            if(e.Key==Key.K){e.Handled=true;await ShowCommands();}
            else if(e.Key==Key.N){e.Handled=true;NewChat();}
        };
        input.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter&&!e.KeyModifiers.HasFlag(KeyModifiers.Shift)){e.Handled=true;await Send();}};
        mode.SelectionChanged+=(_,_)=>{if(changing)return;current.Mode=mode.SelectedIndex==1?"agent":"chat";projectBar.IsVisible=mode.SelectedIndex==1;Save();};
        model.SelectionChanged+=async(_,_)=>{if(changing||busy)return;await SwitchModel(SelectedModel.Id);};
        limit.SelectionChanged+=async(_,_)=>{if(changing||busy||limit.SelectedItem is not TimeOption option)return;if(option.Minutes<0)await ConfigureTimeLimit();else{current.TimeLimitMinutes=option.Minutes;Save();}};
        project.LostFocus+=(_,_)=>CommitProject();
        project.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var path = FolderText(project.Text);
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) { status.Text = "Enter the full path to an existing folder."; return; }
            CommitProject();
            status.Text = "Project folder set to " + current.Project;
            input.Focus();
        };
        agent.Update+=(kind,text)=>Dispatcher.UIThread.Post(()=>{if(kind=="text")Append(text);else{if(kind=="tool"){summaryOffset=activeMessage?.Text.Length??0;SetMiniPhase("Running a tool");}AddActivity(kind,text);status.Text=kind=="tool"?"Working… see Activity":text;}});
        Opened+=async(_,_)=>
        {
            InitializeNotifications();
            RestoreSession();
            if(preview){connection.Text="●  Layout preview";return;}
            await RefreshModels();
            if(preferences.LoadModelAtStartup && installedModels.Any(item => item.Id == current.Model)) await LoadModel();
        };
        Closing+=(_,e)=>
        {
            closingWorkspace = true;
            if(activeCheckpoint!=null || requestStreaming)
            {
                e.Cancel=true;closeAfterRun=true;
                cancellation?.Cancel();agent.Dispose();status.Text="Saving the unfinished run…";return;
            }
            draftTimer.Stop(); quietRunTimer.Stop(); paintReply.Stop(); notifications?.Dispose(); miniWindow?.Close();
            EndDictation(); DisposeSpeaker();
            cancellation?.Cancel();agent.Dispose();bridge?.Dispose();PersistWorkspace(includeChats: true);
        };
    }
    LocalModel SelectedModel=>model.SelectedItem as LocalModel??LocalModel.Fast;

    LocalBridge GetProjectBridge()
    {
        if (bridge != null) return bridge;
        var projectBridge = new LocalBridge(ollama) { ArchiveToolOutput = ArchiveToolOutput };
        projectBridge.Speed += UpdateSpeed;
        projectBridge.Inference += timing => runCollector?.Inference(timing);
        projectBridge.ContextUsed += UpdateContext;
        projectBridge.Window.ToolResultsReduced += (results,tokens) => runCollector?.ReduceToolResults(results,tokens);
        projectBridge.Status += text => Dispatcher.UIThread.Post(() => { status.Text = text; AddActivity("status", text); });
        bridge = projectBridge;
        return projectBridge;
    }
    string ModelKey=>current.Model+"|"+current.ContextTokens;
    void UpdateContext(ContextUsage usage)=>Dispatcher.UIThread.Post(()=>
    {
        current.LastContextUsed=usage.Used;current.ContextUsageEstimated=usage.Estimated;current.ContextNotesOnly=usage.NotesOnly;
        contextUsed.Text=$"{(usage.NotesOnly?"Notes":"Context")} {(usage.Estimated?"≈":"")}{usage.Used:N0} / {usage.Total:N0} tokens ({100.0*usage.Used/usage.Total:F0}%)";
        contextBar.Value=Math.Clamp((double)usage.Used/usage.Total,0,1);contextBar.Foreground=Brush(contextBar.Value>.85?"#D6A46A":"#6EBA8A");
        ToolTip.SetTip(contextUsed,usage.NotesOnly?"Compacted carry-forward notes only. System instructions and tool definitions will be counted on the next model request. ≈ means estimated.":$"Latest model request, including system instructions, tool definitions and generated output. ≈ means estimated; completed requests use server usage. {current.OutputTokens:N0} tokens reserved for output. Auto-compaction starts around 72% or earlier when output needs more room.");
    });
    Task CompactContext()=>CompactContext(false);

    async Task CompactContext(bool preview)
    {
        if (ollama.Compatible) { status.Text = "Local compaction is available with an Ollama connection."; return; }
        if(busy)return;if(current.Messages.Count==0){status.Text="There is no conversation to compact yet.";return;}
        if(loadedModel!=ModelKey || ollama.MemoryBudget.Enabled){await LoadModel();if(loadedModel!=ModelKey)return;}
        using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(4));cancellation=cts;SetBusy(true);SetMiniPhase("Compacting context");manuallyStopped=false;agent.Dispose();status.Text="Compacting context…";AddActivity("compaction","Manual compaction started. Visible history will be kept.");
        try
        {
            var compactor=new ContextCompaction(ollama){Chat=current};compactor.Usage+=UpdateContext;
            if(preview)
            {
                var summary=await compactor.PreviewManual(cts.Token,store.Root);
                if(await ConfirmCompaction(summary,cts.Token) is not { } accepted)
                {status.Text="Compaction preview closed.";return;}
                compactor.ApplyManual(accepted);
            }
            else await compactor.Manual(cts.Token,store.Root);
            status.Text="Context compacted. Continue your request; full chat history is preserved.";AddActivity("compaction",status.Text);
        }
        catch(OperationCanceledException){status.Text="Compaction stopped. Original context was kept.";}
        catch(Exception e){status.Text="Could not compact: "+e.Message;AddActivity("compaction",status.Text);}
        finally{Save();cancellation=null;SetBusy(false);}
    }
    void RefreshTimeLimit()
    {
        var wasChanging=changing;changing=true;
        var options=new[]{5,15,30,60,120,240}.Select(n=>new TimeOption(n,$"{n} min limit")).ToList();
        if(current.TimeLimitMinutes>0&&!options.Any(o=>o.Minutes==current.TimeLimitMinutes))options.Add(new TimeOption(current.TimeLimitMinutes,$"{current.TimeLimitMinutes} min (custom)"));
        options.Add(new TimeOption(-1,"Custom…"));options.Add(new TimeOption(0,"No time limit"));
        limit.ItemsSource=options;limit.SelectedItem=options.First(o=>o.Minutes==current.TimeLimitMinutes);changing=wasChanging;
    }
    async Task ConfigureTimeLimit()
    {
        var dialog=new Window{Title="Custom time limit",Width=420,Height=280,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#202420")};
        var minutes=new NumericUpDown{Minimum=1,Maximum=1440,Increment=5,Value=current.TimeLimitMinutes>0?current.TimeLimitMinutes:60,FormatString="0"};
        var panel=new StackPanel{Margin=new Thickness(24),Spacing=14};panel.Children.Add(new TextBlock{Text="Maximum minutes per request",FontSize=18,TextWrapping=TextWrapping.Wrap});panel.Children.Add(minutes);panel.Children.Add(new TextBlock{Text="Choose 1–1,440 minutes, or use No time limit in the menu. Stop remains available.",TextWrapping=TextWrapping.Wrap,FontSize=12});
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};row.Children.Add(Button("Cancel",(_,_)=>dialog.Close(false)));row.Children.Add(Button("Apply",(_,_)=>{if(minutes.Value!=null)dialog.Close(true);}));panel.Children.Add(row);dialog.Content=panel;
        if(await dialog.ShowDialog<bool>(this)){current.TimeLimitMinutes=(int)minutes.Value!;Save();}RefreshTimeLimit();
    }
    void CommitProject()
    {
        var path=current.BranchFolder??FolderText(project.Text);project.Text=path;
        if(!string.Equals(current.Project.TrimEnd('\\','/'),path.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))
        {
            current.AgentSession=null;
            current.BridgeSummary="";
            current.BridgeBoundary="";
            current.BuildProject=Projects.Find(path)?.BuildTarget;
            agent.Dispose();
        }
        current.Project=path;Save();
    }
    async Task ConfigureTokens()
    {
        if(busy)return;
        var dialog=new Window{Title="Model profile",Width=490,Height=500,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#202420")};
        var context=new NumericUpDown{Minimum=4096,Maximum=262144,Increment=4096,Value=current.ContextTokens,FormatString="0"};
        var output=new NumericUpDown{Minimum=256,Maximum=32768,Increment=1024,Value=current.OutputTokens,FormatString="0"};
        var layout=new StackPanel{Margin=new Thickness(24),Spacing=12};
        layout.Children.Add(new TextBlock{Text="Profile · "+SelectedModel.Name,FontSize=20,FontWeight=FontWeight.SemiBold});
        layout.Children.Add(new TextBlock{Text="Context: conversation, code and response space",TextWrapping=TextWrapping.Wrap});layout.Children.Add(context);
        layout.Children.Add(new TextBlock{Text="Maximum output per model response / tool call"});layout.Children.Add(output);
        var thinking=new CheckBox{Content="Use model thinking when supported",IsChecked=current.Thinking};
        layout.Children.Add(thinking);
        var note=new TextBlock{Text="These settings become the defaults when you select this model. Larger contexts use more GPU memory. Applying starts a fresh agent session.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brush("#ACB8B0")};layout.Children.Add(note);
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};row.Children.Add(Button("Cancel",(_,_)=>dialog.Close(false)));
        row.Children.Add(Button("Apply",(_,_)=>{if(context.Value==null||output.Value==null||output.Value>=context.Value/2){note.Text="Output must be less than half the context size. For example, use 32,768 context and 8,192 output.";return;}dialog.Close(true);}));layout.Children.Add(row);dialog.Content=layout;
        if(await dialog.ShowDialog<bool>(this)){current.ContextTokens=(int)context.Value!;current.OutputTokens=(int)output.Value!;current.Thinking=thinking.IsChecked==true;preferences.Profiles[current.Model]=new(current.ContextTokens,current.OutputTokens,current.Thinking);preferences.Save(store.Root);current.AgentSession=null;agent.Dispose();Save();await LoadModel();}
    }
    static Button Button(string text,EventHandler<Avalonia.Interactivity.RoutedEventArgs>? click, string? bg=null)
    {
        var b=new Button{Content=text,CornerRadius=new CornerRadius(9),Padding=new Thickness(12,8),Background=Brush(bg??"#242824"),BorderBrush=Brush("#384553"),BorderThickness=new Thickness(bg==null?1:0)};if(click!=null)b.Click+=click;return b;
    }
    Conversation NewConversation(SavedProject? entry = null)
    {
        var chat = new Conversation
        {
            Mode = entry != null || preferences.StartInAgent ? "agent" : "chat",
            Project = entry?.Folder ?? (preferences.StartInAgent ? preferences.Folder : ""),
            BuildProject = entry?.BuildTarget,
            Model = entry?.Model ?? preferences.DefaultModel ?? installedModels.FirstOrDefault()?.Id ?? LocalModel.Fast.Id
        };
        var profile = ProfileFor(chat.Model);
        chat.ContextTokens = profile.Context;
        chat.OutputTokens = profile.Output;
        chat.Thinking = profile.Thinking;
        store.Chats.Add(chat);
        store.Save();
        return chat;
    }
    void NewChat(){if(busy)return;Show(NewConversation());RefreshHistory();}
    void UpdateSpeed(GenerationSpeed value)=>Dispatcher.UIThread.Post(()=>
    {
        if(busy||sendingRequest)miniSpeed=value;
        speed.Text=(value.TokensPerSecond>0?$"{(value.Estimated?"≈":"")}{value.TokensPerSecond:F1} tok/s":"— tok/s")+(value.Finished?$" · {(value.TokenCountEstimated?"≈":"")}{value.Tokens} tokens":" · live");
        ToolTip.SetTip(speed,value.Estimated?"Estimated generation rate, excluding time waiting for the first token. In agent mode this is the latest model call, including generated tool calls. Token counts come from server usage when available; ≈ marks an estimate.":"Output tokens divided by Ollama's generation duration. Excludes model loading and prompt processing.");
    });
    void Show(Conversation chat)
    {
        if(chat.LastContextUsed==0&&chat.AgentSession!=null)
        {
            try{var root=Path.GetFullPath(Path.Combine(store.Root,"cline","sessions"));var path=Path.GetFullPath(Path.Combine(root,chat.AgentSession,chat.AgentSession+".messages.json"));if(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)){var records=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?["messages"]?.AsArray();var metrics=records?.LastOrDefault(m=>m?["metrics"]?["inputTokens"]!=null)?["metrics"];if(metrics!=null){chat.LastContextUsed=(metrics["inputTokens"]?.GetValue<int>()??0)+(metrics["outputTokens"]?.GetValue<int>()??0);chat.ContextUsageEstimated=true;}}}catch{}
        }
        changing=true;current=chat;speed.Text="— tok/s";title.Text=chat.Title;if(!availableModels.Any(m=>m.Id==chat.Model))availableModels.Add(LocalModel.FromTag(chat.Model));model.ItemsSource=availableModels.ToList();model.SelectedItem=availableModels.First(m=>m.Id==chat.Model);ToolTip.SetTip(model,SelectedModel.Note);mode.SelectedIndex=chat.Mode=="agent"?1:0;projectBar.IsVisible=chat.Mode=="agent";project.Text=chat.Project;pendingImages.Clear();pendingFiles.Clear();attachments.Children.Clear();RestoreDraft();messages.Children.Clear();messageCards.Clear();activeMessage=null;streaming=null;activity.Children.Clear();foreach(var entry in chat.Activity)RenderActivity(entry);if(chat.Activity.Count==0)activity.Children.Add(new TextBlock{Text="Tool calls, file changes and checks will appear here.",FontSize=13,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#84958A")});
        if(chat.Messages.Count==0)RenderWelcome();else foreach(var m in chat.Messages)AddMessage(m);
        UpdateRunState();
        project.IsEnabled=!busy&&chat.BranchFolder==null;UpdateWebSearchPermission();history.SelectedItem=chat;UpdatePin();RefreshTimeLimit();changing=false;ShowRecovery();UpdateContext(new(chat.LastContextUsed,chat.ContextTokens,chat.ContextUsageEstimated,chat.ContextNotesOnly));FollowLatest();PersistWorkspace(includeChats: draftTimer.IsEnabled);Dispatcher.UIThread.Post(()=>activityScroll.ScrollToEnd(),DispatcherPriority.Background);
    }
    void UpdateWebSearchPermission()
    {
        var all = trustedToolChats.Contains(current.Id);
        webSearchPermission.IsVisible = all || (current.AllowWebSearch && current.BranchFolder == null);
        webSearchPermission.Content = all ? "Tools approved · Revoke" : "Web search allowed · Revoke";
        ToolTip.SetTip(webSearchPermission, all ? "Restore individual tool approvals for this chat." : "Revoke remembered search approval for this chat.");
    }
    void RefreshHistory(){changing=true;var matches=ChatCatalog.Search(store.Chats.Where(c=>!session.Pending.Contains(c.Id)),chatSearch.Text);history.ItemsSource=matches;history.SelectedItem=matches.Contains(current)?current:null;changing=false;}
    void Save(){current.Updated=DateTime.Now;store.Save();}
    void AddActivity(string kind,string text)
    {
        if (requestStreaming) RecordRunProgress();
        if(current.Activity.Count==0)activity.Children.Clear();var entry=new ActivityEntry(DateTime.Now,kind,text);current.Activity.Add(entry);
        if(current.Activity.Count>500){current.Activity.RemoveAt(0);if(activity.Children.Count>0)activity.Children.RemoveAt(0);}
        RenderActivity(entry);Dispatcher.UIThread.Post(()=>activityScroll.ScrollToEnd(),DispatcherPriority.Background);
    }
    void RenderActivity(ActivityEntry entry)
    {
        if(entry.Kind=="tool output" && File.Exists(entry.Text))
        {
            activity.Children.Add(Button("View full tool output",async(_,_)=>await ViewToolOutput(entry.Text)));
            return;
        }
        var card=new StackPanel{Spacing=5};card.Children.Add(new TextBlock{Text=$"{entry.Time:HH:mm:ss} · {entry.Kind.ToUpperInvariant()}",FontSize=10,Foreground=Brush("#8B9D91")});card.Children.Add(new SelectableTextBlock{Text=entry.Text,FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#CCD6CF")});activity.Children.Add(new Border{Child=card,Padding=new Thickness(12),CornerRadius=new CornerRadius(9),Background=Brush("#202A35")});
    }
    Border SummaryCard(ChatMessage message)
    {
        var body=new StackPanel{Spacing=7};body.Children.Add(new TextBlock{Text=message.Successful?"✓  RESULT":"!  TASK INCOMPLETE",FontSize=12,FontWeight=FontWeight.Bold,Foreground=Brush(message.Successful?"#A8E6BC":"#E8C585")});
        var text=message.Summary??"";
        if (text.Length > 0) body.Children.Add(Button("Read aloud", (_, _) => ReadAloud(text)));
        var preview=SummaryPreview.Create(text);
        body.Children.Add(RenderMarkdown(preview,ChatTextSize));
        if(preview!=text)body.Children.Add(new Expander{Header="Full summary",Content=RenderMarkdown(text,ChatTextSize)});
        if(message.ProjectUsed!=null)body.Children.Add(EvidenceText($"{message.VerifiedFiles.Count} verified file change{(message.VerifiedFiles.Count == 1 ? "" : "s")} · {CheckOutcome(message)}"));
        if(message.ProjectUsed!=null || message.Artifacts.Count>0)body.Children.Add(Button("Review results",async(_,_)=>await ReviewResults(message)));
        AddEvidence(body, message);
        if(message.Diagnostics!=null)body.Children.Add(Button("Run diagnostics",async(_,_)=>await ShowDiagnostics(message.Diagnostics)));
        var index=current.Messages.IndexOf(message);
        if(index>=0)body.Children.Add(Button("Branch from here…",async(_,_)=>await BranchChat(message)));
        if(index==current.Messages.Count-1 && message.ProjectUsed!=null && !message.Successful)
            body.Children.Add(Button("Hand off to another model…",async(_,_)=>await Handoff(message)));
        if(current.BranchFolder!=null&&message.ProjectUsed!=null)body.Children.Add(Button("Apply branch changes…",async(_,_)=>await ApplyBranchChanges()));
        var references = message.Artifacts.Where(a=>File.Exists(a.Path) &&
            !message.VerifiedFiles.Any(f=>f.Path.Equals(a.Path,StringComparison.OrdinalIgnoreCase))).ToList();
        if(message.ProjectUsed!=null && references.Count>0)body.Children.Add(EvidenceText("Files referenced in the response"));
        foreach(var file in references)
        {
            body.Children.Add(PreviewLink("Preview · "+file.Name,file.Path));
        }
        return new Border{Child=body,Padding=new Thickness(14),CornerRadius=new CornerRadius(12),Background=Brush(message.Successful?"#183A27":"#3B3020"),BorderBrush=Brush(message.Successful?"#39794F":"#8A6938"),BorderThickness=new Thickness(1)};
    }
    void CompleteMessage(ChatMessage reply,bool successful)
    {
        reply.SummaryOffset=successful&&current.Mode=="agent"?Math.Clamp(summaryOffset,0,reply.Text.Length):0;
        reply.Summary=reply.Text[reply.SummaryOffset..].Trim();if(reply.Summary.Length==0)reply.Summary="No final summary was returned. Review Activity for details.";
        reply.Successful=successful;reply.Artifacts=Artifacts.Find(reply.Text,current.Project,reply.VerifiedFiles.Select(f=>f.Path));if(messages.Children.Count>0)messages.Children.RemoveAt(messages.Children.Count-1);
        AddMessage(reply);
        AddActivity(successful?"finished":"incomplete",successful?"Response finished. Review the summary in Chat.":
            reply.PauseReason??reply.EvidenceNote??"The request stopped before completion. Any changes already made remain on disk.");Scroll();
    }
    static void RevealFile(string path)
    {
        if(!File.Exists(path))return;
        var start=new System.Diagnostics.ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.Arguments="/select,\""+path+"\"";System.Diagnostics.Process.Start(start);
    }
    void EnableFileDrag(Control source,string path)
    {
        PointerPressedEventArgs? pressed=null;Point start=default;
        source.AddHandler(PointerPressedEvent,(_,e)=>{if(e.GetCurrentPoint(source).Properties.IsLeftButtonPressed){pressed=e;start=e.GetPosition(source);}},Avalonia.Interactivity.RoutingStrategies.Tunnel);
        source.PointerReleased+=(_,_)=>pressed=null;
        source.PointerMoved+=async(_,e)=>
        {
            var position=e.GetPosition(source);if(pressed==null||!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed||Math.Abs(position.X-start.X)+Math.Abs(position.Y-start.Y)<8)return;
            var initial=pressed;pressed=null;
            try{var file=await StorageProvider.TryGetFileFromPathAsync(path);if(file==null)return;var data=new DataTransfer();data.Add(DataTransferItem.CreateFile(file));await DragDrop.DoDragDropAsync(initial,data,DragDropEffects.Copy);}catch(Exception error){status.Text="Could not drag file: "+error.Message;}
        };
    }
    void AddMessage(ChatMessage message)
    {
        var body=new StackPanel{Spacing=9};body.Children.Add(new TextBlock{Text=message.Role=="user"?"YOU":"LOCALDESK",FontSize=10,LetterSpacing=1.2,FontWeight=FontWeight.SemiBold,Foreground=Brush(message.Role=="user"?"#9AA69E":"#B8D6CF")});
        if(message.Images.Count>0)
        {
            var thumbs=new WrapPanel();
            foreach(var file in message.Images.Where(File.Exists)) thumbs.Children.Add(ImagePreviewLink(file,160,110));
            body.Children.Add(thumbs);
        }
        foreach(var file in message.Files)body.Children.Add(AttachmentLink(file));
        var text=message.Summary==null?message.Text:message.Text[..Math.Clamp(message.SummaryOffset,0,message.Text.Length)].Trim();
        var live=message.Role=="assistant"&&message.Summary==null?ChatText(text,ChatTextSize):null;
        if(message.Summary!=null&&!string.IsNullOrWhiteSpace(text))body.Children.Add(new Expander{Header="Response details",Content=RenderMarkdown(text,ChatTextSize)});
        else if(live!=null || text.Length>0)body.Children.Add(live is not null?live:RenderMarkdown(text,ChatTextSize));
        if(message.Summary!=null)body.Children.Add(SummaryCard(message));
        var border=new Border{Child=body,CornerRadius=new CornerRadius(14),Padding=new Thickness(18),Background=message.Role=="user"?Brush("#273744"):Brush("#181E27")};messages.Children.Add(border);
        messageCards[message.Marker]=border;
        if(message.Role=="assistant"){streaming=live;activeMessage=message;}
    }
    void Append(string text){if(activeMessage==null)return;RecordRunProgress();if(requestStreaming)SetMiniPhase("Writing response");activeMessage.Text+=text;}
    bool scrollQueued;
    void Scroll()
    {
        if(scrollQueued||!followChat)return;
        scrollQueued=true;
        Dispatcher.UIThread.Post(()=>{scrollQueued=false;if(followChat)scroller.ScrollToEnd();},DispatcherPriority.Background);
    }
    void SetBusy(bool value)
    {
        if (value && !busy && !sendingRequest) StartMiniClock();
        busy = value;
        UpdateRunState();
        if (value && requestStreaming) paintReply.Start();
        else paintReply.Stop();
        workingHighlight.IsIndeterminate = workingHighlight.IsVisible = value;
        send.IsVisible = !value;
        stop.IsVisible = value;
        model.IsEnabled = mode.IsEnabled = history.IsEnabled = tokenSettings.IsEnabled = limit.IsEnabled = compactButton.IsEnabled = !value;
        project.IsEnabled = !value && current.BranchFolder == null;
        input.IsReadOnly = !CanEditDraft;
        UpdateQueueButton();
        ShowRecovery();
        if (!value && !sendingRequest)
        {
            OpenNotificationDestination();
            ScheduleQueue();
        }
    }
    ChatMessage? LatestProjectResult() => current.Messages.LastOrDefault(message => message.ProjectUsed != null && message.Summary != null);

    void UpdateRunState()
    {
        var result = LatestProjectResult();
        runState.Text = approvalWindow != null ? "Needs your approval" : busy ? "Working"
            : result == null ? "" : result.Successful ? "Ready to review" : "Run needs attention";
        runState.IsVisible = runState.Text.Length > 0;
        reviewLatest.IsVisible = !busy && result != null;
    }

    async Task LoadModel()
    {
        if(busy)return;
        var canReuse = ollama.MemoryBudget.Enabled && loadedModel == ModelKey;
        loadedModel=null;SetBusy(true);using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(4));cancellation=cts;
        SetMiniPhase("Loading model");
        speed.Text="— tok/s";status.Text="Loading "+SelectedModel.Name+"…";connection.Text="●  Loading model";
        try
        {
            if (!canReuse || !await ollama.EnforceMemoryBudget(current.Model, cts.Token, allowUnloaded: true))
                await ollama.Load(current.Model, cts.Token, current.ContextTokens);
            loadedModel=ModelKey;connection.Text="●  " + ollama.Http.BaseAddress!.Authority;status.Text=$"{current.ContextTokens:N0} context · {current.OutputTokens:N0} max output · Thinking {(current.Thinking?"when supported":"off")}";
            UpdateContext(new(current.LastContextUsed,current.ContextTokens,current.ContextUsageEstimated,current.ContextNotesOnly));
        }
        catch(OperationCanceledException){status.Text="Model loading stopped.";connection.Text="●  Stopped";}
        catch(ModelMemoryBudgetException e){status.Text=e.Message;connection.Text="●  Memory budget reached";}
        catch(Exception e){status.Text=e.Message;connection.Text="●  Not connected";}
        finally{cancellation=null;SetBusy(false);}
    }
    bool sendingRequest;
    async Task Send(ChatMessage? resume = null, ChatMessage? paused = null, RunCheckpoint? priorCheckpoint = null, ModelHandoff? handoff = null)
    {
        if (sendingRequest || busy || closingWorkspace || session.Pending.Contains(current.Id)) return;
        if (dictation != null)
        { status.Text = "Stop dictation and review the draft before sending."; return; }
        sendingRequest = true;
        StartMiniClock();
        try { await SendCore(resume, paused, priorCheckpoint, handoff); }
        finally { sendingRequest = false; OpenNotificationDestination(); ScheduleQueue(); }
    }

    async Task SendCore(ChatMessage? resume, ChatMessage? paused, RunCheckpoint? priorCheckpoint, ModelHandoff? handoff)
    {
        var requestWatch=System.Diagnostics.Stopwatch.StartNew();double loadSeconds=0;bool thinkingForRun=false;var prompt=resume?.Text??input.Text?.Trim()??"";if(prompt.Length==0&&(resume?.Images.Count??pendingImages.Count)==0&&(resume?.Files.Count??pendingFiles.Count)==0)return;
        if(resume==null&&RunJournal.Load(store.Root,current.Id)!=null)
        {
            status.Text="Resume or dismiss this chat’s saved request, or start a new chat.";
            return;
        }
        CommitProject();
        if (ollama.Compatible && current.Mode == "agent")
        { status.Text = "This OpenAI-compatible connection supports Chat. Choose an Ollama connection for Project agent."; return; }
        if(current.Mode=="agent"&&!Directory.Exists(current.Project)){status.Text="Choose a project folder to use Project agent.";return;}
        if(loadedModel!=ModelKey || ollama.MemoryBudget.Enabled){var loadWatch=System.Diagnostics.Stopwatch.StartNew();await LoadModel();loadSeconds=loadWatch.Elapsed.TotalSeconds;if(closingWorkspace||loadedModel!=ModelKey)return;}
        using var capabilityCheck=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        SetBusy(true);
        SetMiniPhase("Checking model capabilities");
        cancellation=capabilityCheck;
        manuallyStopped=false;
        try
        {
            var caps=await ollama.Capabilities(current.Model,capabilityCheck.Token);
            thinkingForRun=current.Thinking&&caps.Contains("thinking");
            if(caps is {Count:>0})
            {
                if(current.Mode=="agent"&&!caps.Contains("tools"))
                {
                    status.Text="This model does not support project tools. Switch to Chat or choose a tool-capable model.";
                    return;
                }
                if((resume?.Images.Count>0||pendingImages.Count>0)&&!caps.Contains("vision"))
                {
                    status.Text="This model does not support images. Select a vision model or remove the images.";
                    return;
                }
            }
        }
        catch(OperationCanceledException){status.Text=manuallyStopped?"Stopped.":"The model capability check timed out.";return;}
        catch(Exception error){status.Text="Could not check model capabilities: "+error.Message;return;}
        finally{cancellation=null;SetBusy(false);}
        if (closingWorkspace) return;
        var user=resume==null
            ?new ChatMessage{Text=prompt.Length>0?prompt:pendingImages.Count>0?"Describe this image.":"Review the attached files.",Images=pendingImages.ToList(),Files=pendingFiles.ToList()}
            :new ChatMessage{Text=resume.Text,Images=resume.Images.ToList(),Files=resume.Files.ToList()};
        if(resume==null){pendingImages.Clear();pendingFiles.Clear();attachments.Children.Clear();input.Text="";CaptureDraft();}
        else { agent.Dispose(); current.AgentSession=null; }
        if(current.Messages.Count==0){messages.Children.Clear();current.Title=user.Text.Length>34?user.Text[..34]+"…":user.Text;title.Text=current.Title;}
        current.Messages.Add(user);AddMessage(user);FollowLatest();
        var reply=new ChatMessage{Role="assistant"};current.Messages.Add(reply);AddMessage(reply);Save();RefreshHistory();summaryOffset=0;AddActivity("request",user.Text);
        manuallyStopped=false;using var cts=current.TimeLimitMinutes==0?new CancellationTokenSource():new CancellationTokenSource(TimeSpan.FromMinutes(current.TimeLimitMinutes));cancellation=cts;requestStreaming=true;RecordRunProgress();SetBusy(true);status.Text=current.Mode=="agent"?"Working in "+Path.GetFileName(current.Project)+"…":"Thinking…";
        SetMiniPhase(current.Mode=="agent"?"Preparing project":"Generating response");
        speed.Text="— tok/s · waiting";var watch=System.Diagnostics.Stopwatch.StartNew();
        ProjectEvidence? evidence = null;
        bool expectsChanges = current.Mode == "agent" && EditRunOutcome.ExpectsChanges(user.Text,
            current.Messages.SkipLast(2).LastOrDefault(m => m.Role == "assistant") is { ProjectUsed: not null } previousReply &&
            (previousReply.VerifiedFiles.Count > 0 || previousReply.ExpectedEdits));
        reply.ExpectedEdits = expectsChanges;
        bool successful = false, settled = false;
        RunCheckpoint? checkpoint = null;
        double preparation=0,verify=0,buildSeconds=0;
        runCollector=new RunCollector(loadSeconds);
        try
        {
            if(current.Mode=="chat")await ollama.Chat(current,text=>Dispatcher.UIThread.Post(()=>Append(text)),cts.Token,thinkingForRun);
            else
            {
                var projectBridge = GetProjectBridge();
                projectBridge.ActiveCancellation = cts.Token;
                foreach (var message in current.Messages.Where(message => message.Images.Count > 0))
                    projectBridge.Images[message.Marker] = message.Images;
                var notes = ProjectNotes();
                checkpoint=new RunCheckpoint{Request=user.Marker,Reply=reply.Marker,Project=current.Project,
                    BuildTarget=current.BranchFolder==null&&current.BuildProject is { } target&&IsBuildProject(target)?target:null,
                    FocusedCheck=priorCheckpoint==null?current.AcceptanceCheck??notes?.Verification:priorCheckpoint.FocusedCheck,
                    RepairAttempts=priorCheckpoint?.RepairAttempts??(handoff==null?0:paused?.RepairAttempts??0),
                    RepairFeedback=priorCheckpoint?.RepairFeedback,HandoffText=handoff?.Notes??priorCheckpoint?.HandoffText,
                    AcceptanceCriteria=priorCheckpoint?.AcceptanceCriteria??current.AcceptanceCriteria,
                    ReviewBeforeChanges=priorCheckpoint?.ReviewBeforeChanges??current.ReviewBeforeChanges,
                    ReviewAcknowledged=priorCheckpoint?.ReviewAcknowledged??false,Steering=priorCheckpoint?.Steering??""};
                reply.RepairAttempts=checkpoint.RepairAttempts;
                RunJournal.Save(store.Root,current.Id,checkpoint);
                activeCheckpoint=checkpoint;
                reply.ProjectUsed=current.Project;
                reply.AcceptanceCriteria=checkpoint.AcceptanceCriteria;
                reply.RunModel=current.Model;
                projectBridge.Window.Chat=current;
                projectBridge.Window.ReduceDuplicateReads=preferences.ReduceDuplicateReads;
                var task = "Active user request (verbatim):\n" + user.Text;
                const string editGuidance = "Trace the reported behavior in the exact screen and event handler the user describes. When a prior fix failed, inspect that change and test the failing path. Make the smallest relevant edit. Preserve file encoding and keep temporary inspection files outside the project. If you can only check source or syntax, state that the interaction still needs a runtime test.";
                if (expectsChanges) task += "\n" + editGuidance;
                if (checkpoint.AcceptanceCriteria.Length > 0)
                    task += "\n\nUser's acceptance criteria:\n" + checkpoint.AcceptanceCriteria;
                if (checkpoint.Steering.Length > 0)
                    task += "\n\nLatest direction from the user for this saved run:\n" + checkpoint.Steering;
                if (user.Files.Count > 0)
                    task += "\n\nAttached file paths:\n" + string.Join("\n", user.Files.Select(file => file.SourcePath));
                projectBridge.Window.ActiveTask = task;
                projectBridge.Thinking=thinkingForRun;
                projectBridge.ReviewGate=new ToolReviewGate(TimedReview,current.BranchFolder);
                status.Text="Checking the starting files…";
                evidence=await Task.Run(()=>new ProjectEvidence(current.Project,preferences.Folder,current.Messages.Where(m=>m.Role=="user").TakeLast(5).SelectMany(m=>m.Files).Select(f=>f.SourcePath)));
                await Task.Run(()=>evidence.SaveBaseline(RunJournal.Baseline(store.Root,current.Id,reply.Marker)));
                if(priorCheckpoint!=null)RunJournal.RemoveBaseline(store.Root,current.Id,priorCheckpoint);
                cts.Token.ThrowIfCancellationRequested();
                var pinned = (notes?.PinnedFiles ?? []).Select(relative=>ProjectBrief.Resolve(current.Project,relative)).OfType<string>();
                var scope = user.Files.Select(f=>f.SourcePath).Concat(pinned)
                    .Concat((handoff?.FilePaths??[]).Where(path=>WorkspaceScope.RelativeFile(current.Project,path)!=null&&File.Exists(path))).ToArray();
                var focus = await Task.Run(() => WorkspaceScope.Focus(current.Project, preferences.Folder,
                    user.Text, scope, cts.Token), cts.Token);
                var brief = await Task.Run(()=>ProjectBrief.Build(notes,current.Project,current.ContextTokens),cts.Token);
                var code = await Task.Run(()=>CodeFocus.Find(current.Project,preferences.Folder,user.Text,scope,cts.Token),cts.Token);
                if(code.Length>0)AddActivity("search",code.Trim());
                preparation=watch.Elapsed.TotalSeconds;
                var key=current.Id+"|"+current.Model+"|"+current.Project+"|"+current.ContextTokens+"|"+current.OutputTokens;
                var previousSession=current.AgentSession;
                if(agent.Key!=key)await agent.Start(current,projectBridge,store.Root,cts.Token);
                var text=user.Text+AttachedFiles.Context(user.Files,true)+"\nEnd with at most three concise bullets labeled Outcome, Checks, and Remaining. State the actual result, checks actually run (or not run), and unfinished work. Include full paths in backticks for changed files and outputs. Avoid repeating the activity log or claiming success without evidence."+(user.Images.Count>0?"\n[LocalDeskImage:"+user.Marker+"]":"");
                if(previousSession!=current.AgentSession)
                {
                    var prior=(current.ContextSummary.Length>0?"Carry-forward notes:\n"+current.ContextSummary+"\n\n":"")+string.Join("\n\n",current.Messages.Take(current.Messages.Count-2).Skip(current.CompactedMessageCount).TakeLast(6).Select(m=>m.Role+": "+(m.Text.Length>3000?m.Text[..3000]:m.Text)));
                    text="Selected project folder: "+current.Project+"\nWork on requested files only. Attached files may be outside the working folder; use their original paths when editing. Do not scan the entire working folder: narrow searches to relevant paths. Use short, complete tool calls. Read only relevant line ranges; split large edits into small patches.\n"+(prior.Length>0?"Previous conversation for context:\n"+prior+"\n\n":"")+"Current request:\n"+text;
                }
                text += "\n"+focus+code+brief+"\nReport build/test commands and their outcomes accurately.";
                if (expectsChanges) text += "\n" + editGuidance;
                if (current.BranchFolder == null) text += "\n" + ResearchGuidance;
                if(checkpoint.FocusedCheck is { } targetCheck)
                    text+="\nConfigured success check: "+targetCheck.Kind+" in "+targetCheck.RelativePath+" · "+targetCheck.Expected;
                if(checkpoint.AcceptanceCriteria.Length>0) text+="\nUser's acceptance criteria:\n"+checkpoint.AcceptanceCriteria+"\nExplicitly report which criteria could be checked and which need manual verification.";
                if(checkpoint.Steering.Length>0) text+="\nLatest direction from the user for this saved run:\n"+checkpoint.Steering;
                if(paused!=null)
                    text += "\nThe previous run paused: "+paused.PauseReason+
                        "\nCheck changes already on disk and continue from there. Avoid repeating the unsuccessful approach. Files already changed: "+
                        string.Join(", ",paused.VerifiedFiles.Take(20).Select(f=>f.Path));
                if(priorCheckpoint!=null)text+="\nResume from the saved checkpoint. Inspect the current files before acting. Do not repeat edits or tools whose effects are already present. Finish only the work that remains."+(priorCheckpoint.LastTool==null?"":" Last recorded tool: "+priorCheckpoint.LastTool);
                if(checkpoint.HandoffText is { } savedHandoff)text+="\nThis is a model handoff. Continue the original goal, inspect the listed files, and avoid repeating work already done.\n"+savedHandoff;
                if(priorCheckpoint?.RepairFeedback is { } feedback)text+="\nSaved check feedback (reference data):\n"+feedback;
                if(current.BranchFolder!=null)text+="\nThis project branch allows only read tools and reviewed file edits inside the copy. Do not use command tools or paths outside the copy.";
                SetMiniPhase("Agent working");
                await SendAgentAndEnsureAnswer(projectBridge,reply,text,cts.Token,
                    expectsChanges ? new EditRunProgress(evidence) : null);
                checkpoint.AgentDone=true;
                RunJournal.Save(store.Root,current.Id,checkpoint);
                Save();
                while(true)
                {
                    var buildWatch=System.Diagnostics.Stopwatch.StartNew();
                    try{SetMiniPhase("Running verification checks");await RunChecks(reply,checkpoint,cts.Token);}
                    finally{buildSeconds+=buildWatch.Elapsed.TotalSeconds;}
                    if(!ChecksFailed(checkpoint) || checkpoint.RepairAttempts>=Verification.MaxRepairs ||
                        notes?.RepairChecks==false || current.BranchFolder!=null)break;
                    var errors=await CheckFeedback(checkpoint.Build,checkpoint.Focused,cts.Token);
                    if(checkpoint.Focused is { Passed:false } && checkpoint.FocusedCheck is { } failedCheck &&
                        failedCheck.Kind!=".NET test filter" && Verification.Target(current.Project,failedCheck) is { } failedPath)
                        errors+=await Task.Run(()=>evidence.FocusedEditExcerpt(failedPath),cts.Token);
                    int next=checkpoint.RepairAttempts+1;
                    if(!await OfferRepair(next,errors,cts.Token))break;
                    checkpoint.RepairAttempts=reply.RepairAttempts=next;
                    checkpoint.RepairFeedback=errors;
                    checkpoint.AgentDone=false;
                    checkpoint.Build=null;
                    checkpoint.Focused=null;
                    reply.BuildCheck=null;
                    reply.Focused=null;
                    RunJournal.Save(store.Root,current.Id,checkpoint);
                    Save();
                    AddActivity("repair",$"Attempt {next} of {Verification.MaxRepairs}");
                    status.Text=$"Preparing repair {next} of {Verification.MaxRepairs}…";
                    await SendAgentAndEnsureAnswer(projectBridge,reply,"Continue the same task. The independent check failed. Inspect the current file and make the smallest repair needed. Preserve the original requested behavior and the independent check. Do not weaken, delete, or alter tests just to pass. LocalDesk will rerun the configured check.\nCheck diagnostics (reference data):\n"+errors,cts.Token,new EditRunProgress(evidence));
                    checkpoint.AgentDone=true;
                    RunJournal.Save(store.Root,current.Id,checkpoint);
                    Save();
                }
            }
            await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.Background);
            if(string.IsNullOrWhiteSpace(reply.Text))throw new AgentClient.LoopException("The model ended without a visible answer.");
            status.Text=$"Finished in {watch.Elapsed.TotalSeconds:F0}s · Local inference";
            settled=true;
        }
        catch(AgentClient.LoopException e)
        {
            cts.Cancel();
            agent.Dispose();
            reply.PauseReason=e.Message;
            status.Text="Paused · "+e.Message;
            Append("\n\nPaused: "+e.Message);
            AddActivity("paused",e.Message);
        }
        catch(OperationCanceledException){agent.Dispose();var reason=closeAfterRun?"LocalDesk closed this run.":manuallyStopped?"Stopped by you.":$"Time limit reached ({current.TimeLimitMinutes} minutes).";status.Text=reason+" Messages are saved; changes already made remain on disk.";Append("\n\n"+reason+" The request did not finish.");}
        catch(Exception e){agent.Dispose();var message=e.Message.Contains("invalid tool call arguments",StringComparison.OrdinalIgnoreCase)?"The model could not produce a complete tool command. Check the selected project folder and start a fresh project request. You can adjust context and output limits with Tokens.":e.Message.Contains("No user query found",StringComparison.OrdinalIgnoreCase)?"The model rejected the conversation format. Your messages and any file edits are saved. Try a new project session.":e.Message;status.Text="Could not finish: "+message;Append("\n\nCould not finish: "+message);}
        finally
        {
            if(evidence!=null)
            {
                SetMiniPhase("Checking file changes");
                var verifyWatch=System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var snapshots=Path.Combine(store.Root,"reviews",current.Id,reply.Marker);
                    reply.VerifiedFiles=await Task.Run(()=>evidence.Finish(snapshots));
                    if(evidence.Limited)reply.EvidenceNote=(reply.EvidenceNote+" File check covered only part of the workspace.").Trim();
                    if(!evidence.ScansProject)reply.EvidenceNote=(reply.EvidenceNote+" File check covered attached files only for this working folder.").Trim();
                }
                catch(Exception e){settled=false;successful=false;reply.EvidenceNote="File verification could not finish: "+e.Message;}
                verify=verifyWatch.Elapsed.TotalSeconds;
            }
            if (settled)
            {
                var unverified = EditRunOutcome.MarkUnverified(reply,expectsChanges);
                successful = !unverified && (checkpoint==null || !ChecksFailed(checkpoint));
                if(!successful)status.Text=reply.BuildCheck is { ExitCode:not 0 } build?$"Build failed (exit {build.ExitCode})":
                    reply.Focused is { Passed:false }?"Focused check failed.":unverified?"No verified file changes.":reply.EvidenceNote;
            }
            reply.Diagnostics=runCollector.Finish(requestWatch.Elapsed.TotalSeconds,preparation,verify,buildSeconds);
            runCollector=null;
            reply.CompletedAt=DateTime.Now;
            if (reply.ProjectUsed != null) reply.RunSeconds=watch.Elapsed.TotalSeconds;
            CompleteMessage(reply,successful);
            Save();
            if(checkpoint!=null&&settled)
            {
                try{RunJournal.Complete(store.Root,current.Id,checkpoint);}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException){status.Text=error.Message;}
            }
            activeCheckpoint=null;
            cancellation=null;
            if (bridge != null) { bridge.ActiveCancellation=default; bridge.ReviewGate=null; bridge.Window.ActiveTask=""; }
            requestStreaming=false;
            miniResult=(current,reply,miniSpeed);
            NotifyRun(current,reply,successful?"LocalDesk run finished":"LocalDesk run needs attention",
                successful?"Open LocalDesk to review the result.":"Open LocalDesk to review the result and Activity.");
            SetBusy(false);
            if(IsActive)input.Focus();
            FinishClosing();
        }
    }
    void Stop(){manuallyStopped=true;SetMiniPhase("Stopping");cancellation?.Cancel();agent.Dispose();status.Text="Stopping…";}
    async Task Browse(){if(busy)return;if(current.BranchFolder!=null){status.Text="This branch uses its project copy.";return;}if(await PickProjectPath(current.Project) is { } folder){project.Text=folder;CommitProject();}}
    async Task Attach()
    {
        if(!CanEditDraft)return;var chat=current;var picked=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="Attach files",AllowMultiple=true});if(current==chat)ImportFiles(picked.Select(item=>item.Path.LocalPath));
    }
    void ImportFiles(IEnumerable<string> paths)
    {
        if(!CanEditDraft)return;
        foreach(var path in paths)
        {
            try
            {
                if(!File.Exists(path))continue;
                if(pendingImages.Count+pendingFiles.Count>=10){status.Text="Attach up to 10 files per message.";break;}
                if(AttachedFiles.IsImage(path))
                {
                    if(new FileInfo(path).Length>20*1024*1024){status.Text="Choose an image smaller than 20 MB.";continue;}
                    var copy=store.ImportImage(path,current.Id);pendingImages.Add(copy);AddDraftAttachment(ImagePreviewLink(copy,100,70),()=>pendingImages.Remove(copy));
                }
                else
                {
                    if(current.Mode=="chat"&&!AttachedFiles.IsText(path)){status.Text="Use Project agent mode for this file type. Chat supports images, text and source code.";continue;}
                    var file=AttachedFiles.Import(path,store.Root,current.Id);pendingFiles.Add(file);AddDraftAttachment(AttachmentLink(file),()=>pendingFiles.Remove(file));
                }
                status.Text="File attached. Enter a request or add more files.";
            }
            catch(Exception e){status.Text="Could not attach file: "+e.Message;}
        }
        CaptureDraft();
        PersistWorkspace(includeChats:true);
    }
    async Task DeleteChat()
    {
        if(busy)return;var dialog=new Window{Title="Delete chat?",Width=410,Height=210,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#202420")};var layout=new StackPanel{Margin=new Thickness(24),Spacing=18};layout.Children.Add(new TextBlock{Text="Delete this chat history?\nProject files will remain on disk.",TextWrapping=TextWrapping.Wrap,FontSize=16});var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};row.Children.Add(Button("Cancel",(_,_)=>dialog.Close(false)));row.Children.Add(Button("Delete chat",(_,_)=>dialog.Close(true)));layout.Children.Add(row);dialog.Content=layout;if(await dialog.ShowDialog<bool>(this)){agent.Dispose();store.Delete(current);Show(store.Chats.OrderByDescending(x=>x.Updated).FirstOrDefault()??NewConversation());RefreshHistory();}
    }
}

