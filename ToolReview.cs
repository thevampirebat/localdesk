using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalDesk;

public sealed record ToolProposal(string Name, string Arguments);

public sealed class ToolReviewGate(Func<ToolProposal, CancellationToken, Task<bool>> approve, string? branchRoot = null)
{
    internal const string WebFetchTool = "fetch_web_content";
    const int MaxWebBatches = 5;
    static readonly HashSet<string> ReadOnly = new(
        ["read_file", "read_files", "list_files", "search_files", "search_codebase", "list_code_definition_names",
         "attempt_completion", "plan_mode_respond", "act_mode_respond", "task_progress", "update_todo_list"], StringComparer.Ordinal);
    static readonly HashSet<string> BranchEditors = new(["editor", "write_to_file", "replace_in_file"], StringComparer.Ordinal);
    public string? Blocked { get; private set; }
    public string? SubmittedSummary { get; private set; }
    public bool WebLimitReached => WebBatches >= MaxWebBatches;
    public int WebBatches { get; private set; }
    public bool WebLimitBlocked { get; private set; }

    internal void ClearSubmittedSummary() => SubmittedSummary = null;

    internal void ClearWebLimitBlock()
    {
        if (!WebLimitBlocked) return;
        WebLimitBlocked = false;
        Blocked = null;
    }

