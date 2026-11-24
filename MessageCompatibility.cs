using System.Text.Json.Nodes;
namespace LocalDesk;

static class MessageCompatibility
{
    public static void Normalize(JsonObject request)
    {
        if(request["messages"] is not JsonArray messages)return;
        var systems = new List<int>();
        bool hasUserTurn = false;
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            switch (message?["role"]?.ToString())
            {
                case "system" or "developer": systems.Add(i); break;
                case "user" when !hasUserTurn: hasUserTurn = !IsToolResponse(Text(message["content"])); break;
            }
        }
        if (systems.Count > 0 && !(systems is [0] && messages[0] is JsonObject { Count: 2 } system &&
            system["role"]?.ToString() == "system" && system["content"] is JsonValue value && value.TryGetValue<string>(out _)))
        {
            var content = string.Join("\n\n", systems.Select(i => Text(messages[i]?["content"])));
            for (int i = systems.Count - 1; i >= 0; i--) messages.RemoveAt(systems[i]);
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = content });
        }
        // Qwen requires a user turn beyond tool-response wrappers, even for summaries.
        // Keep all existing tool-call/result pairs intact and in their original order.
        if (!hasUserTurn)
            messages.Insert(systems.Count == 0 ? 0 : 1, new JsonObject { ["role"] = "user",
                ["content"] = "Continue the existing task using the conversation and tool results below. Follow the system instructions." });
    }
    static bool IsToolResponse(string content)=>content.Trim().StartsWith("<tool_response>",StringComparison.Ordinal)&&content.Trim().EndsWith("</tool_response>",StringComparison.Ordinal);
    static string Text(JsonNode? content)=>content is JsonArray blocks?string.Join("\n",blocks.Where(b=>b?["type"]?.ToString()=="text").Select(b=>b?["text"]?.ToString()??"")):content?.ToString()??"";
}
