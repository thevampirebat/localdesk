using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LocalDesk;

public sealed partial class MainWindow
{
    async Task BrowseHistory()
    {
        if (busy) return;
        var dialog = UtilityWindow("Chat history", 750, 630);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(20) };
        var search = new TextBox { PlaceholderText = "Search chats and files…", Margin = new Thickness(0, 0, 0, 10) };
        var results = new StackPanel { Spacing = 6 };
        var scroll = new ScrollViewer { Content = results };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(search);
        layout.Children.Add(scroll);
        void Refresh()
        {
            results.Children.Clear();
            var matches = ChatCatalog.Search(store.Chats.Where(c => !session.Pending.Contains(c.Id)), search.Text);
            foreach (var group in matches.GroupBy(c => c.Pinned ? "Pinned" : c.Updated.Date == DateTime.Today ? "Today" :
                c.Updated.Date >= DateTime.Today.AddDays(-6) ? "Previous 6 days" : c.Updated.ToString("MMMM yyyy")))
            {
                results.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 9, 0, 1) });
                foreach (var chat in group)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    var open = WorkspaceNav(chat.Title, (_, _) => { Show(chat); RefreshHistory(); dialog.Close(); });
                    var export = Button("Export…", async (_, _) => await ExportChat(chat, dialog));
                    Grid.SetColumn(export, 1);
                    row.Children.Add(open);
                    row.Children.Add(export);
                    results.Children.Add(row);
                }
            }
            if (matches.Count == 0) results.Children.Add(EvidenceText("No matching chats."));
        }
        search.TextChanged += (_, _) => Refresh();
        Refresh();
        dialog.Content = layout;
        await dialog.ShowDialog(this);
    }

    async Task ExportChat(Conversation chat, Window owner)
    {
        if (busy) return;
        var filename = string.Concat(chat.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).TrimEnd('.', ' ');
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export chat", SuggestedFileName = filename.Length == 0 ? "LocalDesk chat" : filename,
            DefaultExtension = "md", FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
        });
        if (file == null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(ChatMarkdownExport(chat));
            status.Text = "Chat exported · " + file.Name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { status.Text = "Could not export chat. " + error.Message; }
    }

    internal static string ChatMarkdownExport(Conversation chat)
    {
        var markdown = new StringBuilder().Append("# ").AppendLine(chat.Title);
        foreach (var message in chat.Messages)
        {
            markdown.AppendLine().AppendLine(message.Role == "user" ? "## You" : "## LocalDesk").AppendLine();
            markdown.AppendLine(string.IsNullOrWhiteSpace(message.Text) ? message.Summary ?? "" : message.Text);
        }
        return markdown.ToString();
    }
}
