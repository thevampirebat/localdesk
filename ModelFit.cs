using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;

namespace LocalDesk;

internal sealed record FitChoice(LocalModel Model, int Context, string Reason, bool Fits, bool Measured, bool Tools, bool Vision)
{
    public override string ToString() => Model.Name + " · " + Reason;
}

public sealed partial class MainWindow
{
    async Task<List<FitChoice>> FindModelFits(CancellationToken ct)
    {
        if (ollama.Compatible) return [];
        await ollama.Connect(ct);
        var json = JsonNode.Parse(await ollama.Http.GetStringAsync("/api/tags", ct));
        var remote = !ollama.Http.BaseAddress!.IsLoopback;
        var gpus = remote ? [] : await ResourceReader.Gpus(ct);
        const long gib = 1L << 30;
        var freeGpu = gpus.Count == 0 ? (long?)null : gpus.Max(gpu => Math.Max(0, gpu.TotalMiB - gpu.UsedMiB)) << 20;
        var ram = remote ? null : ResourceReader.Ram();
        long? ramLimit = preferences.ModelRamGiB > 0 ? (long)(preferences.ModelRamGiB * gib)
            : ram is { } memory ? (long)(memory.Total - memory.Used) : null;
        long? vramLimit = preferences.ModelVramGiB > 0 ? (long)(preferences.ModelVramGiB * gib) : freeGpu;
        var results = new List<FitChoice>();
        foreach (var entry in (json?["models"]?.AsArray() ?? []).Take(30))
        {
            ct.ThrowIfCancellationRequested();
            var tag = entry?["name"]?.ToString();
            if (string.IsNullOrEmpty(tag) || tag.StartsWith("localdesk-", StringComparison.OrdinalIgnoreCase)) continue;
            var model = LocalModel.FromTag(tag);
            IReadOnlySet<string> caps;
            try { caps = await ollama.Capabilities(tag, ct); }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { caps = new HashSet<string>(); }
            var samples = remote ? [] : preferences.Benchmarks.GetValueOrDefault(tag) ?? [];
            var measured = samples.Where(s => s.ModelBytes is > 0 && s.ModelVramBytes is >= 0 && s.ModelVramBytes <= s.ModelBytes)
                .OrderByDescending(s => s.Context).ToList();
            bool Fits(GpuBenchmarkSample sample)
            {
                var allocation = new ResidentMemory(tag, sample.ModelBytes!.Value, sample.ModelVramBytes, sample.Context);
                return ollama.MemoryBudget.Rejection(allocation) == null;
            }
            var verified = measured.FirstOrDefault(Fits);
            var size = entry?["size"]?.GetValue<long>() ?? 0;
            var fit = verified != null || measured.Count == 0 && size > 0 && vramLimit is > 0 && size + gib < vramLimit;
            var reason = verified != null ? $"Benchmarked at {verified.Context:N0} tokens"
                : measured.Count != 0 ? "Benchmark exceeds the configured memory budget"
                : fit ? "Likely GPU fit from weight size; benchmark to confirm"
                : vramLimit.HasValue && size > vramLimit.Value && ramLimit.HasValue && size > vramLimit.Value + ramLimit.Value
                    ? "Weights exceed the combined available memory estimate"
                    : "Fit unverified; CPU offload or a smaller context may be needed";
            results.Add(new(model, verified?.Context ?? 4096, reason, fit, verified != null, caps.Contains("tools"), caps.Contains("vision")));
        }
        return results;
    }

    async Task ShowModelFit()
    {
        if (busy || sendingRequest) return;
        var dialog = UtilityWindow("Find a model that fits", 770, 550);
        var note = EvidenceText("Checking available models…");
        var list = new ListBox { Height = 300 };
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 10, Children = { note, list } };
        var use = Button("Use selected model and context", (_, _) => dialog.Close(list.SelectedItem));
        var benchmark = Button("Benchmark current model…", (_, _) => dialog.Close("benchmark"));
        benchmark.IsEnabled = !ollama.Compatible && ollama.Http.BaseAddress!.IsLoopback;
        panel.Children.Add(use);
        panel.Children.Add(benchmark);
        panel.Children.Add(Button("Close", (_, _) => dialog.Close()));
        dialog.Content = panel;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        dialog.Closed += (_, _) => stop.Cancel();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var choices = (await FindModelFits(stop.Token)).OrderByDescending(item => item.Measured).ThenByDescending(item => item.Fits).ToList();
                list.ItemsSource = choices;
                list.SelectedIndex = choices.Count > 0 ? 0 : -1;
                note.Text = choices.Count == 0 ? "Model fit needs an Ollama connection and available models."
                    : "Measured entries use saved benchmarks. Estimates start at 4,096 tokens. Server hardware may differ from this PC.";
            }
            catch (OperationCanceledException) { note.Text = "Model fit check stopped or timed out."; }
            catch (Exception error) { note.Text = error.Message; }
        };
        var selected = await dialog.ShowDialog<object?>(this);
        if (selected is string) { await ShowGpuBenchmark(); return; }
        if (selected is not FitChoice pick) return;
        current.Model = pick.Model.Id;
        ApplyProfile(pick.Model.Id);
        current.ContextTokens = pick.Context;
        current.OutputTokens = Math.Min(current.OutputTokens, pick.Context / 3);
        Show(current);
        Save();
    }
}
