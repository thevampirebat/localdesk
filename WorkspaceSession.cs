using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace LocalDesk;

public sealed class WorkspaceSession
{
    public string? ChatId { get; set; }
    public double ActivityWidth { get; set; } = 300;
    public List<string> Pending { get; set; } = [];
    public string? Running { get; set; }
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }
    public bool MiniTopmost { get; set; } = true;

    public static WorkspaceSession Load(string root)
    {
        try { return JsonSerializer.Deserialize<WorkspaceSession>(File.ReadAllText(Path.Combine(root, "workspace.json"))) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string root)
    {
        var path = Path.Combine(root, "workspace.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

public sealed partial class MainWindow
{
    WorkspaceSession session = null!;
    readonly DispatcherTimer draftTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    Button queueMenu = null!;
    Button queueDraft = null!;
    Window? queueWindow;
    Action? refreshQueue;
    bool queueEnabled, drainingQueue, requestStreaming, closingWorkspace;

    void InitializeSession()
    {
        session = WorkspaceSession.Load(store.Root);
        lastActivityWidth = double.IsFinite(session.ActivityWidth) ? Math.Clamp(session.ActivityWidth, 220, 700) : 300;
        input.TextChanged += (_, _) =>
        {
            if (changing) return;
            CaptureDraft();
            draftTimer.Stop();
            draftTimer.Start();
        };
        draftTimer.Tick += (_, _) => { draftTimer.Stop(); PersistWorkspace(includeChats: true); };
    }

    void RestoreSession()
    {
        session.Pending.RemoveAll(id => id == session.Running || !store.Chats.Any(chat => chat.Id == id));
        var interrupted = session.Running != null;
        session.Running = null;
        var chat = store.Chats.FirstOrDefault(c => c.Id == session.ChatId) ?? store.Chats.OrderByDescending(c => c.Updated).FirstOrDefault();
        Show(chat ?? NewConversation());
        RefreshHistory();
        RefreshQueue();
        if (interrupted) status.Text = "A queued run was interrupted. Open its chat to review or recover it.";
    }

    void PersistWorkspace(bool includeChats = false)
    {
        if (!store.Chats.Contains(current)) return;
        session.ChatId = current.Id;
        session.ActivityWidth = lastActivityWidth;
        try
        {
            if (includeChats) store.Save();
            session.Save(store.Root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { status.Text = "Could not save workspace state. " + error.Message; }
    }

    void CaptureDraft()
    {
        current.Draft = input.Text ?? "";
        current.DraftImages = pendingImages.ToList();
        current.DraftFiles = pendingFiles.ToList();
        UpdateQueueButton();
    }

    void RestoreDraft()
    {
        input.Text = current.Draft;
        pendingImages.AddRange(current.DraftImages.Where(File.Exists));
        pendingFiles.AddRange(current.DraftFiles);
        foreach (var path in pendingImages) AddDraftAttachment(ImagePreviewLink(path, 100, 70), () => pendingImages.Remove(path));
        foreach (var file in pendingFiles) AddDraftAttachment(AttachmentLink(file), () => pendingFiles.Remove(file));
        UpdateQueueButton();
    }

    void AddDraftAttachment(Control preview, Action remove)
    {
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(2, 2, 10, 6), MaxWidth = 180 };
        panel.Children.Add(preview);
        var button = Button("Remove", (_, _) =>
        {
            remove();
            attachments.Children.Remove(panel);
            CaptureDraft();
            PersistWorkspace(includeChats: true);
        });
        button.FontSize = 10;
        button.Padding = new Thickness(5, 2);
        panel.Children.Add(button);
        attachments.Children.Add(panel);
    }

    bool HasDraft => !string.IsNullOrWhiteSpace(input.Text) || pendingImages.Count + pendingFiles.Count > 0;
    bool CanEditDraft => !busy || requestStreaming;

    void UpdateQueueButton()
    {
        if (queueDraft != null) queueDraft.IsEnabled = CanEditDraft && HasDraft;
    }

    void RefreshQueue()
    {
        queueMenu.Content = session.Pending.Count == 0 ? "Run queue" : $"Run queue ({session.Pending.Count})";
        refreshQueue?.Invoke();
    }

    void EnqueueDraft()
    {
        if (!CanEditDraft || !HasDraft) return;
        if (!busy && current.Mode == "agent") CommitProject();
        if (current.Mode == "agent" && !Directory.Exists(current.Project))
        {
            status.Text = "Choose an existing project folder before queuing a run.";
            return;
        }

        var queued = new Conversation
        {
            Title = string.IsNullOrWhiteSpace(input.Text) ? "Queued attachments" : input.Text.Trim().Split('\n')[0].Trim(),
            Model = current.Model, Mode = current.Mode, Project = current.Project, BuildProject = current.BuildProject,
            BranchFolder = current.BranchFolder, ParentChat = current.BranchFolder != null ? current.ParentChat : null,
            ContextTokens = current.ContextTokens, OutputTokens = current.OutputTokens, Thinking = current.Thinking,
            TimeLimitMinutes = current.TimeLimitMinutes, Draft = input.Text ?? "", DraftFiles = pendingFiles.ToList(),
            AcceptanceCriteria=current.AcceptanceCriteria,AcceptanceCheck=current.AcceptanceCheck,ReviewBeforeChanges=current.ReviewBeforeChanges
        };
        if (queued.Title.Length > 45) queued.Title = queued.Title[..42] + "…";
        try { queued.DraftImages.AddRange(pendingImages.Select(path => store.ImportImage(path, queued.Id))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { status.Text = "Could not copy the queued images. " + error.Message; return; }

        store.Chats.Add(queued);
        session.Pending.Add(queued.Id);
        pendingImages.Clear();
        pendingFiles.Clear();
        attachments.Children.Clear();
        input.Text = "";
        CaptureDraft();
        PersistWorkspace(includeChats: true);
        RefreshHistory();
        RefreshQueue();
        status.Text = queueEnabled ? "Draft added to the run queue." : "Draft queued. Open Run queue and choose Start queue when ready.";
        ScheduleQueue();
    }

    void ShowQueue()
    {
        if (queueWindow != null) { queueWindow.Activate(); return; }
        var dialog = UtilityWindow("Run queue", 680, 480);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20) };
        var state = EvidenceText("");
        state.Margin = new Thickness(0, 0, 0, 12);
        layout.Children.Add(state);
        var list = new ListBox();
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var toggle = Button("Start queue", (_, _) => { queueEnabled = !queueEnabled; RefreshQueue(); ScheduleQueue(); });
        var up = Button("Move up", (_, _) => Move(-1));
        var down = Button("Move down", (_, _) => Move(1));
        var cancel = Button("Remove from queue", (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            session.Pending.RemoveAt(list.SelectedIndex);
            PersistWorkspace();
            RefreshHistory();
            RefreshQueue();
        });
        foreach (var button in new[] { toggle, up, down, cancel }) { button.Margin = new Thickness(0, 0, 7, 0); actions.Children.Add(button); }
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        void Move(int delta)
        {
            var from = list.SelectedIndex;
            int to = from + delta;
            if (from < 0 || to < 0 || to >= session.Pending.Count) return;
            (session.Pending[from], session.Pending[to]) = (session.Pending[to], session.Pending[from]);
            PersistWorkspace();
            RefreshQueue();
            list.SelectedIndex = to;
        }
        void UpdateButtons()
        {
            up.IsEnabled = list.SelectedIndex > 0;
            cancel.IsEnabled = list.SelectedIndex >= 0;
            down.IsEnabled = cancel.IsEnabled && list.SelectedIndex < session.Pending.Count - 1;
        }
        refreshQueue = () =>
        {
            var selected = list.SelectedIndex;
            list.ItemsSource = session.Pending.Select(id => store.Chats.First(c => c.Id == id))
                .Select(c => $"{c.Title}  |  {LocalModel.FromTag(c.Model).Name}\n{(c.Mode == "agent" ? c.Project : "Chat")}").ToArray();
            list.SelectedIndex = Math.Min(selected, session.Pending.Count - 1);
            toggle.Content = queueEnabled ? "Pause queue" : "Start queue";
            var running = store.Chats.FirstOrDefault(c => c.Id == session.Running);
            state.Text = (running != null ? "Running · " + running.Title + "\n" : "") +
                (queueEnabled ? "Jobs run one at a time. An incomplete run pauses the queue." : "Queue paused. Removed jobs stay in history as drafts.");
            UpdateButtons();
        };
        list.SelectionChanged += (_, _) => UpdateButtons();
        dialog.Content = layout;
        dialog.Closed += (_, _) => { queueWindow = null; refreshQueue = null; };
        queueWindow = dialog;
        refreshQueue();
        dialog.Show(this);
    }

    void ScheduleQueue()
    {
        if (closingWorkspace || !queueEnabled || drainingQueue || busy || sendingRequest || session.Pending.Count == 0) return;
        Dispatcher.UIThread.Post(async () => await DrainQueue(), DispatcherPriority.Background);
    }

    async Task DrainQueue()
    {
        if (closingWorkspace || !queueEnabled || drainingQueue || busy || sendingRequest || session.Pending.Count == 0) return;
        drainingQueue = true;
        try
        {
            var id = session.Pending[0];
            var job = store.Chats.First(c => c.Id == id);
            session.Running = id;
            session.Pending.RemoveAt(0);
            Show(job);
            RefreshHistory();
            RefreshQueue();
            var previousCount = job.Messages.Count;
            await Send();
            if (job.Messages.Count == previousCount || job.Messages.LastOrDefault()?.Successful != true)
            {
                queueEnabled = false;
                if (job.Messages.Count == previousCount)
                    NotifyRun(job, null, "Queued run could not start", status.Text ?? "Open LocalDesk to review the draft.");
            }
        }
        catch (Exception error)
        {
            queueEnabled = false;
            status.Text = "Queue paused. " + error.Message;
        }
        finally
        {
            session.Running = null;
            PersistWorkspace();
            drainingQueue = false;
            RefreshQueue();
            ScheduleQueue();
        }
    }
}
