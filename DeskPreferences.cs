using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace LocalDesk;

public sealed partial class MainWindow
{
    List<LocalModel> ModelChoices(string? preferred, string noPreference)
    {
        List<LocalModel> choices = [new("", noPreference, ""), .. installedModels];
        if (!string.IsNullOrEmpty(preferred) && choices.All(item => item.Id != preferred))
            choices.Add(LocalModel.FromTag(preferred));
        return choices;
    }

    async Task Preferences()
    {
        if (busy || sendingRequest) return;
        var dialog = UtilityWindow("LocalDesk settings", 650, 680);
        var theme = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = preferences.Theme };
        var textSize = new NumericUpDown { Minimum = 13, Maximum = 24, Value = ChatTextSize, FormatString = "0" };
        var activityVisible = new CheckBox { Content = "Show activity pane", IsChecked = preferences.ShowActivity };
        var agentDefault = new CheckBox { Content = "Start new chats in Project agent mode", IsChecked = preferences.StartInAgent };
        var startup = new CheckBox { Content = "Load a model at startup", IsChecked = preferences.LoadModelAtStartup };
        var alerts = new CheckBox { Content = "Show Windows notifications for background runs", IsChecked = preferences.RunNotifications };
        var tokenGuard = new CheckBox { Content = "Shorten repeated file results (Token Guard)", IsChecked = preferences.ReduceDuplicateReads };
        var folder = new TextBox { Text = preferences.Folder, PlaceholderText = "Default project folder" };
        var ram = new NumericUpDown { Minimum = 0, Maximum = 4096, Increment = 1, Value = preferences.ModelRamGiB, FormatString = "0.##" };
        var vram = new NumericUpDown { Minimum = 0, Maximum = 1024, Increment = 1, Value = preferences.ModelVramGiB, FormatString = "0.##" };
        var choices = ModelChoices(preferences.DefaultModel, "First installed model");
        var defaultModel = new ComboBox { ItemsSource = choices, SelectedItem = choices.First(item => item.Id == (preferences.DefaultModel ?? "")) };
        var note = EvidenceText("");
        var picker = Button("Browse…", async (_, _) =>
        {
            var picked = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Default project folder" });
            if (picked.Count > 0) folder.Text = picked[0].TryGetLocalPath();
        });
        var advanced = new StackPanel { Spacing = 8, Children = { tokenGuard, EvidenceText("Model RAM budget (GiB)"), ram,
            EvidenceText("Model VRAM budget (GiB)"), vram,
            EvidenceText("Use 0 to turn off a budget. LocalDesk checks Ollama’s reported allocation after loading and unloads an over-budget model before it can start the task. Loading can briefly use more memory.") } };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10, Children = {
            new TextBlock { Text = "Appearance", FontSize = 21 }, EvidenceText("Theme"), theme,
            EvidenceText("Chat text size"), textSize, activityVisible,
            new TextBlock { Text = "New chats", FontSize = 21 }, EvidenceText("Default model"), defaultModel,
            agentDefault, EvidenceText("Default project folder"), folder, picker, startup, alerts,
            new Expander { Header = "Advanced and memory budgets", Content = advanced }, note } };
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Button("Cancel", (_, _) => dialog.Close()));
        row.Children.Add(Button("Save", (_, _) =>
        {
            var path = folder.Text?.Trim() ?? "";
            if (agentDefault.IsChecked == true && (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)))
            {
                note.Text = "Choose an existing folder for new Project agent chats.";
                return;
            }
            preferences.Theme = theme.SelectedItem as string ?? "System";
            preferences.ChatTextSize = (int)(textSize.Value ?? 16);
            preferences.StartInAgent = agentDefault.IsChecked == true;
            preferences.LoadModelAtStartup = startup.IsChecked == true;
            preferences.RunNotifications = alerts.IsChecked == true;
            preferences.ReduceDuplicateReads = tokenGuard.IsChecked == true;
            preferences.WorkingFolder = path;
            preferences.DefaultModel = defaultModel.SelectedItem is LocalModel selected && selected.Id.Length > 0 ? selected.Id : null;
            preferences.ModelRamGiB = ram.Value ?? 0;
            preferences.ModelVramGiB = vram.Value ?? 0;
            ollama.MemoryBudget = new(preferences.ModelRamGiB, preferences.ModelVramGiB);
            loadedModel = null;
            ApplyAppearance();
            ToggleActivity(activityVisible.IsChecked == true);
            preferences.Save(store.Root);
            Show(current);
            dialog.Close();
            status.Text = "Settings saved.";
        }));
        panel.Children.Add(row);
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(this);
    }
}
