using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;

namespace LocalDesk;

internal sealed class LocalMcp(Uri endpoint) : IDisposable
{
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    { MaxResponseContentBufferSize = 1024 * 1024, Timeout = TimeSpan.FromSeconds(20) };
    string? session;
    string? protocol;
    int sequence;

    public static bool TryEndpoint(string? text, out Uri? uri)
    {
        return Uri.TryCreate(text?.Trim(), UriKind.Absolute, out uri) && uri.IsLoopback &&
            uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length == 0;
    }

    public async Task<JsonArray> Discover(CancellationToken ct)
    {
        var init = await Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
            clientInfo = new { name = "LocalDesk", version = "1.3" } }, ct);
        protocol = init?["protocolVersion"]?.GetValue<string>() ?? throw new InvalidOperationException("MCP negotiation failed.");
        if (protocol != "2025-06-18" && protocol != "2025-03-26" && protocol != "2025-11-25")
            throw new InvalidOperationException("The server selected an unsupported MCP version.");
        await Call("notifications/initialized", new { }, ct, notification: true);
        var result = await Call("tools/list", new { }, ct);
        return result?["tools"] as JsonArray ?? [];
    }

    public async Task<JsonNode?> Call(string method, object args, CancellationToken ct, bool notification = false)
    {
        var id = ++sequence;
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method,
            ["params"] = System.Text.Json.JsonSerializer.SerializeToNode(args) };
        if (!notification) body["id"] = id;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (session != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (protocol != null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocol);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) session = ids.FirstOrDefault();
        if (notification) return null;
        JsonNode? message = null;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var data = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line[5..].TrimStart());
                if (data.Length > 1024 * 1024) throw new InvalidOperationException("MCP reply exceeded the size limit.");
                if (line.Length != 0 || data.Length == 0) continue;
                var eventData = JsonNode.Parse(data.ToString());
                data.Clear();
                if (eventData?["id"]?.ToString() == id.ToString()) { message = eventData; break; }
            }
        }
        else
        {
            await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
            message = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        if (message?["id"]?.ToString() != id.ToString()) throw new InvalidOperationException("MCP reply ID did not match.");
        if (message["error"] is { } error) throw new InvalidOperationException(error["message"]?.ToString() ?? "MCP request failed.");
        return message["result"];
    }

    public void Dispose() => http.Dispose();
}

public sealed partial class MainWindow
{
    sealed record ProjectTool(string Name, string Description, JsonNode? Schema)
    {
        public override string ToString() => Name;
    }

    async Task ShowProjectTools()
    {
        if (busy || sendingRequest || !Directory.Exists(current.Project) || WorkspaceScope.IsBroad(current.Project, preferences.Folder))
        { status.Text = "Open a specific project folder first."; return; }
        var projectEntry = Projects.Remember(current.Project);
        var dialog = UtilityWindow("Project MCP tools", 780, 710);
        var address = new TextBox { Text = projectEntry.McpEndpoint, PlaceholderText = "http://127.0.0.1:8080/mcp" };
        var list = new ComboBox();
        var description = EvidenceText("");
        var args = new TextBox { Text = "{}", AcceptsReturn = true, Height = 82 };
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 160, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var note = EvidenceText("Use a local MCP server you administer. Only tools marked read-only are shown. You choose each call.");
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ProjectTool tool) description.Text = tool.Description + "\n" + tool.Schema?.ToJsonString();
        };
        LocalMcp? client = null;
        using var closed = new CancellationTokenSource();
        dialog.Closed += (_, _) => closed.Cancel();
        var discover = Button("Save and discover", null);
        var run = Button("Run selected tool", null);
        var clear = Button("Disable for this project", (_, _) =>
        {
            projectEntry.McpEndpoint = null;
            Projects.Save();
            dialog.Close();
        });
        discover.Click += async (_, _) =>
        {
            if (!LocalMcp.TryEndpoint(address.Text, out var uri)) { note.Text = "Enter a loopback HTTP MCP address."; return; }
            discover.IsEnabled = run.IsEnabled = false;
            list.ItemsSource = null;
            try
            {
                client?.Dispose();
                client = new LocalMcp(uri!);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var tools = (await client.Discover(timeout.Token)).OfType<JsonObject>()
                    .Where(tool => tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true && tool["name"] != null)
                    .Select(tool => new ProjectTool(tool["name"]!.ToString(), tool["description"]?.ToString() ?? "", tool["inputSchema"])).ToList();
                list.ItemsSource = tools;
                list.SelectedIndex = tools.Count > 0 ? 0 : -1;
                projectEntry.McpEndpoint = uri!.ToString();
                Projects.Save();
                note.Text = $"{tools.Count} read-only tools found.";
            }
            catch (Exception error) { note.Text = error.Message; }
            finally { discover.IsEnabled = run.IsEnabled = true; }
        };
        run.Click += async (_, _) =>
        {
            if (client == null || list.SelectedItem is not ProjectTool tool) return;
            try
            {
                var inputArgs = JsonNode.Parse(args.Text ?? "{}") as JsonObject ?? throw new InvalidOperationException("Arguments must be a JSON object.");
                var approval = UtilityWindow("Approve MCP tool call", 600, 370);
                approval.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(22), Spacing = 10, Children = {
                    EvidenceText(tool.Name), EvidenceText(projectEntry.McpEndpoint ?? ""), EvidenceText(inputArgs.ToJsonString()),
                    Button("Approve this call", (_, _) => approval.Close(true)), Button("Cancel", (_, _) => approval.Close(false)) } } };
                if (!await approval.ShowDialog<bool>(dialog)) return;
                discover.IsEnabled = run.IsEnabled = false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var result = await client.Call("tools/call", new { name = tool.Name, arguments = inputArgs }, timeout.Token);
                var value = result?.ToJsonString() ?? "";
                output.Text = value.Length <= 16000 ? value : value[..16000];
            }
            catch (Exception error) { note.Text = error.Message; }
            finally { discover.IsEnabled = run.IsEnabled = true; }
        };
        var sendResult = Button("Put result in draft", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(output.Text)) return;
            input.Text = (input.Text ?? "") + "\n\nMCP result (reference data):\n" + output.Text;
            dialog.Close();
        });
        dialog.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(22), Spacing = 9,
            Children = { note, address, discover, clear, list, description, args, run, output, sendResult } } };
        try { await dialog.ShowDialog(this); }
        finally { client?.Dispose(); }
    }
}
