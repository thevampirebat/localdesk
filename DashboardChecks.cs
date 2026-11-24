using System.Net;

namespace LocalDesk;

static class DashboardChecks
{
    private sealed class CapabilitiesHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"capabilities":["completion","vision","tools"]}""")
            });
        }
    }

    public static async Task Run(string root, CancellationToken ct)
    {
        string folder = Path.Combine(root, "dashboard-project");
        var chat = new Conversation
        {
            Messages = [
                new ChatMessage { Role = "user", Text = "Older request" },
                new ChatMessage { Role = "assistant", ProjectUsed = folder, Summary = "Old result", CompletedAt = new DateTime(2026, 10, 1) },
                new ChatMessage { Role = "user", Text = "Different project" },
                new ChatMessage { Role = "assistant", ProjectUsed = Path.Combine(root, "different"), Summary = "Unrelated result" },
                new ChatMessage { Role = "user", Text = "Latest request" },
                new ChatMessage { Role = "assistant", ProjectUsed = folder + Path.DirectorySeparatorChar, Summary = "Latest result",
                    CompletedAt = new DateTime(2026, 11, 1), BuildCheck = new BuildResult(0, "build.log") }
            ]
        };
        var recent = ProjectRunHistory.Recent([chat], folder, 12);
        if (recent.Count != 2 || recent[0].Request != "Latest request" || recent[0].Reply.BuildCheck?.ExitCode != 0)
            throw new Exception("The dashboard did not scope and sort runs for the selected project.");

        var path = Path.Combine(root, "large-preview.log");
        await File.WriteAllTextAsync(path, new string('x', 10000), ct);
        var preview = await PreviewFiles.ReadText(path, 1000, ct);
        if (preview is not { Text.Length: 1000, Truncated: true })
            throw new Exception("Large file previews did not return a bounded excerpt.");

        var handler = new CapabilitiesHandler();
        var client = new OllamaClient(handler);
        if (!(await client.Capabilities("first", ct)).Contains("tools")) throw new Exception("Capability parsing failed.");
        await client.Capabilities("first", ct);
        await client.Capabilities("second", ct);
        if (handler.Calls != 2) throw new Exception("Capability lookup missed its cache or mixed models.");
        client.ClearCapabilities();
        await client.Capabilities("first", ct);
        if (handler.Calls != 3) throw new Exception("Model refresh did not clear capability metadata.");
    }
}
