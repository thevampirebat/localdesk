using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocalDesk;

public sealed record ModelProfile(int Context, int Output, bool Thinking);

public sealed partial class MainWindow
{
    void ApplyProfile(string modelId)
    {
        var fallback = modelId == LocalModel.Large.Id ? new ModelProfile(16384, 4096, false) : new ModelProfile(32768, 8192, false);
        var profile = preferences.Profiles.GetValueOrDefault(modelId, fallback);
        current.ContextTokens = profile.Context;
        current.OutputTokens = profile.Output;
        current.Thinking = profile.Thinking;
        current.LastContextUsed = 0;
        current.LastBreakdown = null;
        current.BridgeSummary = "";
        current.BridgeBoundary = "";
        current.AgentSession = null;
        agent.Dispose();
        UpdateContext(new(0, current.ContextTokens, true));
    }

    async Task SwitchModel(string modelId)
    {
        if (!string.Equals(current.Model, modelId, StringComparison.OrdinalIgnoreCase))
        {
            current.Model = modelId;
            ApplyProfile(modelId);
        }
        ToolTip.SetTip(model, SelectedModel.Note);
        Save();
        await LoadModel();
    }
}

public static class ChatCatalog
{
    public static IReadOnlyList<Conversation> Search(IEnumerable<Conversation> items, string? query)
    {
        var term = query?.Trim() ?? "";
        bool Has(string? text) => text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
        return items.Where(chat => term.Length == 0 || Has(chat.Title) || Has(chat.Project) ||
            chat.Messages.Any(message => Has(message.Text) || Has(message.Summary) ||
                message.Files.Any(file => Has(file.Name) || Has(file.SourcePath)) ||
                message.Artifacts.Any(file => Has(file.Name) || Has(file.Path)) ||
                message.VerifiedFiles.Any(file => Has(file.Path))))
            .OrderByDescending(chat => chat.Pinned).ThenByDescending(chat => chat.Updated).ToList();
    }
}

public sealed partial class MainWindow
{
    readonly TextBox chatSearch = new() { PlaceholderText = "Search chats or files…", Margin = new Thickness(0, 3, 0, 4) };
    Button pinChat = null!;

    void UpdatePin()
    {
        if (pinChat != null) pinChat.Content = current.Pinned ? "Unpin chat" : "Pin chat";
    }

    void TogglePin()
    {
        if (busy) return;
        current.Pinned = !current.Pinned;
        store.Save();
        UpdatePin();
        RefreshHistory();
    }
}
