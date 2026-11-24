using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json;

namespace LocalDesk;

static class ChatBranching
{
    public static string Remap(string text, string original, string copy) =>
        text.Replace(original, copy, StringComparison.OrdinalIgnoreCase)
            .Replace(original.Replace('\\', '/'), copy.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static Conversation Copy(Conversation chat, int count, string? projectSource, string data, CancellationToken ct)
    {
        var branch = new Conversation
        {
            Title = chat.Title + " · branch", Model = chat.Model, ContextTokens = chat.ContextTokens,
            OutputTokens = chat.OutputTokens, Thinking = chat.Thinking, TimeLimitMinutes = chat.TimeLimitMinutes,
            Mode = projectSource == null ? "chat" : "agent", ParentChat = chat.Id
        };
        if (projectSource != null)
        {
            var destination = Path.Combine(data, "branches", branch.Id, Path.GetFileName(Path.TrimEndingDirectorySeparator(projectSource)));
            ModelComparison.CopyProject(projectSource, destination, ct);
            BranchApply.CaptureOrigin(projectSource, destination, ct);
            branch.Project = branch.BranchFolder = destination;
        }
        foreach (var message in chat.Messages.Take(count))
        {
            ct.ThrowIfCancellationRequested();
            var clone = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message))!;
            clone.Marker = Guid.NewGuid().ToString("N");
            // A branch is a new run. History is context, with its old operation links disabled.
            clone.Artifacts.Clear();
            clone.VerifiedFiles.Clear();
            clone.BuildCheck = null;
            clone.Focused = null;
            clone.RepairAttempts = 0;
            clone.ProjectUsed = null;
            clone.Diagnostics = null;
            clone.PauseReason = null;
            if (projectSource != null)
            {
                clone.Text = Remap(clone.Text, projectSource, branch.Project);
                if (clone.Summary != null) clone.Summary = Remap(clone.Summary, projectSource, branch.Project);
                clone.SummaryOffset = 0;
            }
            for (int i = 0; i < clone.Images.Count; i++)
            {
                var from = clone.Images[i];
                if (!File.Exists(from)) continue;
                var images = Path.Combine(data, "images", branch.Id);
                Directory.CreateDirectory(images);
                var to = Path.Combine(images, Guid.NewGuid().ToString("N") + Path.GetExtension(from));
                File.Copy(from, to);
                clone.Images[i] = to;
            }
            foreach (var file in clone.Files)
            {
                if (projectSource != null && WorkspaceScope.RelativeFile(projectSource, file.SourcePath) is { } relative)
                    file.SourcePath = Path.Combine(branch.Project, relative);
                else if (projectSource != null)
                    file.SourcePath = file.StoredPath;
                if (!File.Exists(file.StoredPath)) continue;
                var files = Path.Combine(data, "files", branch.Id);
                Directory.CreateDirectory(files);
                var to = Path.Combine(files, Guid.NewGuid().ToString("N") + Path.GetExtension(file.StoredPath));
                File.Copy(file.StoredPath, to);
                if (file.SourcePath == file.StoredPath) file.SourcePath = to;
                file.StoredPath = to;
            }
            branch.Messages.Add(clone);
        }
        return branch;
    }
}

public sealed partial class MainWindow
{
    async Task BranchChat(ChatMessage? through = null)
    {
        if (busy) return;
        CommitProject();
        var from = current;
        var count = through == null ? from.Messages.Count : from.Messages.IndexOf(through) + 1;
        if (count == 0) { status.Text = "Send a message before branching this chat."; return; }
        var root = through?.ProjectUsed ?? (from.Mode == "agent" ? from.Project : null);
        if (root != null && (!Directory.Exists(root) || WorkspaceScope.IsBroad(root, preferences.Folder)))
        {
            status.Text = "Choose a specific project source folder before branching an agent run.";
            return;
        }
        var dialog = UtilityWindow("Branch chat", 575, 345);
        var content = new StackPanel { Margin = new Thickness(23), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "Start a separate chat from this point", FontSize = 20, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(EvidenceText(root == null
            ? "The selected conversation history will be copied to the new chat."
            : "LocalDesk copies the current project files and conversation up to this point. Edits need approval and stay inside the copy. Agent commands and automatic builds are disabled in project branches."));
        if (root != null) content.Children.Add(EvidenceText("Copy from · " + root));
        var choose = new ComboBox { ItemsSource = availableModels.ToList(), SelectedItem = availableModels.FirstOrDefault(m => m.Id == from.Model) };
        content.Children.Add(choose);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        actions.Children.Add(Button("Cancel", (_, _) => dialog.Close(false)));
        actions.Children.Add(Button("Create branch", (_, _) => dialog.Close(true)));
        content.Children.Add(actions);
        dialog.Content = content;
        if (!await dialog.ShowDialog<bool>(this)) return;
        using var copy = new CancellationTokenSource();
        cancellation = copy;
        SetBusy(true);
        status.Text = "Creating branch…";
        try
        {
            var branch = await Task.Run(() => ChatBranching.Copy(from, count, root, store.Root, copy.Token), copy.Token);
            store.Chats.Add(branch);
            Show(branch);
            if (choose.SelectedItem is LocalModel picked && picked.Id != branch.Model)
            {
                branch.Model = picked.Id;
                ApplyProfile(picked.Id);
                Show(branch);
            }
            chatSearch.Text = "";
            RefreshHistory();
            Save();
            status.Text = root == null ? "Branch created." : "Branch created in " + branch.BranchFolder;
        }
        catch (OperationCanceledException) { status.Text = "Branch creation cancelled."; }
        catch (Exception e) { status.Text = "Could not create branch: " + e.Message; }
        finally { cancellation = null; SetBusy(false); }
    }
}
