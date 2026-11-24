using System.Net;
using System.Text;
using System.Text.Json.Nodes;
namespace LocalDesk;

static class RecoveryChecks
{
    public static async Task Run(CancellationToken ct)
    {
        var cases=new[]{
            "{\"messages\":[{\"role\":\"system\",\"content\":\"Summarize\"}]}",
            "{\"messages\":[{\"role\":\"system\",\"content\":\"A\"},{\"role\":\"system\",\"content\":\"B\"},{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"1\"}]},{\"role\":\"tool\",\"tool_call_id\":\"1\",\"content\":\"result\"}]}",
            "{\"messages\":[{\"role\":\"user\",\"content\":\"<tool_response>result</tool_response>\"}]}",
            "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Inspect\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"test\"}}]}]}"
        };
        foreach(var input in cases)
        {
            var data=JsonNode.Parse(input)!.AsObject();var original=data.DeepClone();
            var originalTurns=data["messages"]!.AsArray().Where(m=>m?["role"]?.ToString() is not ("system" or "developer")).ToList();
            MessageCompatibility.Normalize(data);
            var once=data.ToJsonString();MessageCompatibility.Normalize(data);
            if(once!=data.ToJsonString())throw new Exception("Message normalization is not idempotent.");
            var messages=data["messages"]!.AsArray();
            if (originalTurns.Any(turn => !messages.Any(message => ReferenceEquals(message, turn))))
                throw new Exception("Normalization copied an existing conversation turn.");
            if(!messages.Any(m=>m?["role"]?.ToString()=="user"&&!m["content"]!.ToString().StartsWith("<tool_response>")))throw new Exception("Missing plain user turn.");
            if(messages.Skip(1).Any(m=>m?["role"]?.ToString()=="system"))throw new Exception("System turn was left in the middle.");
            foreach(var tool in original["messages"]!.AsArray().Where(m=>m?["role"]?.ToString() is "assistant" or "tool"))
                if(!messages.Any(m=>JsonNode.DeepEquals(m,tool)))throw new Exception("Tool history changed.");
            if(input.Contains("image_url")&&!JsonNode.DeepEquals(original,data))throw new Exception("Existing image message changed.");
        }
        foreach(var scenario in new[]{"recover","template","bounded","other"})
        {
            using var handler=new RejectOnce(scenario);
            var ollama=new OllamaClient(handler);using var bridge=new LocalBridge(ollama){OutputTokens=2048,ActiveCancellation=ct};
            using var http=new HttpClient();
            using var response=await http.PostAsync(bridge.BaseUrl+"/chat/completions",new StringContent("{\"model\":\"wrong-model\",\"messages\":[{\"role\":\"user\",\"content\":\"test\"}],\"max_tokens\":8192}",Encoding.UTF8,"application/json"),ct);
            var expected=scenario=="other"?1:2;
            if(handler.Calls!=expected||response.IsSuccessStatusCode!=(scenario is "recover" or "template"))throw new Exception("Recovery check failed: "+scenario);
            if(handler.Last?["model"]?.ToString()!=OllamaClient.RuntimeModel(LocalModel.Fast.Id)||handler.Last?["max_tokens"]?.GetValue<int>()!=2048)throw new Exception("Selected model/output limit was not honored.");
            if(handler.Last?["messages"]?.AsArray().Skip(1).Any(m=>m?["role"]?.ToString()=="system")==true)throw new Exception("Recovery introduced a misplaced system turn.");
        }
    }
    sealed class RejectOnce(string scenario):HttpMessageHandler
    {
        public int Calls;public JsonNode? Last;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Last=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            var success=(scenario is "recover" or "template")&&Calls>1;
            return new HttpResponseMessage(success?HttpStatusCode.OK:HttpStatusCode.InternalServerError){Content=new StringContent(success?"{\"choices\":[]}":scenario=="other"?"{\"error\":\"unrelated failure\"}":scenario=="template"?"{\"error\":\"Jinja Exception: No user query found in messages.\"}":"{\"error\":\"llama-server returned invalid tool call arguments: unexpected end of JSON input\"}",Encoding.UTF8,"application/json")};
        }
    }
}
