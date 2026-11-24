using System.Text.RegularExpressions;
namespace LocalDesk;
public record ArtifactLink(string Name,string Path);
static class Artifacts
{
    public static List<ArtifactLink> Find(string text,string project,IEnumerable<string>? hints=null)
    {
        var candidates=new List<string>(hints??[]);
        foreach(Match match in Regex.Matches(text,@"`([^`\r\n]+)`|\]\(([^)\r\n]+)\)|([A-Za-z]:[\\/][^\r\n<>""|]+)"))
            candidates.Add(match.Groups.Cast<Group>().Skip(1).First(g=>g.Success).Value);
        var result=new List<ArtifactLink>();
        foreach(var candidate in candidates)
        {
            try
            {
                var path=candidate.Trim().Trim('"','\'','<','>').TrimEnd('.',',',';');
                if(path.StartsWith("file://",StringComparison.OrdinalIgnoreCase))path=new Uri(path).LocalPath;
                if(!Path.IsPathFullyQualified(path)){if(string.IsNullOrWhiteSpace(project))continue;path=Path.Combine(project,path);}
                path=Path.GetFullPath(path);
                if(File.Exists(path)&&!result.Any(a=>a.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))result.Add(new(Path.GetFileName(path),path));
            }
            catch{}
            if(result.Count>=20)break;
        }
        return result;
    }
}
