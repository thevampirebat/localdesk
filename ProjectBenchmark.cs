using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LocalDesk;

static class ProjectBenchmark
{
    public static async Task Run(string model, string project)
    {
        string target = Path.Combine(project, "src", "AchievementLabs.Desktop", "MainWindow.axaml");
        string buildTarget = Path.Combine(Path.GetDirectoryName(target)!, "AchievementLabs.Desktop.csproj");
        string expected = File.ReadAllText(target).Replace("Content=\"Check for updates\" Click=\"CheckUpdates\"", "Content=\"Check for app updates\" Click=\"CheckUpdates\"");
        string results = project + "-result.json";
        string trace = project + "-activity.log";
        File.WriteAllText(trace, "");
        var client = new OllamaClient();
        var chat = new Conversation { Model = model, Mode = "agent", Project = project, ContextTokens = 16384, OutputTokens = 4096 };
        var evidence = new ProjectEvidence(project, @"D:\Documents", []);
        var watch = Stopwatch.StartNew();
        double loadSeconds = 0, agentSeconds = 0;
        string? error = null;
        int? exitCode = null;
        var speeds = new List<double>();
        var response = new StringBuilder();

        try
        {
            using var load = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await client.Load(model, load.Token, chat.ContextTokens);
            loadSeconds = watch.Elapsed.TotalSeconds;
            using var run = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var bridge = new LocalBridge(client) { ActiveCancellation = run.Token };
            bridge.Speed += speed => { if (speed.Finished && speed.TokensPerSecond > 0) lock(speeds) speeds.Add(speed.TokensPerSecond); };
            using var agent = new AgentClient();
            agent.Update += (kind, detail) =>
            {
                if (kind == "text") response.Append(detail);
                else File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss} {kind}: {detail}\n");
            };
            await agent.Start(chat, bridge, project + "-state", run.Token);
            await agent.Send("In the selected AchievementLabs source copy, rename the About page's “Check for updates” button to “Check for app updates”. Change only the visible button label and keep its handler and behavior. Search the desktop source, edit exactly one file, and finish after inspecting the edited line. LocalDesk will build it separately. Do not run the app or any build, and do not contact remote services.", run.Token);
        }
        catch (Exception e) { error = e is OperationCanceledException ? "Timed out" : e.Message; }
        agentSeconds = Math.Max(0, watch.Elapsed.TotalSeconds - loadSeconds);

        var edits = evidence.Finish();
        var correctEdit = File.ReadAllText(target) == expected && edits.Count == 1 && edits[0].Path == target;
        try
        {
            using var build = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            exitCode = (await ProjectEvidence.Build(buildTarget, project, project + "-build.log", build.Token)).ExitCode;
        }
        catch (Exception e) { error = (error + " Build: " + e.Message).Trim(); }
        File.WriteAllText(results, JsonSerializer.Serialize(new {
            model, correctEdit, exitCode, loadSeconds, agentSeconds, totalSeconds = watch.Elapsed.TotalSeconds,
            meanTokensPerSecond = speeds.Count == 0 ? (double?)null : speeds.Average(),
            error, changes = edits.Select(f => new { f.Path, f.Change }), response = response.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
