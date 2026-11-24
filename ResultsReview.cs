using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocalDesk;

public sealed partial class MainWindow
{
    static string CheckOutcome(ChatMessage message)
    {
        var states = new List<string>();
        if (message.BuildCheck is { } build) states.Add(build.ExitCode == 0 ? "Build passed" : $"Build failed (exit {build.ExitCode})");
        if (message.Focused is { } focused) states.Add(focused.Passed ? "Focused check passed" : "Focused check failed");
        return states.Count == 0 ? "No independent build or focused check recorded" : string.Join(" · ", states);
    }

    async Task ReviewResults(ChatMessage message)
    {
        if (busy) return;
        var dialog = UtilityWindow("Review results", 890, 680);
        var panel = new StackPanel { Margin = new Thickness(23), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message.Successful ? "Run completed" : "Run needs attention", FontWeight = FontWeight.SemiBold, FontSize = 23,
            Foreground = Brush(message.Successful ? "#A8E6BC" : "#E8C585") });
        if (message.ProjectUsed is { } workspace) panel.Children.Add(EvidenceText(workspace));
        if (!string.IsNullOrWhiteSpace(message.Summary)) panel.Children.Add(RenderMarkdown(message.Summary, 14));
        if (message.ProjectUsed != null)
        {
            panel.Children.Add(new TextBlock { Text = "Independent checks", FontSize = 18, FontWeight = FontWeight.SemiBold });
            if(message.AcceptanceCriteria.Length>0)
            {
                panel.Children.Add(EvidenceText("Requested acceptance criteria"));
                panel.Children.Add(ChatText(message.AcceptanceCriteria,13));
            }
            panel.Children.Add(EvidenceText(CheckOutcome(message)));
            if (message.BuildCheck is { } build && File.Exists(build.LogPath)) panel.Children.Add(EvidenceLink("View build log", build.LogPath, dialog));
            if (message.Focused is { } focused)
            {
                panel.Children.Add(EvidenceText(focused.Summary));
                if (focused.LogPath is { } log && File.Exists(log)) panel.Children.Add(EvidenceLink("View focused check log", log, dialog));
            }
            if (message.PauseReason is { } paused) panel.Children.Add(EvidenceText(paused));
            if (message.EvidenceNote is { } scope) panel.Children.Add(EvidenceText(scope));
            if (message.RepairAttempts > 0) panel.Children.Add(EvidenceText($"Repair attempts · {message.RepairAttempts} / {Verification.MaxRepairs}"));
            panel.Children.Add(new TextBlock { Text = $"Verified changes ({message.VerifiedFiles.Count})", FontSize = 18, FontWeight = FontWeight.SemiBold });
            foreach (var file in message.VerifiedFiles)
            {
                var row = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
                var relative = Path.GetRelativePath(message.ProjectUsed, file.Path);
                var name = relative.StartsWith("..", StringComparison.Ordinal) ? file.Path : relative;
                string DescribeChange() => (file.Undone ? "Undone · " : "") + file.Change + " · " + name;
                var label = EvidenceText(DescribeChange());
                row.Children.Add(label);
                var actions = new WrapPanel { Orientation = Orientation.Horizontal };
                if (file.BeforeSnapshot != null || file.AfterSnapshot != null)
                    actions.Children.Add(Button("Open diff", async (_, _) => { await ReviewChange(file, dialog); label.Text = DescribeChange(); }));
                else if (file.Diff != null)
                    row.Children.Add(new Expander { Header = "Diff preview", Content = ChatText(file.Diff, 12) });
                if (File.Exists(file.Path))
                {
                    actions.Children.Add(EvidenceLink("Preview file", file.Path, dialog));
                    if (AttachedFiles.HasTextExtension(file.Path)) actions.Children.Add(Button("Open in editor", (_, _) => OpenSourceEditor(file.Path)));
                }
                foreach (var button in actions.Children) button.Margin = new Thickness(0, 0, 7, 0);
                if (actions.Children.Count > 0) row.Children.Add(actions);
                panel.Children.Add(new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Background = Brush("#252E38"), Child = row });
            }
        }

        var outputs = message.Artifacts.Select(file => file.Path).Concat(message.BuildCheck?.Executables ?? [])
            .Where(File.Exists).Except(message.VerifiedFiles.Select(file => file.Path), StringComparer.OrdinalIgnoreCase).ToList();
        if (outputs.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Outputs and referenced files", FontSize = 18, FontWeight = FontWeight.SemiBold });
            foreach (var path in outputs) panel.Children.Add(EvidenceLink(Path.GetFileName(path), path, dialog));
        }
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(this);
    }

    void OpenSourceEditor(string path)
    {
        if (!File.Exists(path)) { status.Text = "The file is no longer available."; return; }
        var editor = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe")
        }.FirstOrDefault(File.Exists) ?? Path.Combine(Environment.SystemDirectory, "notepad.exe");
        try { Process.Start(new ProcessStartInfo(editor) { ArgumentList = { path }, UseShellExecute = false }); }
        catch (Exception error) { status.Text = "Could not open the editor. " + error.Message; }
    }
}
