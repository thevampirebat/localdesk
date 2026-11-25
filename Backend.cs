using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalDesk;
public sealed partial class OllamaClient
{
    public static string RuntimeModel(string model)=>model==LocalModel.Fast.Id?"localdesk-qwen35-9b:current":model==LocalModel.Large.Id?"localdesk-qwen36-35b:current":"localdesk-custom-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(model))).ToLowerInvariant()[..16]+":current";
    public event Action<GenerationSpeed>? Speed;
    public event Action<InferenceTiming>? Inference;
    public event Action<ContextUsage>? ContextUsed;
    public event Action<string>? Status;
    public HttpClient Http { get; private set; }
    internal ModelMemoryBudget MemoryBudget { get; set; } = new(0, 0);
    public OllamaClient(HttpMessageHandler? handler=null){Http=handler==null?new HttpClient():new HttpClient(handler);Http.BaseAddress=new Uri("http://127.0.0.1:11434");Http.Timeout=Timeout.InfiniteTimeSpan;}
    private readonly ConcurrentDictionary<string,(DateTime Expires, HashSet<string> Values)> capabilityCache = new();
    public void ClearCapabilities() => capabilityCache.Clear();

    public async Task<IReadOnlySet<string>> Capabilities(string model, CancellationToken ct)
    {
        if (Compatible) return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chat" };
        if (capabilityCache.TryGetValue(model, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Values;
        using var response = await Post("/api/show", new { model }, ct);
        await Check(response, ct);
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var values = data?["capabilities"]?.AsArray()
            .Select(item => item?.ToString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (values.Count > 0) capabilityCache[model]=(DateTime.UtcNow.AddMinutes(5),values);
        return values;
    }

    private Process? ownedServer;

    internal static Process StartServer(string modelFolder, int port)
    {
        var exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ollama","ollama.exe");
        if(!File.Exists(exe))throw new IOException("Ollama is not installed on this PC.");
        var start = new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["OLLAMA_MODELS"] = modelFolder;
        start.Environment["OLLAMA_HOST"] = "127.0.0.1:" + port;
        start.Environment["OLLAMA_NOPRUNE"] = "true";
        var process = Process.Start(start) ?? throw new IOException("Could not start Ollama.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public async Task StopOwnedServer(CancellationToken ct)
    {
        if (ownedServer == null) return;
        if (!ownedServer.HasExited)
        {
            ownedServer.Kill(entireProcessTree: true);
            await ownedServer.WaitForExitAsync(ct);
        }
        ownedServer.Dispose();
        ownedServer = null;
    }

    public async Task Connect(CancellationToken ct)
    {
        if (Compatible || !Http.BaseAddress!.Equals(DefaultOrigin))
        {
            using var remote = await Http.GetAsync(ModelListPath, ct);
            remote.EnsureSuccessStatusCode();
            return;
        }
        try{using var r=await Http.GetAsync("/api/tags",ct);r.EnsureSuccessStatusCode();return;}catch(HttpRequestException){}
        if (ownedServer == null || ownedServer.HasExited)
        {
            ownedServer?.Dispose();
            ownedServer = StartServer(ModelStorage.CurrentFolder, 11434);
        }
        for(var i=0;i<30;i++){await Task.Delay(1000,ct);try{using var r=await Http.GetAsync("/api/tags",ct);r.EnsureSuccessStatusCode();return;}catch(HttpRequestException){}}
        throw new Exception("Could not connect to Ollama on this PC.");
    }
    public async Task Load(string model,CancellationToken ct,int contextTokens=32768)
    {
        await Connect(ct);
        if (Compatible)
        {
            if (MemoryBudget.Enabled) throw new ModelMemoryBudgetException("This connection does not report memory. Set both budgets to zero to use it.");
            return;
        }
        var runtime=RuntimeModel(model);
        using(var existing=await Post("/api/show",new{model=runtime},ct))
        {
            var parameters=existing.IsSuccessStatusCode?JsonNode.Parse(await existing.Content.ReadAsStringAsync(ct))?["parameters"]?.ToString()??"":"";
            if(!System.Text.RegularExpressions.Regex.IsMatch(parameters,@"(?m)^num_ctx\s+"+contextTokens+@"\s*$"))
            {
                if(existing.IsSuccessStatusCode){using var unload=await Post("/api/generate",new{model=runtime,keep_alive=0},ct);await Check(unload,ct);}

            }
        }
        using(var create=await Post("/api/create",new{model=runtime,from=model,stream=false,parameters=new{num_ctx=contextTokens}},ct))await Check(create,ct);
        using var load=await Post("/api/generate",new{model=runtime,keep_alive="10m",options=new{num_ctx=contextTokens}},ct);
        await Check(load,ct);
        await EnforceMemoryBudget(model, ct);
    }

    internal async Task<bool> EnforceMemoryBudget(string model, CancellationToken ct, bool allowUnloaded = false)
    {
        var budget = MemoryBudget;
        if (!budget.Enabled) return true;
        var runtime = RuntimeModel(model);
        var residents = await ResourceReader.Residents(Http, ct);
        var allocation = residents?.FirstOrDefault(entry => entry.Name.Equals(runtime, StringComparison.OrdinalIgnoreCase));
        if (allowUnloaded && residents != null && allocation == null) return false;
        var rejection = budget.Rejection(allocation);
        if (rejection == null) return true;
        using var unload = await Post("/api/generate", new { model = runtime, keep_alive = 0 }, ct);
        if (!unload.IsSuccessStatusCode) rejection += " Ollama could not unload the model.";
        throw new ModelMemoryBudgetException(rejection);
    }
    public async Task<HttpResponseMessage> Post(string route,object body,CancellationToken ct)=>await Http.SendAsync(new HttpRequestMessage(HttpMethod.Post,route){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")},HttpCompletionOption.ResponseHeadersRead,ct);
    public static async Task Check(HttpResponseMessage response,CancellationToken ct){if(!response.IsSuccessStatusCode){var body=await response.Content.ReadAsStringAsync(ct);throw new Exception(JsonNode.Parse(body)?["error"]?.ToString()??body);}}
    public async Task Chat(Conversation chat,Action<string> chunk,CancellationToken ct,bool? thinking = null)
    {
        if (Compatible) { await CompatibleChat(chat, chunk, ct); return; }
        var messages=new List<object>{new{role="system",content="You are a helpful local assistant. Be direct and accurate. Describe image details carefully. You have no file-editing tools in Chat mode; do not claim you changed files."}};
        if(chat.ContextSummary.Length>0)messages.Add(new{role="user",content="Carry-forward notes: "+chat.ContextSummary});
        if (chat.DocumentContext is { } sources) messages.Insert(0, new { role = "system", content = DocumentInstructions + sources });
        var recent=chat.Messages.Skip(chat.CompactedMessageCount).Where(m=>m.Text.Length>0||m.Images.Count>0).ToList();
        foreach(var m in recent){if(m.Role=="assistant"&&m.Text.Length==0)continue;messages.Add(new{role=m.Role,content=m.Text+AttachedFiles.Context(m.Files,false),images=m.Images.Where(File.Exists).Select(p=>Convert.ToBase64String(File.ReadAllBytes(p))).ToArray()});}
        var request=JsonSerializer.SerializeToNode(new{model=RuntimeModel(chat.Model),messages,stream=true,think=thinking??chat.Thinking,keep_alive="10m",options=new{num_ctx=chat.ContextTokens,num_predict=chat.OutputTokens,temperature=0.6,top_k=20,top_p=0.95,min_p=0,repeat_penalty=1}})!.AsObject();
        var context=new ContextCompaction(this){Chat=chat};context.Usage+=value=>ContextUsed?.Invoke(value);context.Status+=value=>Status?.Invoke(value);var promptEstimate=await context.Prepare(request,ct);
        using var response=await Post("/api/chat",request,ct);
        await Check(response,ct);using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var meter=new GenerationMeter(value=>Speed?.Invoke(value));
        var outputEstimate=0;var chunks=0;
        while(await reader.ReadLineAsync(ct) is {} line)
        {
            if(string.IsNullOrWhiteSpace(line))continue;var data=JsonNode.Parse(line);
            if(data?["error"]!=null)throw new Exception(data["error"]!.ToString());
            var text=data?["message"]?["content"]?.GetValue<string>();
            if(string.IsNullOrEmpty(text)&&!string.IsNullOrEmpty(data?["message"]?["thinking"]?.ToString()))meter.Tick();
            if(!string.IsNullOrEmpty(text)){meter.Tick();chunk(text);outputEstimate+=Math.Max(1,(int)Math.Ceiling(text.Length/2.5));if(++chunks%16==0)context.Report(promptEstimate+outputEstimate,true);}
            if(data?["done"]?.GetValue<bool>()==true)
            {
                var count=data["eval_count"]?.GetValue<int>();var ns=data["eval_duration"]?.GetValue<double>();
                meter.Complete(count,ns is >0?ns/1e9:null,ns is not >0);
                var readNs=data["prompt_eval_duration"]?.GetValue<double>();
                Inference?.Invoke(new((readNs??0)/1e9,(ns??0)/1e9,readNs==null||ns==null));
                context.Report((data["prompt_eval_count"]?.GetValue<int>()??0)+(count??0),false);
            }
        }
    }
}

public sealed class LocalBridge : IDisposable
{
    public event Action<ContextUsage>? ContextUsed;
    public ContextCompaction Window {get;}
    public event Action<GenerationSpeed>? Speed;
    public event Action<InferenceTiming>? Inference;
    public event Action<string>? Status;
    public Func<string, string?>? ArchiveToolOutput { get; set; }
    readonly HttpListener listener;readonly OllamaClient client;readonly CancellationTokenSource lifetime=new();
    public ConcurrentDictionary<string,List<string>> Images {get;}=new();
    public CancellationToken ActiveCancellation {get;set;}
    public string BaseUrl {get;}
    public ToolReviewGate? ReviewGate { get; set; }
    public bool AnswerOnly { get; set; }
    public bool Thinking { get; set; }
    public string Model {get;set;}=LocalModel.Fast.Id;
    public int OutputTokens {get;set;}=8192;
    public LocalBridge(OllamaClient client)
    {
        this.client=client;
        Window=new ContextCompaction(client);Window.Usage+=value=>ContextUsed?.Invoke(value);Window.Status+=value=>Status?.Invoke(value);
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var candidate = new HttpListener();
            var url = $"http://127.0.0.1:{port}/";
            candidate.Prefixes.Add(url);
            try { candidate.Start(); }
            catch (HttpListenerException error)
            {
                candidate.Close();
                if (attempt == 9 || error.ErrorCode != 183) throw;
                continue;
            }
            listener = candidate;
            BaseUrl = url + "v1";
            _ = Listen();
            break;
        }
    }
    async Task Listen(){while(!lifetime.IsCancellationRequested){try{var context=await listener.GetContextAsync();_=Forward(context);}catch when(lifetime.IsCancellationRequested){break;}}}
    async Task Forward(HttpListenerContext context)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,ActiveCancellation);var ct=linked.Token;
        var gate = ReviewGate;
        var answerOnly = AnswerOnly;
        try
        {
            string body;using(var reader=new StreamReader(context.Request.InputStream))body=await reader.ReadToEndAsync(ct);
            Func<JsonObject>? restoreRequest = null;
            int promptEstimate = 0;
            if(body.Length>0&&context.Request.Url!.AbsolutePath.Contains("chat/completions"))
            {
                var data=JsonNode.Parse(body)!.AsObject();data["model"]=OllamaClient.RuntimeModel(Model);data["think"]=Thinking;if(!Thinking)data["reasoning_effort"]="none";else data.Remove("reasoning_effort");data["max_tokens"]=Math.Min(data["max_tokens"]?.GetValue<int>()??OutputTokens,OutputTokens);data["temperature"]=0.6;data["top_p"]=0.95;data["top_k"]=20;data["min_p"]=0;
                LimitResearchTools(data, gate?.WebLimitReached == true, answerOnly);
                if(data["stream"]?.GetValue<bool>()==true)data["stream_options"]=new JsonObject{["include_usage"]=true};
                foreach(var message in data["messages"]?.AsArray()??[])
                {
                    if(message?["role"]?.ToString()!="user")continue;
                    var content=message["content"];var text=content is JsonValue?content.ToString():content?.ToJsonString()??"";
                    var matches=Images.Where(pair=>text.Contains("[LocalDeskImage:"+pair.Key+"]")).ToList();if(matches.Count==0)continue;
                    var blocks=new JsonArray();if(content is JsonArray old){foreach(var block in old)blocks.Add(block?.DeepClone());}else blocks.Add(new JsonObject{["type"]="text",["text"]=text});
                    foreach(var pair in matches)
                    {
                        foreach(var block in blocks)if(block?["type"]?.ToString()=="text")block["text"]=block["text"]!.ToString().Replace("[LocalDeskImage:"+pair.Key+"]","");
                        foreach(var path in pair.Value.Where(File.Exists)){var mime=Path.GetExtension(path).ToLowerInvariant() switch{".jpg" or ".jpeg"=>"image/jpeg",".webp"=>"image/webp",_=>"image/png"};blocks.Add(new JsonObject{["type"]="image_url",["image_url"]=new JsonObject{["url"]=$"data:{mime};base64,"+Convert.ToBase64String(File.ReadAllBytes(path))}});}
                    }
                    message!["content"]=blocks;
                }
                ToolOutputArchive.Clip(data,Window.Budget,ArchiveToolOutput??(_=>null));
                MessageCompatibility.Normalize(data);
                if (Window.ReduceDuplicateReads)
                {
                    var original = data.ToJsonString();
                    restoreRequest = () => JsonNode.Parse(original)!.AsObject();
                }
                else if (data["messages"] is JsonArray original)
                {
                    restoreRequest = () =>
                    {
                        if (!ReferenceEquals(data["messages"], original)) data["messages"] = original;
                        return data;
                    };
                }
                promptEstimate=await Window.Prepare(data,ct);body=data.ToJsonString();
            }
            var inferenceWatch=Stopwatch.StartNew();
            double? firstOutput=null;
            var inference=await SendWithRecovery(context.Request.HttpMethod,context.Request.RawUrl!,body,promptEstimate,ct,restoreRequest);
            using var response=inference.Response;
            context.Response.StatusCode=(int)response.StatusCode;context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            if(response.IsSuccessStatusCode&&response.Content.Headers.ContentType?.MediaType=="text/event-stream")
            {
                var meter=new GenerationMeter(value=>Speed?.Invoke(value));int? tokens=null;var outputEstimate=0;var chunks=0;
                var calls = new ToolReviewGate.StreamCalls();
                var withheld = new StringBuilder();
                async Task Write(string text)
                {
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text),ct);
                    await context.Response.OutputStream.FlushAsync(ct);
                }
                using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                while(await reader.ReadLineAsync(ct) is {} line)
                {
                    if(line.StartsWith("data: ")&&line!="data: [DONE]")
                    {
                        var data=JsonNode.Parse(line[6..]);
                        var choices=data?["choices"]?.AsArray();
                        if(gate!=null)calls.Add(choices);
                        var delta=choices?.FirstOrDefault()?["delta"];
                        if(delta is JsonObject fields&&fields.Any(p=>p.Key!="role"&&p.Value!=null&&p.Value.ToString().Length>0)){firstOutput??=inferenceWatch.Elapsed.TotalSeconds;meter.Tick();outputEstimate+=Math.Max(1,ContextCompaction.Estimate(delta)-4);if(++chunks%16==0)Window.Report(inference.PromptEstimate+outputEstimate,true);}
                        if(data?["usage"]?["completion_tokens"] is {} count)tokens=count.GetValue<int>();
                        if(data?["usage"]?["prompt_tokens"] is {} prompt)Window.Report(prompt.GetValue<int>()+(tokens??0),false);
                    }
                    if (calls.HasCalls) withheld.Append(line).Append('\n');
                    else await Write(line+"\n");
                }
                var inferenceSeconds=inferenceWatch.Elapsed.TotalSeconds;
                Inference?.Invoke(new(firstOutput??inferenceSeconds,firstOutput.HasValue?inferenceSeconds-firstOutput.Value:0,true));
                if (gate != null && calls.HasCalls)
                {
                    if (await gate.Allow(calls.Proposals(), ct, answerOnly)) await Write(withheld.ToString());
                    else
                    {
                        var stop = new { id = "localdesk-review", @object = "chat.completion.chunk", choices = new[]
                        {
                            new { index = 0, delta = new { content = gate.Blocked }, finish_reason = "stop" }
                        } };
                        await Write("data: "+JsonSerializer.Serialize(stop)+"\n\ndata: [DONE]\n\n");
                    }
                }
                meter.Complete(tokens);
            }
            else
            {
                var bytes=await response.Content.ReadAsByteArrayAsync(ct);
                if(response.IsSuccessStatusCode&&context.Request.Url!.AbsolutePath.Contains("chat/completions"))
                {
                    var data=JsonNode.Parse(bytes);
                    Inference?.Invoke(new(inferenceWatch.Elapsed.TotalSeconds,0,true));
                    if(data?["usage"]?["prompt_tokens"] is {} prompt)Window.Report(prompt.GetValue<int>()+(data["usage"]?["completion_tokens"]?.GetValue<int>()??0),false);
                    if(gate!=null && data?["choices"] is JsonArray choices)
                    {
                        var proposed=choices.SelectMany(choice=>choice?["message"]?["tool_calls"] is JsonArray calls ? ToolReviewGate.ReadCalls(calls) : []);
                        if(!await gate.Allow(proposed,ct,answerOnly))
                        {
                            foreach(var choice in choices.OfType<JsonObject>())
                            {
                                if(choice["message"] is JsonObject message){message.Remove("tool_calls");message["content"]=gate.Blocked;}
                                choice["finish_reason"]="stop";
                            }
                            bytes=Encoding.UTF8.GetBytes(data.ToJsonString());
                        }
                    }
                }
                await context.Response.OutputStream.WriteAsync(bytes,ct);
            }
        }
        catch(Exception e){try{context.Response.StatusCode=502;var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{error=new{message=e is OperationCanceledException?"Stopped":e.Message}}));await context.Response.OutputStream.WriteAsync(bytes);}catch{}}
        finally{try{context.Response.Close();}catch{}}
    }

