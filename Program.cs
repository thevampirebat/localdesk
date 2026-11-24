using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
namespace LocalDesk;
class Program
{
    [STAThread] public static void Main(string[] args)
    {
        if(args.Contains("--self-test")){try{Smoke.Run(args).GetAwaiter().GetResult();Environment.Exit(0);}catch(Exception e){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"self-test-error.txt"),e.ToString());Environment.Exit(1);}return;}
        AppBuilder.Configure<DeskApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
public class DeskApp:Application
{
    public override void Initialize(){RequestedThemeVariant=ThemeVariant.Dark;var theme=new FluentTheme();theme.Palettes[ThemeVariant.Dark]=new ColorPaletteResources{Accent=Avalonia.Media.Color.Parse("#93BDB2")};Styles.Add(theme);}
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = desktop.Args is ["--workspace-ui-check" or "--agent-answer-ui-check", var directory]
                ? MainWindow.CreateWorkspaceCheck(directory, desktop, desktop.Args[0] == "--agent-answer-ui-check")
                : new MainWindow(desktop.Args?.Contains("--ui-preview") == true);
        base.OnFrameworkInitializationCompleted();
    }
}
static class Smoke
{
    public static async Task Run(string[] args)
    {
        if(args is [_, "--project-benchmark", var benchmarkModel, var benchmarkProject]){await ProjectBenchmark.Run(benchmarkModel,benchmarkProject);return;}
        var root=Path.Combine(AppContext.BaseDirectory,"validation");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"sample.txt"),"old value");
        var image=args.SkipWhile(x=>x!="--image").Skip(1).FirstOrDefault();
        var model=args.Contains("--large")?LocalModel.Large.Id:LocalModel.Fast.Id;var context=args.Contains("--large")?32768:16384;
        using var ct=new CancellationTokenSource(TimeSpan.FromMinutes(6));await ModelStorageChecks.Run(root,args.Contains("--storage-check-live"),ct.Token);await DashboardChecks.Run(root,ct.Token);await ContinuityChecks.Run(root,ct.Token);await ProficiencyChecks.Run(root,ct.Token);if(args is [_, "--proficiency-live", var achievement]){await ProficiencyChecks.Live(root,achievement,ct.Token);return;}if(args is [_, "--achievement-edits", var source]){await ProficiencyChecks.AchievementEdits(root,source,ct.Token);return;}await RunUpgradeChecks.Run(root,ct.Token);if(args.Contains("--review-live")){await RunUpgradeChecks.Live(root,ct.Token);return;}ProjectEvidenceChecks.Run(root);WorkspaceUpgradeChecks.Run(root);if(args.Contains("--comparison-check-live")){await WorkspaceUpgradeChecks.Live(root);return;}await ModelChecks.Run(ct.Token);await RecoveryChecks.Run(ct.Token);await ContextChecks.Run(ct.Token);if(args.Contains("--model-check-live")){await ModelChecks.Live(ct.Token);return;}if(args.Contains("--checks-only"))return;var ollama=new OllamaClient();await ollama.Load(model,ct.Token,context);
        var chat=new Conversation{Project=root,Mode="agent",Model=model,ContextTokens=context,OutputTokens=2048};var message=new ChatMessage{Text="Describe the colored shapes on the left and right in one sentence. Do not use tools."};if(image!=null)message.Images.Add(image);chat.Messages.Add(message);
        var chatSpeeds=new List<GenerationSpeed>();ollama.Speed+=chatSpeeds.Add;
        var answer=new System.Text.StringBuilder();await ollama.Chat(chat,s=>answer.Append(s),ct.Token);var chatAnswer=answer.ToString();
        if(!chatSpeeds.Any(s=>s.Finished&&!s.Estimated&&s.Tokens>0&&s.TokensPerSecond>0))throw new Exception("Chat speed metrics missing.");
        if(image!=null&&(!chatAnswer.Contains("red",StringComparison.OrdinalIgnoreCase)||!chatAnswer.Contains("blue",StringComparison.OrdinalIgnoreCase)))throw new Exception("Chat image check failed: "+chatAnswer);
        var crowded=new Conversation{Model=model,ContextTokens=context,OutputTokens=256};crowded.Messages.Add(new ChatMessage{Text="The important verification code is COMPACT_KEEP_4729. Keep it for later."});for(var i=0;i<16;i++)crowded.Messages.Add(new ChatMessage{Role=i%2==0?"assistant":"user",Text="Old reference log "+i+": "+string.Concat(Enumerable.Repeat("unrelated historical detail. ",180))});crowded.Messages.Add(new ChatMessage{Text="What is the important verification code? Reply only with the code."});
        var compactAnswer=new System.Text.StringBuilder();await ollama.Chat(crowded,s=>compactAnswer.Append(s),ct.Token);if(!compactAnswer.ToString().Contains("COMPACT_KEEP_4729")||crowded.BridgeSummary.Length==0)throw new Exception("Live automatic compaction lost the verification code.");
        var visibleCount=crowded.Messages.Count;await new ContextCompaction(ollama){Chat=crowded}.Manual(ct.Token);if(crowded.Messages.Count!=visibleCount||crowded.CompactedMessageCount!=visibleCount)throw new Exception("Live manual compaction removed visible messages.");crowded.Messages.Add(new ChatMessage{Text="What is the important verification code?"});compactAnswer.Clear();await ollama.Chat(crowded,s=>compactAnswer.Append(s),ct.Token);if(!compactAnswer.ToString().Contains("COMPACT_KEEP_4729"))throw new Exception("Compacted notes were not reused by Chat.");
        var note=Path.Combine(root,"attachment-check.txt");File.WriteAllText(note,"The verification code is FILE_INPUT_OK.");var attached=AttachedFiles.Import(note,root,"attachment-test");
        var fileChat=new Conversation{Model=model,ContextTokens=context,OutputTokens=256,Messages=[new ChatMessage{Text="What is the verification code in the attached file? Reply with the code only.",Files=[attached]}]};
        var fileAnswer=new System.Text.StringBuilder();await ollama.Chat(fileChat,s=>fileAnswer.Append(s),ct.Token);if(!fileAnswer.ToString().Contains("FILE_INPUT_OK"))throw new Exception("Text file attachment was not delivered to the model.");
        var links=Artifacts.Find("Created `attachment-check.txt`. Missing `not-a-real-file.txt`.",root);if(links.Count!=1||links[0].Path!=note)throw new Exception("Artifact link validation failed.");
        using var bridge=new LocalBridge(ollama){ActiveCancellation=ct.Token,Model=model};bridge.Images[message.Marker]=message.Images;
        using(var helperClient=new HttpClient())
        {
            var helpers=new[]{
                "{\"messages\":[{\"role\":\"system\",\"content\":\"Reply with READY.\"}],\"stream\":false,\"max_tokens\":64}",
                "{\"messages\":[{\"role\":\"system\",\"content\":\"Briefly summarize the tool result.\"},{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"probe1\",\"type\":\"function\",\"function\":{\"name\":\"probe\",\"arguments\":\"{}\"}}]},{\"role\":\"tool\",\"tool_call_id\":\"probe1\",\"content\":\"The test file contains LOCALDESK_OK.\"}],\"stream\":false,\"max_tokens\":128}"
            };
            foreach(var helper in helpers){using var response=await helperClient.PostAsync(bridge.BaseUrl+"/chat/completions",new StringContent(helper,System.Text.Encoding.UTF8,"application/json"),ct.Token);if(!response.IsSuccessStatusCode)throw new Exception("Helper template test failed: "+await response.Content.ReadAsStringAsync(ct.Token));}
        }
        var agentSpeeds=new List<GenerationSpeed>();bridge.Speed+=agentSpeeds.Add;
        using var agent=new AgentClient();var events=new List<string>();answer.Clear();agent.Update+=(kind,text)=>{if(kind=="text")answer.Append(text);events.Add(kind+": "+text);};
        await agent.Start(chat,bridge,Path.Combine(root,"state"),ct.Token);
        await agent.Send(message.Text+(image==null?"":"\n[LocalDeskImage:"+message.Marker+"]"),ct.Token);
        var agentAnswer=answer.ToString();if(image!=null&&(!agentAnswer.Contains("red",StringComparison.OrdinalIgnoreCase)||!agentAnswer.Contains("blue",StringComparison.OrdinalIgnoreCase)))throw new Exception("Agent image check failed: "+agentAnswer);
        answer.Clear();await agent.Send("In the selected project, read sample.txt and replace its contents with exactly: LOCALDESK_OK. Make the file change now, then briefly confirm. Do not touch other files.",ct.Token);
        if(File.ReadAllText(Path.Combine(root,"sample.txt")).Trim()!="LOCALDESK_OK")throw new Exception("Project edit check failed.");
        if(!agentSpeeds.Any(s=>s.Finished&&s.Tokens>0&&s.TokensPerSecond>0))throw new Exception("Agent speed metrics missing.");
        var state=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root,"state","cline","sessions",agent.SessionId!,agent.SessionId+".json")));
        if(state?["model"]?.ToString()!=chat.Model)throw new Exception("Cline did not use the selected model identity.");
        agent.Dispose();using(var resumed=new AgentClient()){await resumed.Start(chat,bridge,Path.Combine(root,"state"),ct.Token);await resumed.Send("Read sample.txt and report its current contents. Do not change files.",ct.Token);}
        using(var ps=await ollama.Http.GetAsync("/api/ps",ct.Token))
        {
            var models=System.Text.Json.Nodes.JsonNode.Parse(await ps.Content.ReadAsStringAsync(ct.Token))?["models"]?.AsArray();
            if(!models!.Any(m=>m?["name"]?.ToString()==OllamaClient.RuntimeModel(chat.Model)&&m?["context_length"]?.GetValue<int>()==chat.ContextTokens))throw new Exception("Agent context does not match the configured limit.");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"self-test-results.json"),System.Text.Json.JsonSerializer.Serialize(new{chatImage=chatAnswer,agentImage=agentAnswer,projectEdit=true,chatSpeeds=chatSpeeds.Where(s=>s.Finished),agentSpeeds=agentSpeeds.Where(s=>s.Finished),agentSession=agent.SessionId,events},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }
}









