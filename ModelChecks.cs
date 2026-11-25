using System.Net;
using System.Text;
using System.Text.Json;
namespace LocalDesk;
static class ModelChecks
{
 sealed class Handler(string body):HttpMessageHandler
 {
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){var json=await request.Content!.ReadAsStringAsync(ct);if(!json.Contains("custom:test"))throw new Exception("Download lost model identity.");return new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/x-ndjson")};}
 }
 public static async Task Live(CancellationToken ct)
 {
  var client=new OllamaClient();var tag="localdesk-validation:"+Guid.NewGuid().ToString("N");var runtime=OllamaClient.RuntimeModel(tag);
  try{using(var create=await client.Post("/api/create",new{model=tag,from=LocalModel.Fast.Id,stream=false},ct))await OllamaClient.Check(create,ct);await client.Load(tag,ct,8192);var answer=new StringBuilder();await client.Chat(new Conversation{Model=tag,ContextTokens=8192,OutputTokens=64,Messages=[new ChatMessage{Text="Reply exactly: CUSTOM_MODEL_OK"}]},t=>answer.Append(t),ct);if(!answer.ToString().Contains("CUSTOM_MODEL_OK"))throw new Exception("Custom model inference failed: "+answer);File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"model-live-results.txt"),"Custom model identity, loading and inference: passed.");}
  finally{foreach(var name in new[]{runtime,tag}){try{using var unload=await client.Post("/api/generate",new{model=name,keep_alive=0},CancellationToken.None);using var request=new HttpRequestMessage(HttpMethod.Delete,"/api/delete"){Content=new StringContent(System.Text.Json.JsonSerializer.Serialize(new{model=name}),Encoding.UTF8,"application/json")};using var deleted=await client.Http.SendAsync(request);}catch{}}}
 }
 public static async Task Run(CancellationToken ct)
 {
  var a=OllamaClient.RuntimeModel("custom:a");var b=OllamaClient.RuntimeModel("custom:b");if(a==b||a==OllamaClient.RuntimeModel(LocalModel.Large.Id)||a!=OllamaClient.RuntimeModel("custom:a"))throw new Exception("Custom runtime routing failed.");
  double value=0;var client=new OllamaClient(new Handler("{\"status\":\"pulling\",\"total\":100,\"completed\":50}\n{\"status\":\"success\"}\n"));await ModelDownloads.Pull(client,"custom:test",(_,v)=>value=Math.Max(value,v),ct);if(value!=50)throw new Exception("Download progress failed.");
  foreach(var bad in new[]{"{\"error\":\"test failure\"}\n","{\"status\":\"pulling\"}\n"}){bool failed=false;try{await ModelDownloads.Pull(new OllamaClient(new Handler(bad)),"custom:test",(_,_)=>{},ct);}catch{failed=true;}if(!failed)throw new Exception("Incomplete model download accepted.");}
  var settings=new DeskSettings();if(settings.StartInAgent||settings.LoadModelAtStartup||settings.Folder!=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))throw new Exception("Startup defaults failed.");
  await CheckMemoryBudget(ct);
  var text=new string('x',720)+"\n- Outcome: Edited the file.\n- Checks: Build passed.\n- Remaining: One failing test.";var preview=SummaryPreview.Create(text);if(!preview.Contains("One failing test")||preview.Length>700)throw new Exception("Summary omitted remaining work.");
 }

 sealed class MemoryHandler(string runtime) : HttpMessageHandler
 {
     public string? Unloaded { get; private set; }
     public int Reads { get; private set; }
     protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
     {
         if (request.RequestUri?.AbsolutePath == "/api/ps")
         {
             Reads++;
             return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
             {
                 models = new[]
                 {
                     new { name = "other-client:latest", size = 6L << 30, size_vram = 4L << 30 },
                     new { name = runtime, size = 8L << 30, size_vram = 5L << 30 }
                 }
             })) };
         }
         using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
         if (request.RequestUri?.AbsolutePath != "/api/generate" || body.RootElement.GetProperty("keep_alive").GetInt32() != 0)
             throw new InvalidOperationException("The budget issued an unexpected request.");
         Unloaded = body.RootElement.GetProperty("model").GetString();
         return new(HttpStatusCode.OK);
     }
 }

 static async Task CheckMemoryBudget(CancellationToken ct)
 {
     const string model = "budget:model";
     var runtime = OllamaClient.RuntimeModel(model);
     var handler = new MemoryHandler(runtime);
     var client = new OllamaClient(handler);
     await client.EnforceMemoryBudget(model, ct);
     if (handler.Reads != 0) throw new Exception("Disabled memory budgets contacted Ollama.");
     client.MemoryBudget = new(3, 5);
     await client.EnforceMemoryBudget(model, ct);
     if (handler.Unloaded != null) throw new Exception("A model at the memory budget was unloaded.");
     foreach (var caps in new[] { new ModelMemoryBudget(2, 0), new ModelMemoryBudget(0, 4) })
     {
         client.MemoryBudget = caps;
         try
         {
             await client.EnforceMemoryBudget(model, ct);
             throw new Exception("An over-budget model was allowed to start.");
         }
         catch (ModelMemoryBudgetException) when (handler.Unloaded == runtime) { }
     }
 }
}
