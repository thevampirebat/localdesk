namespace LocalDesk;

static class ProficiencyChecks
{
    public static async Task Run(string area,CancellationToken ct)
    {
        string root=Directory.CreateDirectory(Path.Combine(area,"proficiency-"+Guid.NewGuid().ToString("N"))).FullName;
        var layout=Path.Combine(root,"Page.axaml");
        var handler=Path.Combine(root,"Page.axaml.cs");
        var view=Path.Combine(root,"ViewModel.cs");
        File.WriteAllText(layout,new string(' ',2500)+"""<Button Content="Other button" Click="OtherHandler"/><Button Content="Refresh inventory" Click="RunInventory" Command="{Binding QueueWork}"/>""");
        File.WriteAllText(handler,"private void RunInventory() => model.LoadItems();");
        File.WriteAllText(view,"public void LoadItems() { }");
        var hints=CodeFocus.Find(root,area,"Fix the \"Refresh inventory\" button",[],ct);
        if(!hints.Contains("Page.axaml:1")||!hints.Contains("Page.axaml.cs:1")||!hints.Contains("ViewModel.cs:1"))
            throw new Exception("UI source search lost the click handler or called member: "+hints);
        if(CodeFocus.Find(root,root,"Fix the \"Refresh inventory\" button",[],ct).Length!=0)
            throw new Exception("Code search widened into the default broad folder.");
        if(!(await Verification.CheckText(root,new("Text present","Page.axaml","Refresh inventory"),ct)).Passed ||
            (await Verification.CheckText(root,new("Text absent","Page.axaml","Refresh inventory"),ct)).Passed ||
            (await Verification.CheckText(root,new("Text present","..\\outside.txt","a"),ct)).Passed)
            throw new Exception("Focused verification missed an exact assertion or path boundary.");

        var baseline=new ProjectEvidence(root,area,[]);
        File.AppendAllText(layout,"<Button Content=\"Old label changed accidentally\" />");
        var observed=baseline.FocusedEditExcerpt(layout);
        if(!observed.Contains("Old label changed accidentally"))
            throw new Exception("Repair feedback did not show the actual edit in markup.");
        var log=Path.Combine(root,"build.log");
        File.WriteAllLines(log,Enumerable.Repeat("Program.cs(1,15): error CS1525: Invalid expression term ';'",12)
            .Concat(Enumerable.Range(0,12).Select(i=>$"Other.cs({i},1): error CS1002: ; expected")));
        var errors=await Verification.Errors(log,ct);
        if(errors.Split('\n').Length!=8||errors.Split('\n').Count(line=>line.Contains("CS1525"))!=1 || Verification.MaxRepairs!=2)
            throw new Exception("Error feedback was not deduplicated and capped.");
        string results=Directory.CreateDirectory(Path.Combine(root,"results")).FullName;
        if(Verification.TestCounts(results).Passed!=0)throw new Exception("No test results were accepted as a pass.");
        File.WriteAllText(Path.Combine(results,"check.trx"),"""<TestRun><ResultSummary><Counters total="1" executed="1" passed="1" failed="0"/></ResultSummary></TestRun>""");
        if(Verification.TestCounts(results)!=(1,0))throw new Exception("TRX pass counts were not read.");
    }

