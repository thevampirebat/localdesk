using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalDesk;

static class RunUpgradeChecks
{
    public static async Task Run(string baseFolder, CancellationToken ct)
    {
        string root = Path.Combine(baseFolder, "review-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "sample.txt");
        await File.WriteAllTextAsync(file, "old", ct);
        var chat = new Conversation { Title = "Main", Project = project, Mode = "agent", Pinned = true,
            Messages = [new ChatMessage { Text = "Find sample.txt" }, new ChatMessage { Role = "assistant", Text = file, ProjectUsed = project, Summary = "Found the file" }] };
        if (ChatCatalog.Search([new Conversation(), chat], "sample.txt").Single() != chat ||
            ChatCatalog.Search([new Conversation(), chat], null).First() != chat)
            throw new Exception("Chat search or pin order is broken.");
        var fork = ChatBranching.Copy(chat, 2, project, Path.Combine(root, "data"), ct);
        var branchFile = Path.Combine(fork.BranchFolder!, "sample.txt");
        if (fork.Messages.Count != 2 || !fork.Messages[1].Text.Contains(branchFile) || fork.Project == chat.Project ||
            fork.AgentSession != null || fork.Messages[1].ProjectUsed != null) throw new Exception("Branch retained the wrong history or session.");
        await File.WriteAllTextAsync(branchFile, "different", ct);
        if (await File.ReadAllTextAsync(file, ct) != "old") throw new Exception("The branch modified its source.");

        var allow = new ToolReviewGate((_, _) => Task.FromResult(true), fork.BranchFolder);
        ToolProposal Edit(string path) => new("editor", JsonSerializer.Serialize(new { path, old_text = "old", new_text = "new" }));
        if (!await allow.Allow([Edit(branchFile)], ct)) throw new Exception("A valid branch edit was blocked.");
        if (await allow.Allow([Edit(file)], ct)) throw new Exception("A project branch allowed an edit outside the copy.");
        var shell = new ToolReviewGate((_, _) => Task.FromResult(true), fork.BranchFolder);
        if (await shell.Allow([new("run_commands", """{"command":"echo test"}""")], ct)) throw new Exception("A branch allowed shell execution.");
        await CheckWebResearch(ct);

        foreach (bool approved in new[] { false, true })
        {
            const string parameters = """{"path":"sample.txt","old_text":"old","new_text":"new"}""";
            var model = new OllamaClient(new FakeToolResponse(parameters));
            using var bridge = new LocalBridge(model) { ActiveCancellation = ct };
            int prompts = 0;
            bridge.ReviewGate = new ToolReviewGate((call, _) =>
            {
                if (call.Name != "editor" || JsonNode.Parse(call.Arguments)?["new_text"]?.ToString() != "new")
                    throw new Exception("The review showed incomplete tool arguments.");
                prompts++;
                return Task.FromResult(approved);
            });
            using var client = new HttpClient();
            for (int i = 0; i < (approved ? 2 : 1); i++)
            {
                var content = new StringContent("""{"messages":[{"role":"user","content":"Replace old with new"}],"stream":true}""", Encoding.UTF8, "application/json");
                using var result = await client.PostAsync(bridge.BaseUrl + "/chat/completions", content, ct);
                var body = await result.Content.ReadAsStringAsync(ct);
                if (approved != body.Contains("tool_calls") || (!approved && !body.Contains("declined")))
                    throw new Exception("The gateway did not hold or release the proposed edit correctly.");
            }
            if (prompts != (approved ? 2 : 1)) throw new Exception("Approval was reused unexpectedly.");
        }
    }

    static async Task CheckWebResearch(CancellationToken ct)
    {
        static JsonObject Request(string url) => JsonSerializer.SerializeToNode(new { requests = new[] { new { url, prompt = "Find relevant articles" } } })!.AsObject();
        var search = Request("https://www.google.com/search?q=webOS+display");
        if (!ToolReviewGate.IsSearchRequest(ToolReviewGate.WebFetchTool, search)) throw new Exception("A standard search did not qualify for chat approval.");
        foreach (var url in new[] { "https://example.org/search?q=webOS", "http://www.google.com/search?q=test",
            "https://www.google.com/url?q=test", "https://www.google.com/search?q=test&btnI=1", "https://www.google.com.example.org/search?q=test" })
            if (ToolReviewGate.IsSearchRequest(ToolReviewGate.WebFetchTool, Request(url))) throw new Exception("A non-search URL qualified for remembered approval.");
        var mixed = (JsonArray)search["requests"]!;
        mixed.Add(Request("https://example.org/article")["requests"]![0]!.DeepClone());
        if (ToolReviewGate.IsSearchRequest(ToolReviewGate.WebFetchTool, search) || ToolReviewGate.IsSearchRequest("run_commands", Request("https://www.bing.com/search?q=test")))
            throw new Exception("Search approval included visited pages or commands.");

        var gate = new ToolReviewGate((_, _) => Task.FromResult(true));
        var fetch = new ToolProposal(ToolReviewGate.WebFetchTool, Request("https://www.bing.com/search?q=webOS").ToJsonString());
        for (int i = 0; i < 5; i++) if (!await gate.Allow([fetch], ct)) throw new Exception("Web research stopped too soon.");
        if (!gate.WebLimitReached || await gate.Allow([fetch], ct) || !gate.WebLimitBlocked) throw new Exception("Research budget did not prevent an extra fetch.");
        var payload = JsonNode.Parse("""{"messages":[{"role":"user","content":"Find relevant documentation"}],"tools":[{"function":{"name":"fetch_web_content"}},{"function":{"name":"read_files"}}],"tool_choice":"auto"}""")!.AsObject();
        LocalBridge.LimitResearchTools(payload, true, false);
        if (payload["tools"]?.AsArray().Single()?["function"]?["name"]?.ToString() != "read_files") throw new Exception("The research budget affected unrelated tools.");
        LocalBridge.LimitResearchTools(payload, false, true);
        if (payload.ContainsKey("tools") || payload.ContainsKey("tool_choice") || payload["think"]?.GetValue<bool>() != false)
            throw new Exception("The final-answer retry still offered tools or thinking.");
        var finalGate = new ToolReviewGate((_, _) => throw new Exception("The final-answer retry opened an approval dialog."));
        if (await finalGate.Allow([new("read_files", "{}")], ct, answerOnly: true)) throw new Exception("A final-answer retry passed a tool call.");
        var submit = new ToolReviewGate((_, _) => throw new Exception("A text-only submission opened an approval dialog."));
        if (!await submit.Allow([new("submit_and_exit", """{"summary":"Verified sources are limited.","verified":false}""")], ct) || submit.SubmittedSummary != "Verified sources are limited.")
            throw new Exception("The explicit final answer was lost.");
    }

    private sealed class FakeToolResponse(string arguments) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string Event(string name, string args) => "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, function = new { name, arguments = args } } } } } }
            }) + "\n\n";
            var stream = Event("editor", arguments[..20]) + Event("", arguments[20..]) + "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") });
        }
    }

    public static async Task Live(string baseFolder, CancellationToken ct)
    {
        string root = Path.Combine(baseFolder, "live-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(file, "OLD_VALUE", ct);
        var client = new OllamaClient();
        await client.Load(LocalModel.Fast.Id, ct, 16384);
        int reviewed = 0;
        // Separate sessions prevent the denied request becoming context for the approved one.
        foreach (bool allow in new[] { false, true })
        {
            var chat = new Conversation { Project = root, Mode = "agent", ContextTokens = 16384, OutputTokens = 2048 };
            using var bridge = new LocalBridge(client) { ActiveCancellation = ct };
            bridge.ReviewGate = new ToolReviewGate((proposal, _) =>
            {
                reviewed++;
                if (proposal.Name is not ("editor" or "write_to_file" or "replace_in_file"))
                    return Task.FromResult(false);
                return Task.FromResult(allow);
            }, root);
            using var agent = new AgentClient();
            await agent.Start(chat, bridge, Path.Combine(root, "state-"+allow), ct);
            await agent.Send("Edit sample.txt with the editor tool. Its whole text is OLD_VALUE. Replace OLD_VALUE with NEW_VALUE, then finish. Use only editor. Do not use commands.", ct);
            string expected = allow ? "NEW_VALUE" : "OLD_VALUE";
            if ((await File.ReadAllTextAsync(file, ct)).Trim() != expected || (allow == (bridge.ReviewGate.Blocked != null)))
                throw new Exception("Live approval failed. Gate: " + bridge.ReviewGate.Blocked + ", approved " + allow);
        }
        if (reviewed < 2) throw new Exception("Live agent did not request both reviews.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "review-live-passed.txt"), "Rejected edit left the file unchanged. Approved edit changed only the test file.");
    }
}
