using System.Net;
using System.Text;
using System.Text.Json.Nodes;
namespace LocalDesk;
static class ContextChecks
{
    public static async Task Run(CancellationToken ct)
    {
        CheckTokenGuard();
        await CheckRepeatedCompaction(ct);
        await ExperienceUpgradeChecks.Run(Path.Combine(AppContext.BaseDirectory,"validation"),ct);
        using var handler=new Summarizer();var client=new OllamaClient(handler);var chat=new Conversation{ContextTokens=4096,OutputTokens=512,Messages=[new ChatMessage{Text="visible history"}]};var compact=new ContextCompaction(client){Chat=chat};
        var messages=new JsonArray{new JsonObject{["role"]="system",["content"]="Keep rules"},new JsonObject{["role"]="user",["content"]="Keep GOAL_4729 and sample.txt"}};
        for(var i=0;i<6;i++){messages.Add(new JsonObject{["role"]="assistant",["tool_calls"]=new JsonArray(new JsonObject{["id"]="call"+i,["type"]="function",["function"]=new JsonObject{["name"]="read",["arguments"]="{}"}})});messages.Add(new JsonObject{["role"]="tool",["tool_call_id"]="call"+i,["content"]=i<4?new string('x',6000):"recent result"});}
        var original=new JsonObject{["messages"]=messages};var request=original.DeepClone().AsObject();var before=compact.Count(request);await compact.Prepare(request,ct);
        if(handler.Calls!=1||compact.Count(request)>=before||chat.Messages.Count!=1||chat.BridgeSummary.Length==0)throw new Exception("Automatic compaction did not reduce context safely.");
        var ids=new HashSet<string>();foreach(var m in request["messages"]!.AsArray()){if(m?["tool_calls"] is JsonArray calls)foreach(var call in calls)ids.Add(call!["id"]!.ToString());if(m?["role"]?.ToString()=="tool"&&!ids.Contains(m["tool_call_id"]!.ToString()))throw new Exception("Compaction orphaned a tool result.");}
        var next=original.DeepClone().AsObject();next["messages"]!.AsArray().Add(new JsonObject{["role"]="assistant",["content"]="done"});await compact.Prepare(next,ct);if(handler.Calls!=1)throw new Exception("Compacted checkpoint was not reused.");
        await compact.Manual(ct);if(chat.Messages.Count!=1||chat.CompactedMessageCount!=1||!chat.ContextSummary.Contains("GOAL_4729"))throw new Exception("Manual compaction lost visible history or notes.");
        var image=JsonNode.Parse("{\"type\":\"image_url\",\"image_url\":{\"url\":\""+new string('a',100000)+"\"}}")!;if(ContextCompaction.Estimate(image)>4096)throw new Exception("Image base64 was counted as text tokens.");
        using var failure=new Summarizer{Fail=true};var failedChat=new Conversation{Messages=[new ChatMessage{Text="must remain"}],ContextSummary="existing notes"};try{await new ContextCompaction(new OllamaClient(failure)){Chat=failedChat}.Manual(ct);throw new Exception("Expected a failed summary.");}catch(HttpRequestException){}catch(Exception e) when(e.Message.Contains("synthetic failure")){}
        if(failedChat.Messages.Count!=1||failedChat.ContextSummary!="existing notes")throw new Exception("Failed compaction changed original history.");
    }

    static async Task CheckRepeatedCompaction(CancellationToken ct)
    {
        using var handler = new Summarizer();
        var chat = new Conversation { Mode = "agent", ContextTokens = 4096, OutputTokens = 512 };
        const string task = "Active user request (verbatim):\nImplement FIRST and SECOND.\nPreserve THIRD.\n\nUser's acceptance criteria:\nKeep EXACT_VALUE.";
        var compact = new ContextCompaction(new OllamaClient(handler)) { Chat = chat, ActiveTask = task };
        var history = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "Keep rules" },
            new JsonObject { ["role"] = "user", ["content"] = task }
        };
        AddResults(0);
        var first = await Prepare();
        var firstBoundary = chat.BridgeBoundary;
        if (handler.Calls != 1 || !ContainsTask(first) || !firstBoundary.StartsWith("tool:"))
            throw new Exception("The first compacted request lost the original task or checkpoint.");

        AddResults(6);
        var second = await Prepare();
        if (handler.Calls != 2 || !ContainsTask(second) || chat.BridgeBoundary == firstBoundary)
            throw new Exception("A second compaction failed to advance the checkpoint or carry the original task.");

        var boundaryId = chat.BridgeBoundary["tool:".Length..];
        history.OfType<JsonObject>().Single(message => message["tool_call_id"]?.ToString() == boundaryId)["content"] =
            "The same tool result with a different excerpt size.";
        var third = await Prepare();
        if (handler.Calls != 2 || !ContainsTask(third))
            throw new Exception("A changed tool excerpt invalidated the checkpoint or lost the original task.");

        void AddResults(int offset)
        {
            for (int i = 0; i < 6; i++)
            {
                var id = "tool-" + (offset + i);
                history.Add(new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
                    { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read", ["arguments"] = "{}" } }) });
                history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id,
                    ["content"] = i < 4 ? new string('x', 6000) : "recent result" });
            }
        }

        async Task<JsonObject> Prepare()
        {
            var request = new JsonObject { ["messages"] = history.DeepClone() };
            if (await compact.Prepare(request, ct) != compact.Count(request))
                throw new Exception("Reported prompt size did not match the outbound request.");
            return request;
        }

        static bool ContainsTask(JsonObject request) => request["messages"]!.AsArray().Count(message =>
            message?["content"]?.ToString().Contains(task, StringComparison.Ordinal) == true) == 1;
    }

    static void CheckTokenGuard()
    {
        var messages = new JsonArray();
        var source = new string('x', 5500);
        void Add(string id, string name, string args, string text)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
                { ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } }) });
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text });
        }
        Add("first", "read_files", "range A", source);
        Add("again", "read_files", "range A", source);
        Add("different-range", "read_files", "range B", source);
        Add("changed-file", "read_files", "range A", source[..2750] + "y" + source[2751..]);
        Add("command", "run_commands", "range A", source);
        ToolOutputArchive.Clip(new JsonObject { ["messages"] = messages }, 4096, _ => "archived");
        var change = TokenGuard.Reduce(messages);
        if (change.Results != 1 || change.Tokens <= 0 || !messages[3]!["content"]!.ToString().Contains("earlier in the current context") ||
            messages[3]!["tool_call_id"]!.ToString() != "again" || !messages[7]!["content"]!.ToString().Contains("shortened"))
            throw new Exception("Token Guard removed a distinct result or failed to shorten an identical read.");
    }
    sealed class Summarizer:HttpMessageHandler
    {
        public int Calls;public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.InternalServerError:HttpStatusCode.OK){Content=new StringContent(Fail?"{\"error\":\"synthetic failure\"}":"{\"message\":{\"content\":\"Goal GOAL_4729. Work on sample.txt. Checks and remaining work preserved.\"}}",Encoding.UTF8,"application/json")});}
    }
}
