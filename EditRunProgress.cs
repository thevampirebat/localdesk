using System.Text.RegularExpressions;

namespace LocalDesk;

internal static partial class EditRunOutcome
{
    internal const string NoVerifiedChanges = "No file changes were verified in the project or attached files. Review Activity for any work done elsewhere.";

    [GeneratedRegex(@"(?:^|[.!?\r\n]\s*|\b(?:and|also|then)\s+)(?:(?:please|also|then)\s+)*(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?|i\s+(?:would\s+like|want|need)\s+(?:you\s+)?to\s+|i['’]d\s+like\s+(?:you\s+)?to\s+)?(?:add|apply|change|copy|create|delete|edit|fix|implement|modify|move|patch|refactor|remove|rename|replace|rewrite|save|update|write)\s+(?!to\b|about\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditRequest();

    [GeneratedRegex(@"^(?:ok(?:ay)?[, ]+)?(?:it|the\s+[^.!?\r\n]{1,75}?)\s+(?:still\s+(?:doesn['’]t|won['’]t|isn['’]t|fails|closes)|is\s+still\s+(?:there|missing|broken|slow)|(?:is|works)\b[^.!?\r\n]{0,70}\bbut\s+(?:doesn['’]t|won['’]t)|['’]?s\s+still\s+not)|^it['’]s\s+still\s+not", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReportedFailure();

    internal static bool ExpectsChanges(string request, bool followingEdit = false) =>
        EditRequest().IsMatch(request.Trim()) || (followingEdit && ReportedFailure().IsMatch(request.Trim()));

    internal static bool MarkUnverified(ChatMessage reply, bool expectsChanges)
    {
        if (!expectsChanges || reply.ProjectUsed == null || reply.VerifiedFiles.Count != 0) return false;
        reply.Successful = false;
        reply.EvidenceNote = reply.EvidenceNote is { Length: > 0 } note
            ? note.Contains(NoVerifiedChanges, StringComparison.Ordinal) ? note : note + " " + NoVerifiedChanges
            : NoVerifiedChanges;
        return true;
    }
}

internal sealed class EditRunProgress(ProjectEvidence evidence)
{
    private readonly object sync = new();
    private Dictionary<string, string> stamp = evidence.ProgressStamp(starting: true);
    private int localTools;
    private int compactions;

    internal void ToolAllowed(string name)
    {
        if (name is "read_file" or "read_files" or "list_files" or "search_files" or "search_codebase" or
            "list_code_definition_names" or "run_commands" or "execute_command")
            lock (sync) localTools++;
    }

    internal string? BeforeCompaction()
    {
        lock (sync)
        {
            if (++compactions < 4 || localTools < 8) return null;

            var current = evidence.ProgressStamp();
            if (current.Count == stamp.Count && current.All(file => stamp.TryGetValue(file.Key, out var hash) && hash == file.Value))
                return "The agent kept inspecting files and compacting context without verified file progress. Review Activity before resuming or handing off to another model.";

            stamp = current;
            compactions = 1;
            localTools = 0;
            return null;
        }
    }
}