    public static async Task AchievementEdits(string area,string source,CancellationToken ct)
    {
        var parent=Directory.CreateDirectory(Path.Combine(area,"labs-edits-"+Guid.NewGuid().ToString("N"))).FullName;
        var project=Path.Combine(parent,"source");
        ModelComparison.CopyProject(source,project,ct);
        const string relative="src/AchievementLabs.Desktop/MainWindow.axaml";
        const string target="src/AchievementLabs.Desktop/AchievementLabs.Desktop.csproj";
        var layout=Path.Combine(project,relative);
        string expected=File.ReadAllText(layout);
        var client=new OllamaClient();
        await client.Load(LocalModel.Fast.Id,ct,16384);
        using var bridge=new LocalBridge(client){ActiveCancellation=ct};
        using var agent=new AgentClient();
        var chat=new Conversation{Project=project,Mode="agent",ContextTokens=16384,OutputTokens=2048};
        bool Accept(ToolProposal proposal)
        {
            if(proposal.Name!="editor")return false;
            var path=System.Text.Json.Nodes.JsonNode.Parse(proposal.Arguments)?["path"]?.ToString();
            if(string.IsNullOrWhiteSpace(path))return false;
            return Path.GetFullPath(path,project).Equals(Path.GetFullPath(layout),StringComparison.OrdinalIgnoreCase);
        }
        bridge.ReviewGate=new ToolReviewGate((proposal,_)=>Task.FromResult(Accept(proposal)),project);
        await agent.Start(chat,bridge,Path.Combine(parent,"state"),ct);
        foreach(var (oldLabel,newLabel,handler) in new[]{
            ("Check for updates","Check for app updates","CheckUpdates"),
            ("Auto Unlocker","Xbox Auto Unlocker","ShowQueues")})
        {
            var oldMarkup=$"Content=\"{oldLabel}\" Click=\"{handler}\"";
            var newMarkup=$"Content=\"{newLabel}\" Click=\"{handler}\"";
            var task=$"Rename the \"{oldLabel}\" control hooked to {handler} to \"{newLabel}\".";
            var sourceMatches=CodeFocus.Find(project,area,$"Find the \"{oldLabel}\" button handler",[],ct);
            expected=ModelComparison.ReplaceOnce(expected,oldMarkup,newMarkup);
            await agent.Send(task+" In "+relative+", replace the exact attribute pair "+oldMarkup+" with "+newMarkup+
                ". Use the editor tool only. Preserve all unrelated text. Do not run builds or commands.\n"+sourceMatches,ct);
            var check=await Verification.CheckText(project,new("Text present",relative,newMarkup),ct);
            if(!check.Passed||File.ReadAllText(layout)!=expected)
                throw new Exception("AchievementLabs edit check failed for "+handler+" · "+bridge.ReviewGate.Blocked);
            File.AppendAllText(Path.Combine(parent,"progress.txt"),"Verified exact requested edit to "+handler+"\n");
        }
        var result=await ProjectEvidence.Build(Path.Combine(project,target),project,Path.Combine(parent,"build.log"),ct);
        if(result.ExitCode!=0)throw new Exception("Isolated AchievementLabs build failed. "+result.LogPath);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"achievement-edits-passed.txt"),
            "Both isolated AchievementLabs label edits matched the exact expected source and passed the desktop Release build. "+result.LogPath);
    }

    public static async Task Live(string area,string achievement,CancellationToken ct)
    {
        var tests=new[]{
            ("Find the \"Check for updates\" button handler","CheckUpdates"),
            ("Find the \"Auto Unlocker\" menu handler","ShowQueues")
        };
        foreach(var (task,method) in tests)
        {
            var hits=CodeFocus.Find(achievement,area,task,[],ct);
            if(!hits.Contains("MainWindow.axaml.cs:")||!hits.Contains(method+"("))
                throw new Exception("AchievementLabs search did not surface "+method+": "+hits);
        }

        var root=Directory.CreateDirectory(Path.Combine(area,"live-repair-"+Guid.NewGuid().ToString("N"))).FullName;
        var path=Path.Combine(root,"Program.cs");
        var project=Path.Combine(root,"Repair.csproj");
        var broken="string label = ;\nSystem.Console.WriteLine(label);\n";
        File.WriteAllText(path,broken);
        File.WriteAllText(project,"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>""");
        var check=new FocusedCheck("Text present","Program.cs","string label = \"REPAIR_OK\";");
        var failed=await ProjectEvidence.Build(project,root,Path.Combine(root,"failed.log"),ct);
        var feedback=await Verification.Errors(failed.LogPath,ct);
        if(failed.ExitCode==0||!feedback.Contains("CS1525")||(await Verification.CheckText(root,check,ct)).Passed)
            throw new Exception("The live fixture did not reproduce a compiler error and failed assertion.");
        var client=new OllamaClient();
        await client.Load(LocalModel.Fast.Id,ct,16384);
        var chat=new Conversation { Project=root,Mode="agent",ContextTokens=16384,OutputTokens=2048 };
        using var bridge=new LocalBridge(client){ActiveCancellation=ct};
        int edits=0;
        bridge.ReviewGate=new ToolReviewGate((call,_) =>
        {
            if(call.Name is not ("editor" or "write_to_file" or "replace_in_file"))return Task.FromResult(false);
            edits++;
            return Task.FromResult(true);
        },root);
        using var agent=new AgentClient();
        await agent.Start(chat,bridge,Path.Combine(area,"repair-state-"+Guid.NewGuid().ToString("N")),ct);
        await agent.Send("Fix this isolated C# exercise using the editor tool only. In Program.cs initialize label to \"REPAIR_OK\". Preserve the Console.WriteLine. Do not run tests or commands. LocalDesk will recheck. Compiler diagnostics:\n"+feedback,ct);
        var passed=await ProjectEvidence.Build(project,root,Path.Combine(root,"rechecked.log"),ct);
        var assertion=await Verification.CheckText(root,check,ct);
        if(edits==0||passed.ExitCode!=0||!assertion.Passed)throw new Exception("Live repair did not pass: "+bridge.ReviewGate.Blocked);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"proficiency-live.txt"),
            "Two AchievementLabs source lookups returned the real click handlers. A gated 9B repair fixed CS1525 and passed both the Release build and exact text assertion.");
    }
}
