using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LocalDesk;

public sealed partial class MainWindow
{
    async Task ManageModelStorage(Window owner)
    {
        if (busy) return;
        var dialog = UtilityWindow("Model storage", 720, 480);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 13 };
        panel.Children.Add(new TextBlock { Text = "Model storage", FontSize = 25, FontWeight = FontWeight.SemiBold });
        var currentFolder = new SelectableTextBlock { Text = ModelStorage.CurrentFolder, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(EvidenceText("Current Ollama library"));
        panel.Children.Add(new Border { Child = currentFolder, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = Brush("#202932") });
        panel.Children.Add(EvidenceText("Choose an empty folder on a local drive. LocalDesk verifies the copied library before switching Ollama to it. Your LM Studio library stays separate."));
        var destination = EvidenceText("No destination chosen.");
        string? target = null;
        var pick = Button("Choose destination…", async (_, _) =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose an empty folder for the Ollama model library", AllowMultiple = false
            });
            if (folders.Count == 0) return;
            target = folders[0].Path.LocalPath;
            destination.Text = target;
        });
        panel.Children.Add(pick);
        panel.Children.Add(destination);
        var keep = new CheckBox { Content = "Keep a backup in the original folder" };
        panel.Children.Add(keep);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, IsVisible = false };
        panel.Children.Add(progress);
        var note = EvidenceText("");
        panel.Children.Add(note);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var run = Button("Move library", null, "#B8D6CF");
        run.Foreground = Brush("#14251E");
        run.Margin = new Thickness(0, 0, 8, 0);
        var cancel = Button("Cancel move", null);
        cancel.IsEnabled = false;
        actions.Children.Add(run);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        CancellationTokenSource? operation = null;
        cancel.Click += (_, _) => operation?.Cancel();
        run.Click += async (_, _) =>
        {
            if (operation != null) return;
            if (target == null) { note.Text = "Choose an empty destination first."; return; }
            using var move = new CancellationTokenSource();
            operation = move;
            run.IsEnabled = pick.IsEnabled = keep.IsEnabled = false;
            cancel.IsEnabled = true;
            progress.Value = 0;
            progress.IsVisible = true;
            note.Text = "Checking the current model library…";
            agent.Dispose();
            loadedModel = null;
            try
            {
                await ollama.StopOwnedServer(move.Token);
                var reporter = new Progress<ModelStorage.TransferProgress>(value =>
                {
                    if (operation != move) return;
                    progress.Value = value.Total == 0 ? 0 : 100d * value.Done / value.Total;
                    note.Text = value.Total > 0
                        ? $"{value.Text}\n{value.Done / 1073741824d:F1} / {value.Total / 1073741824d:F1} GiB"
                        : value.Text;
                });
                var result = await ModelStorage.Move(ModelStorage.CurrentFolder, target, keep.IsChecked == true,
                    ModelStorage.RequireOffline, ModelStorage.VerifyWithOllama, ModelStorage.Activate, reporter, move.Token);
                currentFolder.Text = ModelStorage.CurrentFolder;
                status.Text = "Model library moved. Models will load on demand.";
                connection.Text = "●  Ready · model on demand";
                await RefreshModels();
                note.Text = $"Switched to the new library. Ollama verified {result.Models} model tags." +
                    (keep.IsChecked == true ? " The original files were kept as a backup." :
                    result.OldFilesRetained > 0 ? $" {result.OldFilesRetained} originals could not be removed safely." : "");
                target = null;
                destination.Text = "No destination chosen.";
                progress.Value = 100;
            }
            catch (OperationCanceledException) { note.Text = "The move was cancelled before switching model folders."; }
            catch (Exception error) { note.Text = error.Message; }
            finally
            {
                operation = null;
                run.IsEnabled = pick.IsEnabled = keep.IsEnabled = true;
                cancel.IsEnabled = false;
            }
        };
        dialog.Closing += (_, args) =>
        {
            if (operation == null) return;
            args.Cancel = true;
            operation.Cancel();
        };
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(owner);
    }
}