    internal static void LimitResearchTools(JsonObject request, bool webLimitReached, bool answerOnly)
    {
        if (!webLimitReached && !answerOnly) return;
        if (!answerOnly && request["tools"] is JsonArray tools)
        {
            for (int i = tools.Count - 1; i >= 0; i--)
                if (tools[i]?["function"]?["name"]?.ToString() == ToolReviewGate.WebFetchTool) tools.RemoveAt(i);
            if (tools.Count == 0) request.Remove("tools");
        }
        if (answerOnly)
        {
            request.Remove("tools");
            request.Remove("parallel_tool_calls");
            request["think"] = false;
            request["reasoning_effort"] = "none";
        }
        request.Remove("tool_choice");
        if (request["messages"] is JsonArray messages)
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = answerOnly
                ? "Write the user-facing final answer in your normal response text now. Use only the work and sources already available. No tool calls. State what you could not verify or complete."
                : "The web research budget is exhausted. Do not request any more web pages or use commands to search. Use the evidence already collected, cite only relevant sources you actually opened, and say what you could not verify." });
    }
    async Task<(HttpResponseMessage Response, int PromptEstimate)> SendWithRecovery(string method,string route,string body,
        int promptEstimate,CancellationToken ct,Func<JsonObject>? restoreRequest)
    {
        // Retry only a rejected inference request, before any response/tool call reaches Cline.
        // Never replay a session turn or retry a partially delivered stream.
        for(var attempt=0;;attempt++)
        {
            using var request=new HttpRequestMessage(new HttpMethod(method),route);
            if(body.Length>0)request.Content=new StringContent(body,Encoding.UTF8,"application/json");
            var response=await client.Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(response.IsSuccessStatusCode||attempt>0||!route.Contains("chat/completions"))return (response,promptEstimate);
            var error=await response.Content.ReadAsStringAsync(ct);
            var templateError=error.Contains("No user query found",StringComparison.OrdinalIgnoreCase);
            var overflow=error.Contains("exceed_context_size_error",StringComparison.OrdinalIgnoreCase)||error.Contains("exceeds the available context",StringComparison.OrdinalIgnoreCase);
            if(!overflow&&!templateError&&!error.Contains("invalid tool call arguments",StringComparison.OrdinalIgnoreCase))return (response,promptEstimate);
            response.Dispose();ct.ThrowIfCancellationRequested();
            if(overflow)
            {
                var retry=restoreRequest?.Invoke()??JsonNode.Parse(body)!.AsObject();
                promptEstimate=await Window.Prepare(retry,ct,true);body=retry.ToJsonString();continue;
            }
            Status?.Invoke(templateError?"The model rejected the message format. Retrying this model request once…":"The model returned an incomplete tool command. Retrying this model request once…");
            var data=JsonNode.Parse(body)!.AsObject();data["temperature"]=0.2;
            if(templateError)data["messages"]!.AsArray().Add(new JsonObject{["role"]="user",["content"]="Continue the existing task from the conversation and latest tool results. Follow the system instructions."});
            else data["messages"]!.AsArray().Insert(0,new JsonObject{["role"]="system",["content"]="Use short, complete tool calls with valid JSON arguments. Use paths relative to the selected project. Read only relevant line ranges, avoid large directory listings, and split large edits into small patches."});
            MessageCompatibility.Normalize(data);promptEstimate=Window.Count(data);body=data.ToJsonString();
        }
    }
    public void Dispose(){lifetime.Cancel();listener.Close();}
}

