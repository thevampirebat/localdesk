using System.Net;
using System.Text.Json.Nodes;

namespace LocalDesk;

internal static class ExperienceUpgradeChecks
{
    internal static async Task Run(string baseFolder,CancellationToken ct)
    {
        var root=Path.Combine(baseFolder,"experience-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source=Path.Combine(root,"Window.cs");
        await File.WriteAllTextAsync(source,"using System;\nconst string Caption = \"AcceptanceFixture\";",ct);
        var index=new ProjectSearchIndex(root);
        Require(index.Search("AcceptanceFixture",ct).Single().Line==2,"Index lost the source line.");
        await File.WriteAllTextAsync(source,"using System;\nconst string Caption = \"ChangedFixture\";",ct);
        Require(index.Search("AcceptanceFixture",ct).Count==0&&index.Search("ChangedFixture",ct).Single().Line==2,"Index retained stale file content.");
        var preview=await PreviewFiles.ReadAround(source,2,ct);
        Require(preview.Contains("> 2  const string"),"Cited line was missing from the preview.");

        var body=JsonNode.Parse("""{"messages":[{"role":"system","content":"rules"},{"role":"user","content":"a query\n\nAttached file: a.txt\nreadable content"},{"role":"tool","content":"test output"}],"tools":[{"name":"editor"}]}""")!.AsObject();
        var breakdown=ContextCompaction.Measure(body,512,3000);
        Require(breakdown.Files>0&&breakdown.ToolResults>0&&breakdown.Conversation>0&&breakdown.Definitions>0&&breakdown.Reserved==512,"Context categories were missing.");
        using var backend=new FixtureBackend();
        using var http=new HttpClient(backend);
        var web=await ResearchSearch.Find("Brave Search","fixture query","fixture-key",ct,http);
        Require(web.Count==1&&web[0].Title=="Useful result"&&web[0].Summary=="Verified snippet"&&backend.BraveQueryAuthorized,"Web search failed to bind request and citations.");
        var wiki=await ResearchSearch.Find("Wikipedia","fixture query",null,ct,http);
        Require(wiki.Single().Url.AbsoluteUri=="https://en.wikipedia.org/wiki/Fixture_topic","Wikipedia citation path was lost.");
        var model=new OllamaClient(backend);
        var probe=await GpuBenchmark.Measure(model,LocalModel.Fast.Id,8192,ct);
        Require(probe.Context==8192&&probe.Rate==48&&probe.Repeat>=0&&backend.StreamingProbes==2,"The benchmark lost timing or throughput.");
        var conversation=new Conversation{Messages=[new ChatMessage{Text="Context fixture"}]};
        var compaction=new ContextCompaction(model){Chat=conversation};
        var notes=await compaction.PreviewManual(ct);
        Require(notes=="Keeps the existing goal."&&conversation.CompactedMessageCount==0,"Compaction preview mutated the chat.");
        compaction.ApplyManual(notes);
        Require(conversation.CompactedMessageCount==1&&conversation.ContextSummary==notes,"Confirmed notes were not applied.");
    }

    static void Require(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(reason);
    }

    sealed class FixtureBackend:HttpMessageHandler
    {
        public bool BraveQueryAuthorized;
        public int StreamingProbes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            string body=path switch
            {
                "/api/tags" or "/api/ps"=>"{\"models\":[]}",
                "/api/chat"=>"{\"message\":{\"content\":\"Keeps the existing goal.\"}}",
                "/res/v1/web/search"=>"""{"web":{"results":[{"url":"https://example.com/manual","title":"<b>Useful</b> result","description":"Verified snippet"},{"url":"file:///private","title":"Excluded"}]}}""",
                "/w/api.php"=>"""{"query":{"search":[{"title":"Fixture topic","snippet":"A sourced excerpt"}]}}""",
                _=>"{}"
            };
            if(path=="/res/v1/web/search")BraveQueryAuthorized=request.Headers.GetValues("X-Subscription-Token").Single()=="fixture-key"&&request.RequestUri.Query.Contains("fixture%20query");
            if(path=="/api/generate"&&request.Content!=null&&JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))?["stream"]?.GetValue<bool>()==true)
            {
                StreamingProbes++;
                body="{\"response\":\"fixture\"}\n{\"done\":true,\"eval_count\":48,\"eval_duration\":1000000000}\n";
            }
            return new(HttpStatusCode.OK){Content=new StringContent(body)};
        }
    }
}