    public async Task<bool> Allow(IEnumerable<ToolProposal> proposals, CancellationToken ct, bool answerOnly = false)
    {
        if (Blocked != null) return false;
        foreach (var action in proposals)
        {
            if (string.IsNullOrWhiteSpace(action.Name))
            {
                Blocked = "The model returned a tool call without a name.";
                return false;
            }
            JsonObject args;
            try
            {
                args = JsonNode.Parse(action.Arguments) as JsonObject ?? throw new JsonException();
            }
            catch (JsonException)
            {
                Blocked = "The model supplied incomplete tool arguments.";
                return false;
            }
            if (answerOnly)
            {
                Blocked = "The model tried to use a tool while writing the final answer.";
                return false;
            }
            if (action.Name == "submit_and_exit" && args.All(item => item.Key is "summary" or "verified") &&
                args["summary"] is JsonValue summary && summary.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) &&
                args["verified"] is JsonValue verified && verified.TryGetValue<bool>(out _))
            {
                SubmittedSummary = text;
                continue;
            }
            if (ReadOnly.Contains(action.Name) &&
                !(action.Name=="attempt_completion" && args.Any(item=>item.Key is "command" or "commands"))) continue;
            if (branchRoot != null && !SafeBranchEdit(action.Name, args, branchRoot))
            {
                Blocked = "This branch only allows reviewed edits inside its project copy. Commands and external tools are blocked.";
                return false;
            }
            if (action.Name == WebFetchTool && WebLimitReached)
            {
                Blocked = "The web research limit for this request was reached.";
                WebLimitBlocked = true;
                return false;
            }
            if (!await approve(action with { Arguments = args.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) }, ct))
            {
                Blocked = "Tool action declined: " + action.Name;
                return false;
            }
            if (action.Name == WebFetchTool) WebBatches++;
        }
        return true;
    }

    // Cline follows redirects. Remember approval only for known search endpoints with a single query parameter.
    internal static bool IsSearchRequest(string name, JsonObject? arguments)
    {
        if (name != WebFetchTool || arguments is not { Count: 1 } ||
            arguments["requests"] is not JsonArray { Count: > 0 } requests) return false;
        foreach (var item in requests)
        {
            if (item is not JsonObject request || request.Any(p => p.Key is not ("url" or "prompt")) ||
                request["url"] is not JsonValue value || !value.TryGetValue<string>(out var url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps ||
                address.Port != 443 || address.UserInfo.Length != 0 || address.Fragment.Length != 0 ||
                !address.Query.StartsWith("?q=", StringComparison.Ordinal) || address.Query.Length <= 3 ||
                address.Query.Contains('&') || address.Query.Contains(';')) return false;
            if ((address.IdnHost, address.AbsolutePath) is not
                (("www.google.com" or "www.bing.com", "/search") or ("html.duckduckgo.com", "/html/"))) return false;
        }
        return true;
    }

    internal static bool SafeBranchEdit(string name, JsonObject args, string root)
    {
        if (!BranchEditors.Contains(name) || args["path"] is not JsonValue value || !value.TryGetValue<string>(out var path) ||
            string.IsNullOrWhiteSpace(path)) return false;
        // Block editor sub-operations that can address a second file or invoke commands.
        IEnumerable<string> keys = name switch
        {
            "editor" => ["path", "old_text", "new_text", "replace_all"],
            "write_to_file" => ["path", "content"],
            "replace_in_file" => ["path", "diff"],
            _ => []
        };
        if (args.Any(item => !keys.Contains(item.Key))) return false;
        if (name == "editor" && args["new_text"] is not JsonValue) return false;
        try
        {
            var full = Path.GetFullPath(path, root);
            return WorkspaceScope.RelativeFile(root, full) is { } relative && relative != "." &&
                !relative.Contains(':') && !WorkspaceScope.HasReparsePoint(full);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public static IEnumerable<ToolProposal> ReadCalls(JsonArray calls) =>
        calls.Select(call => new ToolProposal(call?["function"]?["name"]?.ToString() ?? "",
            call?["function"]?["arguments"]?.ToString() ?? ""));

    public sealed class StreamCalls
    {
        private readonly SortedDictionary<(int Choice, int Call), (StringBuilder Name, StringBuilder Args)> calls = new();
        public bool HasCalls => calls.Count > 0;
        public void Add(JsonArray? choices)
        {
            if (choices == null) return;
            foreach (var choice in choices)
            {
                if (choice?["delta"]?["tool_calls"] is not JsonArray parts) continue;
                var choiceId = choice["index"]?.GetValue<int>() ?? 0;
                for (int index = 0; index < parts.Count; index++)
                {
                    var part = parts[index];
                    var key = (choiceId, part?["index"]?.GetValue<int>() ?? index);
                    if (!calls.TryGetValue(key, out var call))
                    {
                        call = (new(), new());
                        calls[key] = call;
                    }
                    call.Name.Append(part?["function"]?["name"]?.ToString());
                    call.Args.Append(part?["function"]?["arguments"]?.ToString());
                }
            }
        }
        public IEnumerable<ToolProposal> Proposals() => calls.Values.Select(v => new ToolProposal(v.Name.ToString(), v.Args.ToString()));
    }
}

public sealed partial class MainWindow
{
    enum ToolApproval { Decline, Once, ChatSearch, AllInChat }
    readonly HashSet<string> trustedToolChats = new(StringComparer.Ordinal);

    Task<bool> ReviewTool(ToolProposal action,CancellationToken ct) => ReviewTool(action,ct,false);

    async Task<bool> ReviewTool(ToolProposal action, CancellationToken ct, bool configuredCheck)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ct.IsCancellationRequested) return false;
            var args = JsonNode.Parse(action.Arguments) as JsonObject;
            if (!await ReviewInvestigation(action, ct)) return false;
            if (ct.IsCancellationRequested) return false;
            if (trustedToolChats.Contains(current.Id))
            {
                AddActivity("review", "Allowed by this chat · " + action.Name);
                return true;
            }
            if (args != null && ReviewProjectPreset(action.Name,args,configuredCheck)) return true;
            var canRemember = current.BranchFolder == null && ToolReviewGate.IsSearchRequest(action.Name, args);
            if (canRemember && current.AllowWebSearch)
            {
                AddActivity("review", "Allowed by this chat · web search");
                return true;
            }
            var dialog = UtilityWindow("Review agent action", 950, 675);
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22) };
            var header = new StackPanel { Spacing = 7, Margin = new Thickness(0, 0, 0, 14) };
            header.Children.Add(new TextBlock { Text = action.Name.Replace('_', ' '), FontSize = 23, FontWeight = FontWeight.SemiBold });
            header.Children.Add(EvidenceText("Working folder · " + current.Project));
            header.Children.Add(EvidenceText(canRemember
                ? "Allow this search once or remember search approval for this chat. Search queries go to the listed search engine. Opening other sites still asks for approval."
                : "Review the complete tool arguments. Approval applies to this action only."));
            header.Children.Add(EvidenceText("Approve all lets this chat run tools without asking again until you revoke it or close LocalDesk. In Project agent, this can include edits, commands and network access."));
            layout.Children.Add(header);
            string displayed = action.Arguments;
            if (action.Name == "editor" && args?["new_text"]?.GetValueKind() == JsonValueKind.String)
            {
                displayed = "Proposed edit\n" + (args["path"]?.ToString() ?? "") +
                    "\n\nBefore\n" + (args["old_text"]?.ToString() ?? "(new file)") +
                    "\n\nAfter\n" + args["new_text"] + "\n\nComplete tool arguments\n" + action.Arguments;
            }
            var preview = new ScrollViewer { Content = new SelectableTextBlock { Text = displayed,
                FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(13) }, Background = Brush("#11151C") };
            Grid.SetRow(preview, 1);
            layout.Children.Add(preview);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 15, 0, 0) };
            actions.Children.Add(Button("Decline", (_, _) => dialog.Close(ToolApproval.Decline)));
            actions.Children.Add(Button("Approve once", (_, _) => dialog.Close(ToolApproval.Once)));
            if (canRemember) actions.Children.Add(Button("Allow searches in this chat", (_, _) => dialog.Close(ToolApproval.ChatSearch)));
            actions.Children.Add(Button("Approve all in this chat", (_, _) => dialog.Close(ToolApproval.AllInChat)));
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);
            dialog.Content = layout;
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(ToolApproval.Decline)));
            if (ct.IsCancellationRequested) return false;
            var choice = await ShowRunApproval<ToolApproval>(dialog);
            if (ct.IsCancellationRequested) return false;
            if (choice == ToolApproval.ChatSearch)
            {
                current.AllowWebSearch = true;
                UpdateWebSearchPermission();
                Save();
            }
            if (choice == ToolApproval.AllInChat)
            {
                trustedToolChats.Add(current.Id);
                UpdateWebSearchPermission();
            }
            AddActivity("review", choice switch
            {
                ToolApproval.AllInChat => "Approved all tools for this chat until LocalDesk closes · " + action.Name,
                ToolApproval.ChatSearch => "Approved · web searches in this chat",
                ToolApproval.Once => "Approved · " + action.Name,
                _ => "Declined · " + action.Name
            });
            return choice != ToolApproval.Decline;
        });
    }
}
