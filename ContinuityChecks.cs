using System.Text.Json.Nodes;

namespace LocalDesk;

static class ContinuityChecks
{
    public static async Task Run(string area,CancellationToken ct)
    {
        string root=Path.Combine(area,"continuity-"+Guid.NewGuid().ToString("N"));
        string original=Directory.CreateDirectory(Path.Combine(root,"bin","source")).FullName;
        string data=Path.Combine(root,"data");
        string note=Path.Combine(original,"Guide.md");
        string conflict=Path.Combine(original,"Conflict.txt");
        string gone=Path.Combine(original,"Remove.txt");
        File.WriteAllText(note,"Original guide");
        File.WriteAllText(conflict,"Original text");
        File.WriteAllText(gone,"Keep an original backup");
        var chat=new Conversation{Mode="agent",Project=original};
        var copy=ChatBranching.Copy(chat,0,original,data,ct).BranchFolder!;
        var origin=BranchApply.ReadOrigin(copy);
        File.WriteAllText(Path.Combine(copy,"Guide.md"),"New guide");
        File.WriteAllText(Path.Combine(copy,"Conflict.txt"),"Branch text");
        File.WriteAllText(Path.Combine(copy,"New.txt"),"New file");
        File.Delete(Path.Combine(copy,"Remove.txt"));
        File.WriteAllText(conflict,"Newer original");
        var compare=BranchApply.Compare(copy,origin,ct);
        if(compare.Count!=4 || compare.Single(f=>f.Relative=="Conflict.txt").Conflict==null)
            throw new Exception("Branch comparison missed a branch edit or original conflict.");
        string backup=Path.Combine(data,"test-backups");
        var guide=compare.Single(f=>f.Relative=="Guide.md");
        File.WriteAllText(note,"Changed during review");
        try { BranchApply.Apply(copy,origin,guide,backup);throw new Exception("A late conflict was overwritten."); }
        catch(IOException) { }
        File.WriteAllText(note,"Original guide");
        BranchApply.Apply(copy,origin,guide,backup);
        BranchApply.Apply(copy,origin,compare.Single(f=>f.Relative=="Remove.txt"),backup);
        BranchApply.Apply(copy,origin,compare.Single(f=>f.Relative=="New.txt"),backup);
        if(File.ReadAllText(note)!="New guide"||File.Exists(gone)||File.ReadAllText(Path.Combine(original,"New.txt"))!="New file"||
            File.ReadAllText(Path.Combine(backup,"Guide.md"))!="Original guide"||File.ReadAllText(conflict)!="Newer original")
            throw new Exception("Selected branch files did not apply with an intact backup.");

        var journal=new RunCheckpoint{Project=original,Reply="reply",Request="request",LastTool="completed · editor",
            AgentDone=true,BuildTarget="sample.csproj",Build=new BuildResult(0,"build.log")};
        RunJournal.Save(data,"chat",journal);
        var baseline=RunJournal.Baseline(data,"chat",journal.Reply);
        new ProjectEvidence(original,root,[]).SaveBaseline(baseline);
        File.WriteAllText(note,"Interrupted edit");
        var restored=RunJournal.Load(data,"chat");
        if(restored is not {AgentDone:true,Build.ExitCode:0,LastTool:"completed · editor"} ||
            ProjectEvidence.Restore(baseline,original).Finish().Single().Path!=note)
            throw new Exception("Recovery lost its last action, finished build, or on-disk edit.");
        RunJournal.Complete(data,"chat",journal);
        if(RunJournal.Load(data,"chat")!=null || File.Exists(baseline))throw new Exception("Settled recovery data was left active.");

        var lib=new ProjectLibrary(data);
        var project=lib.Remember(original);
        project.Instructions="Use the release build.";
        project.PinnedFiles=["Guide.md","..\\outside.txt"];
        File.WriteAllText(Path.Combine(root,"outside.txt"),"EXTERNAL CONTENT");
        lib.Save();
        var brief=ProjectBrief.Build(new ProjectLibrary(data).Find(original),original,16384);
        if(!brief.Contains("Use the release build.") || !brief.Contains("Interrupted edit") || brief.Contains("EXTERNAL CONTENT"))
            throw new Exception("Saved instructions or pinned-file scope were not respected.");

        if(ResourceReader.Ram() is not { Total:>0 })throw new Exception("Windows RAM probe failed.");
        var gpus=ResourceReader.ParseGpus("NVIDIA GeForce RTX 5060 Ti, 1024, 16311");
        if(gpus is not [{ UsedMiB:1024, TotalMiB:16311 }])throw new Exception("VRAM parser failed.");
        var active=ResourceReader.ParseResidents(JsonNode.Parse("""{"models":[{"name":"test","size":1048576,"size_vram":524288,"context_length":8192}]}"""));
        if(active is not [{ VramBytes:524288, Context:8192 }])throw new Exception("Ollama memory parser failed.");
        await Task.CompletedTask;
    }
}