public sealed class AgentClient : IDisposable
{
    Process? process;StreamWriter? writer;long nextId;
    readonly ConcurrentDictionary<long,TaskCompletionSource<JsonElement>> pending=new();
    public event Action<string,string>? Update;
    public event Action<double>? ToolFinished;
    public event Action<string>? ToolCheckpoint;
    private Action<JsonElement>? observeTool;
    private bool guarded;
    public sealed class LoopException(string reason) : Exception(reason);
    string? sessionId;bool replaying;
    public string? SessionId=>sessionId;
    public string Key {get;private set;}="";
    public async Task Start(Conversation chat,LocalBridge bridge,string dataRoot,CancellationToken ct)
    {
        Dispose();bridge.Model=chat.Model;guarded=bridge.ReviewGate!=null;bridge.OutputTokens=chat.OutputTokens;bridge.Window.Chat=chat;Key=chat.Id+"|"+chat.Model+"|"+chat.Project+"|"+chat.ContextTokens+"|"+chat.OutputTokens;
        var state=Path.Combine(dataRoot,"cline");Directory.CreateDirectory(Path.Combine(state,"settings"));
        var settings=new{version=1,lastUsedProvider="openai-compatible",modes=new{},providers=new Dictionary<string,object>{["openai-compatible"]=new{settings=new{provider="openai-compatible",apiKey="local-only",model=chat.Model,baseUrl=bridge.BaseUrl,contextWindow=chat.ContextTokens,maxTokens=chat.OutputTokens,capabilities=new[]{"tools","vision","streaming"},reasoning=new{enabled=false,effort="none"}},tokenSource="manual",updatedAt=DateTime.UtcNow.ToString("O")}}};
        File.WriteAllText(Path.Combine(state,"settings","providers.json"),JsonSerializer.Serialize(settings));
        var exe=Path.Combine(AppContext.BaseDirectory,"runtime","cline.exe");if(!File.Exists(exe))throw new Exception("Cline runtime is missing from the runtime folder.");
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=chat.Project};
        foreach(var arg in new[]{"--acp","--provider","openai-compatible","--model",chat.Model,"--data-dir",state,"--thinking","none","--auto-approve","true"})start.ArgumentList.Add(arg);
        start.Environment["CLINE_PROVIDER"]="openai-compatible";start.Environment["CLINE_MODEL"]=chat.Model;
        start.Environment["CLINE_API_KEY"]="local-only";
        start.Environment["CLINE_PROVIDER_SETTINGS_PATH"]=Path.Combine(state,"settings","providers.json");
        start.Environment["CLINE_DATA_DIR"]=state;start.Environment["CLINE_DIR"]=state;
        process=Process.Start(start)??throw new Exception("Could not start the local agent.");var owner=process;writer=process.StandardInput;writer.AutoFlush=true;
        _=ReadLoop(owner);_=Task.Run(async()=>{try{while(await owner.StandardError.ReadLineAsync() is {} line){if(ReferenceEquals(process,owner)&&line.Contains("Error",StringComparison.OrdinalIgnoreCase))Update?.Invoke("diagnostic",line);}}catch{}});
        await Request("initialize",new{protocolVersion=1,clientInfo=new{name="LocalDesk",version="1.0.0"},clientCapabilities=new{}},ct);
        if(chat.AgentSession!=null)
        {
            try{replaying=true;await Request("session/load",new{sessionId=chat.AgentSession,cwd=chat.Project,mcpServers=Array.Empty<object>()},ct);sessionId=chat.AgentSession;await SelectModel(chat.Model,ct);return;}catch(Exception e) when(e is not OperationCanceledException){Update?.Invoke("status","Starting a fresh project session.");}finally{replaying=false;}
        }
        var created=await Request("session/new",new{cwd=chat.Project,mcpServers=Array.Empty<object>()},ct);sessionId=created.GetProperty("sessionId").GetString();chat.AgentSession=sessionId;
        await SelectModel(chat.Model,ct);
    }
    Task<JsonElement> SelectModel(string model,CancellationToken ct)=>Request("session/set_model",new{sessionId,modelId=model},ct);
    async Task ReadLoop(Process owner)
    {
        try{while(await owner.StandardOutput.ReadLineAsync() is {} line)
        {
            if(!ReferenceEquals(process,owner))return;if(!line.StartsWith('{'))continue;
            using var document=JsonDocument.Parse(line);var root=document.RootElement;
            if(root.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.Number&&pending.TryRemove(id.GetInt64(),out var completion))
            {
                if(root.TryGetProperty("error",out var error))completion.TrySetException(new Exception(error.TryGetProperty("message",out var text)?text.GetString():error.ToString()));else completion.TrySetResult(root.GetProperty("result").Clone());
            }
            else if(root.TryGetProperty("method",out var method))
            {
                if(method.GetString()=="session/update")
                {
                    if(replaying)continue;var update=root.GetProperty("params").GetProperty("update");var kind=update.GetProperty("sessionUpdate").GetString();
                    if(kind is "tool_call" or "tool_call_update") observeTool?.Invoke(update);
                    if(kind=="agent_message_chunk"&&update.TryGetProperty("content",out var content)&&content.TryGetProperty("text",out var text))Update?.Invoke("text",text.GetString()??"");
                    else if(kind=="tool_call")
                    {
                        Update?.Invoke("tool",update.TryGetProperty("title",out var title)?title.GetString()??"Working":"Working");
                        if(update.TryGetProperty("kind",out var toolKind)&&toolKind.GetString()=="edit"&&update.TryGetProperty("locations",out var locations))foreach(var location in locations.EnumerateArray())if(location.TryGetProperty("path",out var path))Update?.Invoke("file",path.GetString()??"");
                    }
                    else if(kind=="tool_call_update"&&update.TryGetProperty("status",out var status)&&status.GetString()=="failed")Update?.Invoke("diagnostic","A tool reported an error; the agent may correct it.");
                }
                else if(root.TryGetProperty("id",out var requestId))
                {
                    object result=new{};
                    if(method.GetString()=="session/request_permission")
                    {
                        if(guarded) { await writer!.WriteLineAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",id=requestId.Clone(),result=new{outcome=new{outcome="cancelled"}}})); continue; }
                        var options=root.GetProperty("params").GetProperty("options");var option=options.EnumerateArray().FirstOrDefault(x=>x.GetProperty("kind").GetString()=="allow_once");
                        result=option.ValueKind==JsonValueKind.Undefined?new{outcome=new{outcome="cancelled"}}:(object)new{outcome=new{outcome="selected",optionId=option.GetProperty("optionId").GetString()}};
                    }
                    await writer!.WriteLineAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",id=requestId.Clone(),result}));
                }
            }
        }}catch(Exception e){if(ReferenceEquals(process,owner))foreach(var item in pending.Values)item.TrySetException(e);}finally{if(ReferenceEquals(process,owner))foreach(var item in pending.Values)item.TrySetException(new Exception("The local agent stopped."));}
    }
    async Task<JsonElement> Request(string method,object parameters,CancellationToken ct)
    {
        var id=Interlocked.Increment(ref nextId);var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=completion;
        using var registration=ct.Register(()=>completion.TrySetCanceled(ct));
        try{await writer!.WriteLineAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",id,method,@params=parameters}));return await completion.Task;}finally{pending.TryRemove(id,out _);}
    }
    public async Task Send(string prompt, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = new ToolLoop();
        var completed = new HashSet<string>();
        var toolClocks = new Dictionary<string,long>();
        var descriptions = new Dictionary<string,string>();
        string? reason = null;
        observeTool = update =>
        {
            if (update.GetProperty("sessionUpdate").GetString() == "tool_call")
            {
                var title = update.TryGetProperty("title", out var heading) ? heading.GetString() : null;
                var input = update.TryGetProperty("rawInput", out var raw) &&
                    raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? raw.GetRawText() : null;
                var paths = update.TryGetProperty("locations", out var locations) ? locations.GetRawText() : null;
                // Generic titles without any arguments cannot distinguish unrelated calls.
                var signature = (input != null && input != "{}") || (title != null && title.Contains(':') && !title.EndsWith("...") && !title.EndsWith("…")) ? title + "|" + input + "|" + paths : null;
                reason ??= loop.Observe(signature);
            }
            if(update.TryGetProperty("toolCallId",out var toolId)&&toolId.GetString() is { } id)
            {
                toolClocks.TryAdd(id,Stopwatch.GetTimestamp());
                if(update.TryGetProperty("title",out var toolTitle) && toolTitle.GetString() is { } label)descriptions[id]=label;
                if(update.TryGetProperty("status",out var status)&&status.GetString() is "completed" or "failed"&&completed.Add(id))
                {
                    ToolFinished?.Invoke(Stopwatch.GetElapsedTime(toolClocks[id]).TotalSeconds);
                    ToolCheckpoint?.Invoke(status.GetString()+" · "+descriptions.GetValueOrDefault(id,"tool"));
                    reason??=loop.Outcome(status.GetString()!);
                }
            }
            if (reason != null)
            {
                try { request.Cancel(); } catch (ObjectDisposedException) { }
            }
        };
        try
        {
            var result = await Request("session/prompt", new { sessionId, prompt = new[] { new { type = "text", text = prompt } } }, request.Token);
            if (result.TryGetProperty("stopReason", out var stop))
            {
                if (stop.GetString() == "cancelled") throw new OperationCanceledException();
                if (stop.GetString() is "max_turn_requests" or "max_tokens")
                    throw new Exception("The agent reached its step or response limit before finishing.");
            }
        }
        catch (OperationCanceledException) when (reason != null && !ct.IsCancellationRequested)
        {
            throw new LoopException(reason);
        }
        finally { observeTool = null; }
    }
    public void Dispose(){try{if(process is {HasExited:false})process.Kill(true);}catch{}process=null;writer=null;Key="";sessionId=null;foreach(var c in pending.Values)c.TrySetCanceled();pending.Clear();}
}
