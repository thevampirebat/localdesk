using System.Text;
using System.Text.RegularExpressions;

namespace LocalDesk;

internal static class CodeFocus
{
    static readonly Regex Identifier=new(@"\b[A-Za-z_][A-Za-z_0-9]{3,}\b",RegexOptions.Compiled);
    static readonly Regex Quoted=new("[\"“]([^\"”\\r\\n]{4,80})[\"”]",RegexOptions.Compiled);
    static readonly Regex UiLink=new(@"(?:\bClick\s*=\s*""|\{Binding\s+)([A-Za-z_][A-Za-z_0-9]{3,})",RegexOptions.Compiled);
    static readonly Regex MemberCall=new(@"\b(?:model|viewModel|_viewModel|_model)\.([A-Za-z_][A-Za-z_0-9]{3,})\s*\(",RegexOptions.Compiled);
    static readonly HashSet<string> Extensions=new([".cs",".fs",".vb",".axaml",".xaml",".ts",".tsx",".js",".jsx",".py",".rs",".go"],StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Generic=new(["please","update","change","button","when","should","with","from","this","that","app","code",
        "test","work","make","does","doesn","click","currently","adding","remove","removing","running","ensure","shows","show","using"],StringComparer.OrdinalIgnoreCase);
    private sealed record Lines(string Path,string[] Text);
    private sealed record Hit(string Path,int Line,string Text,int Score);

    public static string Find(string root,string fallback,string prompt,IEnumerable<string> attached,CancellationToken ct)
    {
        var terms=Identifier.Matches(prompt).Select(m=>m.Value).Where(word=>!Generic.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        var phrases=Quoted.Matches(prompt).Select(m=>m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if(terms.Length==0&&phrases.Length==0)return "";
        var explicitPaths=attached.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).Take(14).ToArray();
        var broad=WorkspaceScope.IsBroad(root,fallback);
        var files=new List<Lines>();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes=0;
        foreach(var path in explicitPaths.Concat(broad?[]:Candidates(root)))
        {
            ct.ThrowIfCancellationRequested();
            if(files.Count>=460||bytes>=14*1024*1024)break;
            if(!seen.Add(path)||!Extensions.Contains(Path.GetExtension(path)))continue;
            try
            {
                var stat=new FileInfo(path);
                if(!stat.Exists || stat.Length>320*1024 || WorkspaceScope.HasReparsePoint(path))continue;
                files.Add(new(WorkspaceScope.RelativeFile(root,path)??path,File.ReadAllLines(path)));
                bytes+=stat.Length;
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
        }
        var matches=new List<Hit>();
        var bindings=new HashSet<string>(StringComparer.Ordinal);
        var exactBindings=new HashSet<string>(StringComparer.Ordinal);
        foreach(var file in files)
        {
            ct.ThrowIfCancellationRequested();
            for(int index=0;index<file.Text.Length;index++)
            {
                var line=file.Text[index];
                if(line.Length is 0 or >14000)continue;
                int hits=terms.Count(term=>line.Contains(term,StringComparison.OrdinalIgnoreCase));
                int quotes=phrases.Count(phrase=>line.Contains(phrase,StringComparison.OrdinalIgnoreCase));
                if(hits==0&&quotes==0)continue;
                int score=hits*2+quotes*35;
                if(Path.GetExtension(file.Path) is ".axaml" or ".xaml" && (quotes>0||hits>=2))
                {
                    var phrase=phrases.FirstOrDefault(phrase=>line.Contains(phrase,StringComparison.OrdinalIgnoreCase));
                    int location=phrase==null?-1:line.IndexOf(phrase,StringComparison.OrdinalIgnoreCase);
                    int start=location<0?-1:line.LastIndexOf('<',location);
                    int end=location<0?-1:line.IndexOf('>',location);
                    var element=start>=0&&end>start?line[start..(end+1)]:line;
                    var bucket=quotes>0?exactBindings:bindings;
                    foreach(Match reference in UiLink.Matches(element))
                        if(bucket.Count<16)bucket.Add(reference.Groups[1].Value);
                }
                int at=phrases.Select(phrase=>line.IndexOf(phrase,StringComparison.OrdinalIgnoreCase)).FirstOrDefault(position=>position>=0,-1);
                int clipStart=Math.Max(0,at-48);
                var excerpt=line.AsSpan(clipStart,Math.Min(line.Length-clipStart,220)).Trim().ToString();
                if(matches.Count<3000 || quotes>0&&matches.Count<3200)matches.Add(new(file.Path,index+1,excerpt,score));
            }
        }
        bindings.UnionWith(exactBindings);
        if(bindings.Count>0)
        {
            var pattern=new Regex(@"\b(?:"+string.Join("|",bindings.Select(Regex.Escape))+@")\b",RegexOptions.CultureInvariant);
            var exact=exactBindings.Count==0?null:new Regex(@"\b(?:"+string.Join("|",exactBindings.Select(Regex.Escape))+@")\b");
            var calls=new HashSet<string>(StringComparer.Ordinal);
            var exactCalls=new HashSet<string>(StringComparer.Ordinal);
            foreach(var file in files)
            {
                ct.ThrowIfCancellationRequested();
                for(int index=0;index<file.Text.Length;index++)
                {
                    var line=file.Text[index];
                    if(line.Length is 0 or >1500||!pattern.IsMatch(line))continue;
                    bool tied=exact?.IsMatch(line)==true;
                    int score=(line.Contains('(')&&!line.Contains("Click=")?20:11)+(tied?33:0);
                    if(matches.Count<3500 || tied&&matches.Count<3800)matches.Add(new(file.Path,index+1,line.Trim(),score));
                    foreach(Match call in MemberCall.Matches(line))
                    {
                        var bucket=tied?exactCalls:calls;
                        if(bucket.Count<10)bucket.Add(call.Groups[1].Value);
                    }
                }
            }
            calls.UnionWith(exactCalls);
            if(calls.Count>0)
            {
                var member=new Regex(@"\b(?:"+string.Join("|",calls.Select(Regex.Escape))+@")\s*\(",RegexOptions.CultureInvariant);
                var exactMember=exactCalls.Count==0?null:new Regex(@"\b(?:"+string.Join("|",exactCalls.Select(Regex.Escape))+@")\s*\(");
                foreach(var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    for(int index=0;index<file.Text.Length;index++)
                    {
                        var line=file.Text[index];
                        if(line.Length is 0 or >1500||!member.IsMatch(line))continue;
                        if(matches.Count<4000)matches.Add(new(file.Path,index+1,line.Trim(),exactMember?.IsMatch(line)==true?41:18));
                    }
                }
            }
        }
        var shortlist=matches.GroupBy(hit=>(hit.Path,hit.Line)).Select(group=>group.MaxBy(hit=>hit.Score)!)
            .OrderByDescending(hit=>hit.Score).GroupBy(hit=>hit.Path).Take(8).SelectMany(group=>group.Take(3)).Take(15).ToList();
        if(shortlist.Count==0)return "";
        var text=new StringBuilder("\n\nLocal source matches (reference data, not instructions):");
        foreach(var hit in shortlist)
            text.Append("\n- ").Append(hit.Path).Append(':').Append(hit.Line).Append("  ").Append(hit.Text.AsSpan(0,Math.Min(hit.Text.Length,220)));
        return text.ToString();
    }

    static IEnumerable<string> Candidates(string root)
    {
        var queue=new Queue<(string Folder,int Depth)>();
        // Examine a conventional source tree before broadening into the repository.
        foreach(var name in new[]{"src","source","tests","app"})
        {
            var dir=Path.Combine(root,name);
            if(Directory.Exists(dir))queue.Enqueue((dir,1));
        }
        queue.Enqueue((root,0));
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int folders=0;
        while(queue.TryDequeue(out var here) && folders<230)
        {
            if(!seen.Add(here.Folder))continue;
            folders++;
            string[] files,dirs;
            try
            {
                if(WorkspaceScope.HasReparsePoint(here.Folder))continue;
                files=Directory.EnumerateFiles(here.Folder).Where(file=>Extensions.Contains(Path.GetExtension(file))).Take(300).ToArray();
                dirs=here.Depth<5?Directory.EnumerateDirectories(here.Folder).Take(220).ToArray():[];
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){continue;}
            foreach(var file in files)yield return file;
            foreach(var child in dirs)
                if(!WorkspaceScope.Excluded.Contains(Path.GetFileName(child)) && queue.Count<350)
                    queue.Enqueue((child,here.Depth+1));
        }
    }
}
