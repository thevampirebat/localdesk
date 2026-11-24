using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LocalDesk;

public sealed partial class MainWindow
{
    async Task CompareModels()
    {
        if (busy) return;
        CommitProject();
        var dialog = UtilityWindow("Compare local models", 780, 820);
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 11 };
        panel.Children.Add(new TextBlock { Text = "Compare local models", FontSize = 22 });
        var source = current.Project;
        string? build = current.BuildProject is { } target && IsBuildProject(target) ? WorkspaceScope.RelativeFile(source, target) : null;
        panel.Children.Add(EvidenceText("Use a scoped edit in one source file. Each model gets the same task and file excerpt on its own project copy. LocalDesk applies only edits that match the expected result, then runs the selected build check."));
        panel.Children.Add(EvidenceText("Source folder · " + source));
        panel.Children.Add(EvidenceText("Build target · " + (build ?? "None. Set one with Build check… to compare builds.")));
        var sourceFile = EvidenceText("No source file selected.");
        string? relativeFile = null;
        var picker = Button("Choose source file…", async (_, _) =>
        {
            var startingFolder = await dialog.StorageProvider.TryGetFolderFromPathAsync(source);
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a file inside the source folder", SuggestedStartLocation = startingFolder
            });
            if (files.Count == 0) return;
            var path = files[0].Path.LocalPath;
            var relative = WorkspaceScope.RelativeFile(source, path);
            if (relative == null || !ModelComparison.SourceFile(path) || WorkspaceScope.HasReparsePoint(path) ||
                relative.Split(Path.DirectorySeparatorChar).Any(WorkspaceScope.Excluded.Contains))
            {
                sourceFile.Text = "Choose a regular source file inside this project.";
                relativeFile = null;
                return;
            }
            relativeFile = relative;
            sourceFile.Text = relative;
        });
        panel.Children.Add(picker);
        panel.Children.Add(sourceFile);
        var task = new TextBox { PlaceholderText = "Task given to each model", AcceptsReturn = true, MaxHeight = 100, TextWrapping = TextWrapping.Wrap };
        var oldText = new TextBox { PlaceholderText = "Exact original text for the comparison", AcceptsReturn = true, MaxHeight = 90, TextWrapping = TextWrapping.Wrap };
        var newText = new TextBox { PlaceholderText = "Expected replacement (leave empty to test a removal)", AcceptsReturn = true, MaxHeight = 90, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(task);
        panel.Children.Add(oldText);
        panel.Children.Add(newText);
        var models = new WrapPanel { Orientation = Orientation.Horizontal };
        var options = availableModels.Select(model => (Model: model, Check: new CheckBox
        {
            Content = model.Name, IsChecked = model.Id == current.Model, Margin = new Thickness(0, 0, 12, 0)
        })).ToList();
        foreach (var option in options) models.Children.Add(option.Check);
        panel.Children.Add(models);
        var limit = new NumericUpDown { Minimum = 1, Maximum = 30, Value = 5, Increment = 1, FormatString = "0" };
        panel.Children.Add(EvidenceText("Minutes per model, including loading and build"));
        panel.Children.Add(limit);
        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        var run = Button("Run comparison", null);
        var results = new StackPanel { Spacing = 8 };
        var note = EvidenceText("");
        CancellationTokenSource? work = null;
        var cancel = Button("Stop comparison", (_, _) => work?.Cancel());
        cancel.IsEnabled = false;
        controls.Children.Add(run);
        controls.Children.Add(cancel);
        panel.Children.Add(controls);
        panel.Children.Add(note);
        panel.Children.Add(results);
        run.Click += async (_, _) =>
        {
            if (work != null) return;
            if (!Directory.Exists(source) || WorkspaceScope.IsBroad(source, preferences.Folder))
            {
                note.Text = "Choose a specific source folder before comparing models.";
                return;
            }
            var selection = options.Where(option => option.Check.IsChecked == true).Select(option => option.Model).ToList();
            var prompt = task.Text?.Trim();
            var before = oldText.Text ?? "";
            var expected = newText.Text ?? "";
            if (relativeFile == null || selection.Count < 2 || string.IsNullOrEmpty(prompt) || before.Length == 0)
            {
                note.Text = "Choose a source file, describe the task, enter the original text and select at least two models.";
                return;
            }
            if (before.Length > 2000 || expected.Length > 6000 || prompt.Length > 3000)
            {
                note.Text = "Keep the task and expected change small enough for a single source excerpt.";
                return;
            }
            results.Children.Clear();
            var root = Path.Combine(store.Root, "comparisons", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            using var execution = new CancellationTokenSource();
            work = execution;
            var locked = new Control[] { run, picker, task, oldText, newText, limit, models };
            foreach (var control in locked) control.IsEnabled = false;
            cancel.IsEnabled = true;
            loadedModel = null;
            SetBusy(true);
            try
            {
                await ModelComparison.Run(ollama, source, relativeFile, build, prompt, before, expected, selection, root,
                    (int)(limit.Value ?? 5), message => Dispatcher.UIThread.Post(() => note.Text = message),
                    row => Dispatcher.UIThread.Post(() =>
                    {
                        var verdict = row.CorrectEdit ? "Exact edit" : "Edit did not pass";
                        var buildLabel = row.BuildExit.HasValue ? row.BuildExit == 0 ? "Build passed" : $"Build failed ({row.BuildExit})" : "Build not run";
                        results.Children.Add(EvidenceText($"{row.Model} · {verdict} · {buildLabel}\nLoad {row.LoadSeconds:F1}s · Edit {row.EditSeconds:F1}s · Build {row.BuildSeconds:F1}s"));
                        if (row.Note != null) results.Children.Add(EvidenceText(row.Note));
                        if (row.BuildLog != null) results.Children.Add(EvidenceLink("View build log", row.BuildLog, dialog));
                    }), execution.Token);
                note.Text = "Comparison finished. Copies and results are saved in LocalDesk data.";
                var report = Path.Combine(root, "results.json");
                if (File.Exists(report)) results.Children.Add(EvidenceLink("View comparison report", report, dialog));
            }
            catch (OperationCanceledException) { note.Text = "Comparison stopped. Completed results were saved."; }
            catch (Exception error) { note.Text = error.Message; }
            finally
            {
                work = null;
                foreach (var control in locked) control.IsEnabled = true;
                cancel.IsEnabled = false;
                SetBusy(false);
            }
        };
        dialog.Closing += (_, args) =>
        {
            if (work == null) return;
            args.Cancel = true;
            work.Cancel();
        };
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(this);
    }
}
