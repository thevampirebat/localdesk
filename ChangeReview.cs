using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Security.Cryptography;

namespace LocalDesk;

static class ChangeReview
{
    public static (string Before, string After) Diff(string before, string after)
    {
        var left = before.Replace("\r\n", "\n").Split('\n');
        var right = after.Replace("\r\n", "\n").Split('\n');
        int prefix = 0, suffix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix]) prefix++;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        int start = Math.Max(0, prefix - 4);
        int leftEnd = left.Length - suffix, rightEnd = right.Length - suffix;
        int changedRows = Math.Min(160, Math.Max(leftEnd - prefix, rightEnd - prefix));
        string Render(string[] lines, int end, char marker)
        {
            var rows = new List<string>();
            for (int i = start; i < prefix; i++) rows.Add($"  {i + 1,5}  {lines[i]}");
            for (int row = 0; row < changedRows; row++)
            {
                int i = prefix + row;
                rows.Add(i < end ? $"{marker} {i + 1,5}  {lines[i]}" : "");
            }
            if (end - prefix > changedRows) rows.Add("… more changed lines in the full snapshot");
            for (int i = end; i < Math.Min(end + 4, lines.Length); i++) rows.Add($"  {i + 1,5}  {lines[i]}");
            return string.Join("\n", rows);
        }
        return (Render(left, leftEnd, '-'), Render(right, rightEnd, '+'));
    }

    public static void Undo(VerifiedFile file)
    {
        if (file.Undone) throw new IOException("This change was already undone.");
        if (WorkspaceScope.HasReparsePoint(file.Path)) throw new IOException("The file or one of its folders is now a link.");
        var folder = Path.GetDirectoryName(file.Path)!;
        if (!Directory.Exists(folder)) throw new IOException("The parent folder is missing.");
        if (file.Change == "Deleted")
        {
            if (file.BeforeSnapshot == null) throw new IOException("No original snapshot is available.");
            if (File.Exists(file.Path) || Directory.Exists(file.Path)) throw new IOException("The path was used again after this run. Undo is blocked.");
            File.Copy(file.BeforeSnapshot, file.Path, overwrite: false);
        }
        else
        {
            if (file.AfterHash == null || file.AfterSnapshot == null) throw new IOException("No verified snapshot is available.");
            if (new FileInfo(file.Path).Length > ProjectEvidence.ReviewSizeLimit) throw new IOException("The file changed after this run.");
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Path)));
            if (!actualHash.Equals(file.AfterHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The file changed after this run. Undo is blocked.");
            if (file.Change == "Added")
            {
                // Keep the saved after snapshot even when removing an unchanged creation.
                if (!File.Exists(file.AfterSnapshot)) throw new IOException("The saved copy is missing.");
                File.Delete(file.Path);
            }
            else
            {
                if (file.BeforeSnapshot == null) throw new IOException("No original snapshot is available.");
                var temporary = Path.Combine(folder, ".localdesk-restore-" + Guid.NewGuid().ToString("N"));
                try
                {
                    File.Copy(file.BeforeSnapshot, temporary);
                    var checkedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Path)));
                    if (checkedHash != file.AfterHash) throw new IOException("The file changed during review.");
                    File.Move(temporary, file.Path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        file.Undone = true;
    }
}

public sealed partial class MainWindow
{
    string? ArchiveToolOutput(string text)
    {
        var chat = current;
        var folder = Path.Combine(store.Root, "tool-output", chat.Id);
        try
        {
            var path = ToolOutputArchive.Save(folder, text, out var created);
            if (created)
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(chat, current)) AddActivity("tool output", path); });
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    Task ViewToolOutput(string path) => PreviewFile(path, name: "Full tool output", fullToolOutput: true);

    Window UtilityWindow(string title, double width, double height) => new()
    {
        Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#1C232B")
    };

    async Task ReviewChange(VerifiedFile file, Window? owner = null)
    {
        if (busy) return;
        var dialog = UtilityWindow("Review file change", 1060, 690);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18) };
        layout.Children.Add(new TextBlock { Text = file.Change + " · " + file.Path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") };
        Grid.SetRow(columns, 1);
        layout.Children.Add(columns);
        string Snapshot(string? path, string missing)
        {
            if (path == null) return missing;
            try { return File.ReadAllText(path); }
            catch (IOException) { return "Snapshot unavailable"; }
            catch (UnauthorizedAccessException) { return "Snapshot unavailable"; }
        }
        string beforeText = Snapshot(file.BeforeSnapshot, file.Change == "Added" ? "" : "Snapshot unavailable");
        string afterText = Snapshot(file.AfterSnapshot, file.Change == "Deleted" ? "" : "Snapshot unavailable");
        var diff = ChangeReview.Diff(beforeText, afterText);
        var beforePane = new SelectableTextBlock { Text = diff.Before, FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(12) };
        var afterPane = new SelectableTextBlock { Text = diff.After, FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(12) };
        Control Side(string title, SelectableTextBlock pane)
        {
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = Brush("#252E38") };
            grid.Children.Add(new TextBlock { Text = title, Margin = new Thickness(12) });
            var scroll = new ScrollViewer { Content = pane };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);
            return grid;
        }
        columns.Children.Add(Side(file.Change == "Added" ? "Before · file absent" : "Before", beforePane));
        var after = Side(file.Change == "Deleted" ? "After · file absent" : "After", afterPane);
        Grid.SetColumn(after, 2);
        columns.Children.Add(after);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        var result = EvidenceText(file.Undone ? "This change was undone." : file.Diff ?? "Saved snapshots from the agent run.");
        result.MaxHeight = 105;
        footer.Children.Add(result);
        var undo = Button("Undo this file…", null);
        undo.IsEnabled = !file.Undone && (file.BeforeSnapshot != null || file.AfterSnapshot != null);
        undo.Click += async (_, _) =>
        {
            var confirm = UtilityWindow("Undo file change?", 500, 210);
            var box = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            box.Children.Add(EvidenceText("Restore " + Path.GetFileName(file.Path) + " to its state before this run? LocalDesk checks for newer changes first."));
            box.Children.Add(Button("Undo this file", (_, _) => confirm.Close(true)));
            box.Children.Add(Button("Cancel", (_, _) => confirm.Close(false)));
            confirm.Content = box;
            if (!await confirm.ShowDialog<bool>(dialog)) return;
            try
            {
                ChangeReview.Undo(file);
                agent.Dispose();
                current.AgentSession = null;
                current.BridgeBoundary = "";
                current.BridgeSummary = "";
                AddActivity("undo", "Restored " + file.Path);
                current.ContextSummary += "\nLocalDesk undid the earlier edit to " + file.Path + ". Read the current file before making further changes.";
                Save();
                result.Text = "Change undone.";
                undo.IsEnabled = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result.Text = error.Message; }
        };
        var full = new CheckBox { Content = "Show full snapshots" };
        full.IsCheckedChanged += (_, _) =>
        {
            beforePane.Text = full.IsChecked == true ? beforeText : diff.Before;
            afterPane.Text = full.IsChecked == true ? afterText : diff.After;
        };
        footer.Children.Add(full);
        footer.Children.Add(undo);
        dialog.Content = layout;
        await dialog.ShowDialog(owner ?? this);
    }
}
