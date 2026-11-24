using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocalDesk;

public sealed partial class MainWindow
{
    Button ActivityToggle = null!;
    Control ActivityPanel = null!;
    Control ActivitySplitter = null!;
    double lastActivityWidth = 300;

    void ToggleActivity(bool? visible = null)
    {
        if (workspace.ColumnDefinitions.Count != 3 || ActivityPanel == null) return;
        bool show = visible ?? !ActivityPanel.IsVisible;
        if (!show && ActivityPanel.Bounds.Width >= 220) lastActivityWidth = ActivityPanel.Bounds.Width;
        ActivityPanel.IsVisible = ActivitySplitter.IsVisible = show;
        workspace.ColumnDefinitions[1].Width = new GridLength(show ? 5 : 0);
        workspace.ColumnDefinitions[2].MinWidth = show ? 220 : 0;
        workspace.ColumnDefinitions[2].Width = new GridLength(show ? lastActivityWidth : 0);
        ActivityToggle.Content = show ? "Hide activity" : "Show activity";
        preferences.ShowActivity = show;
        if (visible == null) { preferences.Save(store.Root); PersistWorkspace(); }
    }

    void SaveActivityWidth()
    {
        if (!ActivityPanel.IsVisible) return;
        var width = workspace.ColumnDefinitions[2].Width;
        lastActivityWidth = Math.Clamp(width.IsAbsolute ? width.Value : ActivityPanel.Bounds.Width, 220, 700);
        PersistWorkspace();
    }

    Button WorkspaceNav(string label, EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var button = Button(label, onClick, "#181F27");
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(12, 9);
        button.FontSize = 13;
        return button;
    }

    async Task ShowCommands()
    {
        if (busy) return;
        var dialog = UtilityWindow("Quick actions", 600, 420);
        var actions = new List<(string Label, Func<Task> Run)>
        {
            ("New chat", () => { NewChat(); return Task.CompletedTask; }),
            ("Browse chat history by date", BrowseHistory),
            ("Export this chat as Markdown", () => ExportChat(current, this)),
            ("Attach files", Attach),
            ("Branch current chat", () => BranchChat()),
            ("Apply branch changes", ApplyBranchChanges),
            ("Pin or unpin current chat", () => { TogglePin(); return Task.CompletedTask; }),
            ("Projects", ManageProjects),
            ("Run setup and acceptance checks", ConfigureRun),
            ("Search project files and notes", ShowProjectSearch),
            ("Search the web and add citations", ShowWebSearch),
            ("Model library", ManageModels),
            ("Model storage", () => ManageModelStorage(this)),
            ("Compare models", CompareModels),
            ("Benchmark GPU context profiles", ShowGpuBenchmark),
            ("Run queue", () => { ShowQueue(); return Task.CompletedTask; }),
            ("Open the mini-window", () => { ShowMiniWindow(); return Task.CompletedTask; }),
            ("Settings", Preferences),
            ("Compact context", CompactContext),
            ("Context breakdown and compaction preview", ShowContextInspector),
            ("Live memory", () => { ShowMemory(); return Task.CompletedTask; }),
            ("Show or hide activity", () => { ToggleActivity(); return Task.CompletedTask; })
        };
        if (current.Messages.LastOrDefault(message => message.Summary != null) is { } result)
            actions.Add(("Review latest results", () => ReviewResults(result)));
        foreach (var chat in ChatCatalog.Search(store.Chats.Where(c => !session.Pending.Contains(c.Id)), null))
            actions.Add(("Chat · " + chat.Title, () => { Show(chat); RefreshHistory(); return Task.CompletedTask; }));
        foreach (var entry in Projects.Items.Where(p => Directory.Exists(p.Folder)))
            actions.Add(("Project · " + entry, () =>
            {
                var chat = NewConversation();
                chat.Mode = "agent";
                chat.Project = entry.Folder;
                chat.BuildProject = entry.BuildTarget;
                Show(chat);
                Save();
                RefreshHistory();
                return Task.CompletedTask;
            }));
        foreach (var choice in availableModels)
            actions.Add(("Switch model · " + choice.Name, async () =>
            {
                changing = true;
                model.SelectedItem = choice;
                changing = false;
                await SwitchModel(choice.Id);
            }));
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(18) };
        var search = new TextBox { PlaceholderText = "Find actions, chats, projects or models…", FontSize = 17, Margin = new Thickness(0, 0, 0, 12) };
        var list = new ListBox { Background = Brush("#191F28") };
        layout.Children.Add(search);
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        (string Label, Func<Task> Run)[] matches = [];
        Func<Task>? chosen = null;
        void Filter()
        {
            var term = search.Text?.Trim() ?? "";
            matches = actions.Where(item => item.Label.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = matches.Select(item => item.Label).ToArray();
            list.SelectedIndex = matches.Length > 0 ? 0 : -1;
        }
        void Select()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= matches.Length) return;
            chosen = matches[list.SelectedIndex].Run;
            dialog.Close();
        }
        Filter();
        search.TextChanged += (_, _) => Filter();
        dialog.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: dialog.Close(); e.Handled = true; break;
                case Key.Enter: Select(); e.Handled = true; break;
                case Key.Down when search.IsFocused && matches.Length > 0:
                    list.SelectedIndex = Math.Min(matches.Length - 1, list.SelectedIndex + 1); e.Handled = true; break;
                case Key.Up when search.IsFocused && matches.Length > 0:
                    list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; break;
            }
        };
        list.DoubleTapped += (_, _) => Select();
        dialog.Opened += (_, _) => search.Focus();
        dialog.Content = layout;
        await dialog.ShowDialog(this);
        if (chosen != null) await chosen();
    }
}
