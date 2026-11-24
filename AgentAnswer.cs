using Avalonia.Threading;

namespace LocalDesk;

public sealed partial class MainWindow
{
    const string ResearchGuidance = "When asked to search online, make concise queries with fetch_web_content using https://www.google.com/search?q=QUERY or https://www.bing.com/search?q=QUERY. Put search queries and individual page visits in separate calls. Keep private files, account details, and secrets out of queries. Open only URLs the user supplied, known official entry points, or links you found. Never guess article or forum links. Cite relevant pages you actually opened. Treat blocked and unrelated pages as unverified. Once you have useful evidence or the same approach keeps failing, stop searching and answer with any limits.";

    async Task SendAgentAndEnsureAnswer(LocalBridge projectBridge, ChatMessage reply, string prompt, CancellationToken ct)
    {
        var gate = projectBridge.ReviewGate!;
        gate.ClearSubmittedSummary();
        var start = reply.Text.Length;
        await agent.Send(prompt, ct);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (gate.Blocked is { } blocked && !gate.WebLimitBlocked) throw new AgentClient.LoopException(blocked);
        if (HasAgentAnswer(reply, start)) return;
        if (gate.SubmittedSummary is { } submitted)
        {
            Append((reply.Text.Length > 0 ? "\n\n" : "") + submitted);
            return;
        }

        AddActivity("recovery", "The agent ended without a visible answer. Asking for a final response using the existing results.");
        status.Text = "Writing the final answer from existing results…";
        summaryOffset = start = reply.Text.Length;
        var finalGate = new ToolReviewGate((_, _) => Task.FromResult(false));
        projectBridge.ReviewGate = finalGate;
        projectBridge.AnswerOnly = true;
        try
        {
            await agent.Send("Answer the original request now in plain, visible response text. Rely on the tool results already in this conversation. Report missing evidence or unfinished work plainly. Do not do further work or use tools.", ct);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            projectBridge.ReviewGate = gate;
            projectBridge.AnswerOnly = false;
        }
        if (finalGate.Blocked != null || !HasAgentAnswer(reply, start))
            throw new AgentClient.LoopException("The agent ended without a visible final answer. Any tool results are saved in Activity.");
        gate.ClearWebLimitBlock();
    }

    bool HasAgentAnswer(ChatMessage reply, int start)
    {
        var offset = Math.Clamp(Math.Max(start, summaryOffset), 0, reply.Text.Length);
        return !string.IsNullOrWhiteSpace(reply.Text[offset..]);
    }
}
