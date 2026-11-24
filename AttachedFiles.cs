using System.Text;
namespace LocalDesk;
static class AttachedFiles
{
    static readonly HashSet<string> TextExtensions=new(".txt .md .cs .csproj .sln .json .jsonl .xml .xaml .axaml .html .htm .css .js .jsx .ts .tsx .py .ps1 .cmd .bat .sh .yaml .yml .toml .ini .cfg .config .csv .log .sql .c .cpp .h .hpp .rs .go .java .kt .swift .vue .svelte .gitignore .editorconfig".Split(' '),StringComparer.OrdinalIgnoreCase);
    public static bool IsImage(string path)=>new[]{".png",".jpg",".jpeg",".webp"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    public static bool HasTextExtension(string path)=>TextExtensions.Contains(Path.GetExtension(path))||TextExtensions.Contains(Path.GetFileName(path));
    public static bool IsText(string path)=>HasTextExtension(path)&&new FileInfo(path).Length<=1024*1024;
    public static FileAttachment Import(string source,string root,string chatId)
    {
        var file=new FileAttachment{Name=Path.GetFileName(source),SourcePath=Path.GetFullPath(source),IsText=IsText(source)};
        if(file.IsText){var dir=Path.Combine(root,"files",chatId);Directory.CreateDirectory(dir);file.StoredPath=Path.Combine(dir,Guid.NewGuid().ToString("N")+Path.GetExtension(source));File.Copy(source,file.StoredPath);}
        return file;
    }
    public static string Context(IEnumerable<FileAttachment> files,bool agent)
    {
        var result=new StringBuilder();var remaining=24000;
        foreach(var file in files)
        {
            result.Append("\n\nAttached file: ").Append(file.Name);
            if(agent)result.Append("\nOriginal path: ").Append(file.SourcePath);
            if(file.IsText&&File.Exists(file.StoredPath)&&remaining>0)
            {
                using var reader=new StreamReader(file.StoredPath,true);var buffer=new char[Math.Min(12000,remaining)];var count=reader.ReadBlock(buffer,0,buffer.Length);remaining-=count;
                result.Append("\nFile contents (reference data):\n").Append(buffer,0,count);
                if(!reader.EndOfStream)result.Append("\n[Preview truncated; use the original file for full contents.]");
            }
            else result.Append(agent?"\n[File reference; use suitable tools to inspect this format.]":"\n[This file needs Project agent mode to inspect its contents.]");
        }
        return result.ToString();
    }
}
