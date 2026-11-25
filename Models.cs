using System.Text.Json;
namespace LocalDesk;
public record LocalModel(string Id,string Name,string Note)
{
    public static readonly LocalModel Fast = new("qwen3.5:9b-q5km", "Qwen3.5 · 9B",
        "Best for everyday chat, screenshots and small project edits. A good first choice on this PC.");
    public static readonly LocalModel Large = new("codex-bench-qwen36-35b:q4km", "Qwen3.6 · 35B",
        "Best tried on harder edits or when you want a second approach. Some model weights run outside GPU memory on this PC, which can slow long tasks.");
    public static readonly LocalModel Gemma = new("gemma4:12b", "Gemma 4 · 12B",
        "Try for explaining screenshots, reviewing code or getting a second opinion. For long project runs, Qwen3.5 9B is the more proven starting point on this PC.");

    public static LocalModel FromTag(string tag) => tag switch
    {
        "qwen3.5:9b-q5km" or "qwen3.5:9b" => Fast with { Id = tag },
        "codex-bench-qwen36-35b:q4km" or "qwen3.6:35b" => Large with { Id = tag },
        "gemma4:12b" => Gemma,
        _ => new(tag, tag, "Use the capabilities below to choose a mode for this model.")
    };
    public override string ToString()=>Name;
}
public class ChatMessage
{
    public string Role {get;set;}="user";
    public string Text {get;set;}="";
    public List<string> Images {get;set;}=[];
    public List<FileAttachment> Files {get;set;}=[];
    public string? Summary {get;set;}
    public int SummaryOffset {get;set;}
    public string? ProjectUsed {get;set;}
    public DateTime? CompletedAt {get;set;}
    public string? RunModel {get;set;}
    public double? RunSeconds {get;set;}
    public RunDiagnostics? Diagnostics {get;set;}
    public string? EvidenceNote {get;set;}
    public List<VerifiedFile> VerifiedFiles {get;set;}=[];
    public BuildResult? BuildCheck {get;set;}
    public FocusedResult? Focused {get;set;}
    public int RepairAttempts {get;set;}
    public string? PauseReason {get;set;}
    public string AcceptanceCriteria { get; set; } = "";
    public bool Successful {get;set;}
    public bool ExpectedEdits {get;set;}
    public List<ArtifactLink> Artifacts {get;set;}=[];
    public string Marker {get;set;}=Guid.NewGuid().ToString("N");
}
public class Conversation
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Title {get;set;}="New chat";
    public string Model {get;set;}=LocalModel.Fast.Id;
    public string? ParentChat {get;set;}
    public string? BranchFolder {get;set;}
    public bool Pinned {get;set;}
    public bool AllowWebSearch { get; set; }
    public bool Thinking {get;set;}
    public int ContextTokens {get;set;}=32768;
    public int OutputTokens {get;set;}=8192;
    public int TimeLimitMinutes {get;set;}=5;
    public string ContextSummary {get;set;}="";
    [System.Text.Json.Serialization.JsonIgnore] internal string? DocumentContext { get; set; }
    public int CompactedMessageCount {get;set;}
    public string BridgeSummary {get;set;}="";
    public string BridgeBoundary {get;set;}="";
    public int LastContextUsed {get;set;}
    public bool ContextUsageEstimated {get;set;}=true;
    public int ContextOverheadTokens {get;set;}
    public bool ContextNotesOnly {get;set;}
    public ContextBreakdown? LastBreakdown {get;set;}
    public string AcceptanceCriteria { get; set; } = "";
    public FocusedCheck? AcceptanceCheck { get; set; }
    public bool ReviewBeforeChanges { get; set; }
    public string Mode {get;set;}="chat";
    public string Project {get;set;}="";
    public string? BuildProject {get;set;}
    public string? AgentSession {get;set;}
    public List<ChatMessage> Messages {get;set;}=[];
    public List<ActivityEntry> Activity {get;set;}=[];
    public string Draft { get; set; } = "";
    public List<string> DraftImages { get; set; } = [];
    public List<FileAttachment> DraftFiles { get; set; } = [];
    public DateTime Updated {get;set;}=DateTime.Now;
    public override string ToString()=>Pinned?"★  "+Title:Title;
}
public record ActivityEntry(DateTime Time,string Kind,string Text);
public class FileAttachment
{
    public string Name {get;set;}="";
    public string SourcePath {get;set;}="";
    public string StoredPath {get;set;}="";
    public bool IsText {get;set;}
}
public sealed class ChatStore
{
    public string Root {get;}
    public List<Conversation> Chats {get;private set;}=[];
    public ChatStore() : this(Path.Combine(AppContext.BaseDirectory, "data")) { }
    internal ChatStore(string root)
    {
        Root=root;
        Directory.CreateDirectory(Root);
        try { Chats=JsonSerializer.Deserialize<List<Conversation>>(File.ReadAllText(Path.Combine(Root,"chats.json")))??[]; }
        catch { }
        foreach (var chat in Chats)
        {
            var expectsChanges = false;
            foreach (var message in chat.Messages)
            {
                if (message.Role == "user") expectsChanges = EditRunOutcome.ExpectsChanges(message.Text, expectsChanges);
                else if (message.Role == "assistant")
                {
                    message.ExpectedEdits |= expectsChanges && message.ProjectUsed != null;
                    if (message.Successful) EditRunOutcome.MarkUnverified(message, message.ExpectedEdits);
                    expectsChanges = message.ExpectedEdits || message.VerifiedFiles.Count > 0;
                }
            }
        }
    }
    public void Save(){var path=Path.Combine(Root,"chats.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(Chats,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);}
    public string ImportImage(string file,string chatId){var dir=Path.Combine(Root,"images",chatId);Directory.CreateDirectory(dir);var dest=Path.Combine(dir,Guid.NewGuid().ToString("N")+Path.GetExtension(file));File.Copy(file,dest);return dest;}
    public void Delete(Conversation chat){Chats.Remove(chat);Save();var dir=Path.Combine(Root,"images",chat.Id);if(Directory.Exists(dir))Directory.Delete(dir,true);}
}

public sealed class DeskSettings
{
 public bool LoadModelAtStartup {get;set;}=false;
 public bool StartInAgent {get;set;}=false;
 public bool ShowActivity {get;set;}=true;
 public bool RunNotifications { get; set; } = true;
 public bool ReduceDuplicateReads { get; set; } = true;
 public string Theme { get; set; } = "System";
 public int ChatTextSize { get; set; } = 16;
 public decimal ModelRamGiB { get; set; }
 public decimal ModelVramGiB { get; set; }
 public string? DefaultModel { get; set; }
 public string? ServerOrigin { get; set; }
 public bool CompatibleServer { get; set; }
 public Dictionary<string,ModelProfile> Profiles {get;set;}=new(StringComparer.OrdinalIgnoreCase);
 public Dictionary<string,List<GpuBenchmarkSample>> Benchmarks {get;set;}=new(StringComparer.OrdinalIgnoreCase);
 public string WorkingFolder {get;set;}=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
 public static DeskSettings Load(string root){try{return JsonSerializer.Deserialize<DeskSettings>(File.ReadAllText(Path.Combine(root,"settings.json")))??new();}catch{return new();}}
 public void Save(string root){var path=Path.Combine(root,"settings.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(this));File.Move(path+".tmp",path,true);}
 [System.Text.Json.Serialization.JsonIgnore] public string Folder=>string.IsNullOrWhiteSpace(WorkingFolder)?Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments):WorkingFolder.Trim();
}
