using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LocalDesk;

public sealed partial class MainWindow
{
    const string StarterModel = "qwen3:4b";
    const string StarterDownload = "Qwen3 4B · about 2.5 GB";
    static readonly Uri OllamaDownload = new("https://ollama.com/download/windows");
    readonly TextBlock setupStatus = new() { TextWrapping = TextWrapping.Wrap };
    IReadOnlyList<LocalModel> installedModels = [];
    bool ollamaReady;

    void RenderWelcome()
    {
        if (current.Messages.Count != 0) return;
        messages.Children.Clear();
        var welcome = new StackPanel { Spacing = 14, Margin = new Thickness(8, 62, 0, 30) };
        welcome.Children.Add(new TextBlock
        {
            Text = "What would you like to work on?", FontSize = 34,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        if (setupStatus.Parent is Panel previous) previous.Children.Remove(setupStatus);
        welcome.Children.Add(setupStatus);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Open a project…", async (_, _) => await OpenProjectFolder()));
        actions.Children.Add(Button("Model setup…", async (_, _) => await ShowModelSetup()));
        actions.Children.Add(Button("Try a sample project", (_, _) => OpenSample()));
        welcome.Children.Add(actions);
        messages.Children.Add(welcome);
        UpdateSetupStatus();
    }

    void UpdateSetupStatus()
    {
        if (ollama.Compatible)
        {
            setupStatus.Text = ollamaReady ? $"Text chat uses {ollama.Http.BaseAddress!.Authority}." : "Open Model connections to check the server.";
            return;
        }
        setupStatus.Text = !ollamaReady ? "Connect to Ollama to start a local chat."
            : installedModels.Count == 0 ? "Ollama is connected. Choose a model to download."
            : "Ask a question, drop in a file, or open a folder for project work.";
    }

    async Task ShowModelSetup()
    {
        if (busy) return;
        if (ollama.Compatible) { await ConfigureConnection(); return; }
        await RefreshModels();
        var dialog = UtilityWindow("Model setup", 600, 385);
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "Get a local model ready", FontSize = 24, FontWeight = FontWeight.SemiBold });
        content.Children.Add(EvidenceText(ollamaReady
            ? installedModels.Count > 0 ? $"{installedModels.Count} installed models available." : "Ollama is connected. Choose a model to download."
            : "Ollama is unavailable. Install or open Ollama, then check the connection."));
        content.Children.Add(EvidenceText($"Starter model: {StarterDownload}. It supports chat and project tools."));
        var actions = new WrapPanel();
        if (!ollamaReady)
        {
            actions.Children.Add(Button("Get Ollama for Windows", async (_, _) => await Launcher.LaunchUriAsync(OllamaDownload)));
            actions.Children.Add(Button("Check again", (_, _) => dialog.Close("retry")));
        }
        else actions.Children.Add(Button(installedModels.Count > 0 ? "Choose installed model" : "Choose model to download",
            (_, _) => dialog.Close("models")));
        actions.Children.Add(Button("Close", (_, _) => dialog.Close("close")));
        content.Children.Add(actions);
        dialog.Content = content;
        var choice = await dialog.ShowDialog<string?>(this);
        if (choice == "retry") await ShowModelSetup();
        else if (choice == "models") await ManageModels(installedModels.Count == 0 ? StarterModel : null);
    }

    async Task OpenProjectFolder()
    {
        if (busy || sendingRequest) return;
        if (await PickProjectPath("") is not { } folder) return;
        if (WorkspaceScope.IsBroad(folder, preferences.Folder))
        {
            status.Text = "Choose a specific project folder inside this location.";
            return;
        }
        OpenSavedProject(Projects.Remember(folder));
    }

    static string FolderText(string? text) => Environment.ExpandEnvironmentVariables((text ?? "").Trim().Trim('"'));

    async Task<string?> PickProjectPath(string initial)
    {
        var dialog = UtilityWindow("Open a project folder", 580, 220);
        var field = new TextBox { Text = initial, PlaceholderText = "Paste a folder path and press Enter" };
        var error = new TextBlock();
        void Open()
        {
            var path = FolderText(field.Text);
            if (Path.IsPathFullyQualified(path) && Directory.Exists(path)) dialog.Close(Path.GetFullPath(path));
            else error.Text = "Enter the full path to an existing folder.";
        }
        field.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Open(); } };
        var actions = new WrapPanel();
        actions.Children.Add(Button("Open folder", (_, _) => Open()));
        actions.Children.Add(Button("Browse…", async (_, _) =>
        {
            var picked = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open a project folder" });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) dialog.Close(path);
        }));
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { field, error, actions } };
        dialog.Opened += (_, _) => { field.Focus(); field.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    void OpenSample()
    {
        if (busy || sendingRequest) return;
        var folder = Path.Combine(store.Root, "samples", "first-project");
        Directory.CreateDirectory(folder);
        var readme = Path.Combine(folder, "README.md");
        if (!File.Exists(readme))
            File.WriteAllText(readme, "# Sample project\n\nA small workspace for trying LocalDesk.\n");
        OpenSavedProject(Projects.Remember(folder));
        input.Text = "Add a line to README.md that says LocalDesk sample completed. Show me the change.";
        current.AcceptanceCriteria = "README.md contains the line LocalDesk sample completed.";
        current.AcceptanceCheck = new FocusedCheck("Text present", "README.md", "LocalDesk sample completed.");
        Save();
        input.Focus();
    }
}
