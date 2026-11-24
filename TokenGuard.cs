using System.Text.Json.Nodes;

namespace LocalDesk;

internal static class TokenGuard
{
    internal static (int Results, int Tokens) Reduce(JsonArray messages,
        List<(JsonObject Message, JsonValue Content)>? replaced = null)
    {
        var reads = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new Dictionary<(string Call, string Text), string>();
        int results = 0, tokens = 0;
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["role"]?.ToString() == "assistant" && message["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls.OfType<JsonObject>())
                {
                    if (call["id"]?.ToString() is not { Length: > 0 } id || call["function"] is not JsonObject function) continue;
                    var name = function["name"]?.ToString();
                    if (name is "read_file" or "read_files") reads[id] = name + "\n" + function["arguments"];
                    else reads.Remove(id);
                }
            }
            if (message["role"]?.ToString() != "tool" || message["tool_call_id"]?.ToString() is not { } toolId ||
                !reads.TryGetValue(toolId, out var key) || message["content"] is not JsonValue content ||
                !content.TryGetValue<string>(out var text) || text.Length < 512 ||
                text.Contains("[LocalDesk retained this identical", StringComparison.Ordinal)) continue;
            if (seen.TryAdd((key, text), toolId)) continue;
            var original = seen[(key, text)];
            if (original == toolId) continue;
            var reference = $"[LocalDesk retained this identical file-read result earlier in the current context, in tool result {original}. The returned content matched exactly.]";
            if (reference.Length >= text.Length) continue;
            replaced?.Add((message, content));
            message["content"] = reference;
            results++;
            tokens += Math.Max(0, (int)Math.Ceiling((text.Length-reference.Length)/2.5));
        }
        return (results, tokens);
    }
}
