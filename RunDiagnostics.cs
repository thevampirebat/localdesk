using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LocalDesk;

public sealed record InferenceTiming(double PromptSeconds, double GenerationSeconds, bool Estimated);

public sealed class RunDiagnostics
{
    public double Load { get; set; }
    public double Prompt { get; set; }
    public double Generation { get; set; }
    public double Tools { get; set; }
    public double Approval { get; set; }
    public double Build { get; set; }
    public double Prepare { get; set; }
    public double Verify { get; set; }
    public double Total { get; set; }
    public int ModelCalls { get; set; }
    public int CompletedTools { get; set; }
    public bool ModelTimingEstimated { get; set; }
    public int DuplicateReadResults { get; set; }
    public int EstimatedInputTokensRemoved { get; set; }
}

sealed class RunCollector(double load)
{
    private readonly RunDiagnostics report = new() { Load = load };
    public void Inference(InferenceTiming timing)
    {
        lock (report)
        {
            report.ModelCalls++;
            report.Prompt += timing.PromptSeconds;
            report.Generation += timing.GenerationSeconds;
            report.ModelTimingEstimated |= timing.Estimated;
        }
    }

    public void Tool(double seconds)
    {
        lock (report) { report.Tools += seconds; report.CompletedTools++; }
    }

    public void Approval(double seconds) { lock (report) report.Approval += seconds; }

    public void ReduceToolResults(int results, int tokens)
    {
        lock (report)
        {
            report.DuplicateReadResults += results;
            report.EstimatedInputTokensRemoved += tokens;
        }
    }

    public RunDiagnostics Finish(double total, double prepare, double verify, double build)
    {
        lock (report)
        {
            report.Total = total;
            report.Prepare = prepare;
            report.Verify = verify;
            report.Build = build;
            return report;
        }
    }
}

public sealed partial class MainWindow
{
    RunCollector? runCollector;

    async Task ShowDiagnostics(RunDiagnostics metrics)
    {
        var dialog = UtilityWindow("Run diagnostics", 625, 545);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Where the time went", FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(EvidenceText($"{metrics.Total:F1}s elapsed · {metrics.ModelCalls} model calls · {metrics.CompletedTools} tools completed"));
        if (metrics.DuplicateReadResults > 0) panel.Children.Add(EvidenceText($"Token Guard shortened {metrics.DuplicateReadResults:N0} repeated file results across model requests (≈{metrics.EstimatedInputTokensRemoved:N0} input tokens removed)."));
        var rows = new (string Label, double Seconds)[]
        {
            ("Model load in this run", metrics.Load),
            ("File preparation and focused search", metrics.Prepare),
            (metrics.ModelTimingEstimated ? "Context and first output (estimated)" : "Context processing", metrics.Prompt),
            (metrics.ModelTimingEstimated ? "Generation (estimated)" : "Generation", metrics.Generation),
            ("Tool calls that reported completion", metrics.Tools),
            ("Waiting for your approval", metrics.Approval),
            ("Build check", metrics.Build),
            ("File verification", metrics.Verify)
        };
        foreach (var row in rows)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3) };
            line.Children.Add(EvidenceText(row.Label));
            var value = EvidenceText($"{row.Seconds:F2}s");
            Grid.SetColumn(value, 1);
            line.Children.Add(value);
            panel.Children.Add(line);
        }
        panel.Children.Add(EvidenceText("Some phases can overlap. Tool time covers completed calls, and other overhead may account for part of the elapsed time."));
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(this);
    }

    Task<bool> TimedReview(ToolProposal action,CancellationToken ct) => TimedReview(action,ct,false);

    async Task<bool> TimedReview(ToolProposal action, CancellationToken ct, bool configuredCheck)
    {
        var run = runCollector;
        var watch = Stopwatch.StartNew();
        try { return await ReviewTool(action, ct, configuredCheck); }
        finally { run?.Approval(watch.Elapsed.TotalSeconds); }
    }
}
