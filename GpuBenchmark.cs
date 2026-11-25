using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LocalDesk;

public sealed record GpuBenchmarkSample(int Context,double Load,double First,double Repeat,double Rate,long? ModelVramBytes,long? ModelBytes)
{
    public override string ToString()=>$"{Context:N0} context · load {Load:F1}s · first {First:F2}s · repeat {Repeat:F2}s · {Rate:F1} tok/s"+
        (ModelVramBytes is { } vram&&ModelBytes is { } bytes?$" · {vram/1073741824.0:F1}/{bytes/1073741824.0:F1} GiB on GPU":"");
}

internal static class GpuBenchmark
{
    internal static async Task<GpuBenchmarkSample> Measure(OllamaClient client,string model,int context,CancellationToken ct)
    {
        var runtime=OllamaClient.RuntimeModel(model);
        await client.Connect(ct);
        using(var unload=await client.Post("/api/generate",new{model=runtime,keep_alive=0},ct))
        if(!unload.IsSuccessStatusCode&&(int)unload.StatusCode!=404)await OllamaClient.Check(unload,ct);
        var watch=Stopwatch.StartNew();
        await client.Load(model,ct,context);
        var load=watch.Elapsed.TotalSeconds;
        var first=await Probe(client,runtime,context,ct);
        var repeat=await Probe(client,runtime,context,ct);
        var memory=(await ResourceReader.Residents(client.Http,ct))?.FirstOrDefault(entry=>entry.Name==runtime);
        return new(context,load,first.First,repeat.First,repeat.Rate,memory?.VramBytes,memory?.Bytes);
    }

    private static async Task<(double First,double Rate)> Probe(OllamaClient client,string model,int context,CancellationToken ct)
    {
        const string prompt="Write a short factual paragraph, about 80 words, about why daily measurements can vary. Reply with the paragraph only.";
        var watch=Stopwatch.StartNew();
        double? first=null;
        double rate=0;
        using var response=await client.Post("/api/generate",new{model,prompt,stream=true,think=false,options=new{num_ctx=context,num_predict=96,temperature=0}},ct);
        await OllamaClient.Check(response,ct);
        using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while(await reader.ReadLineAsync(ct) is { } line)
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            var data=JsonNode.Parse(line);
            if(data?["error"] is { } error)throw new IOException(error.ToString());
            if(data?["response"]?.ToString().Length>0)first??=watch.Elapsed.TotalSeconds;
            if(data?["eval_duration"]?.GetValue<double>() is >0 and var duration)
                rate=(data["eval_count"]?.GetValue<int>()??0)*1e9/duration;
        }
        return (first??throw new IOException("The benchmark model returned no visible output."),rate);
    }
}

public sealed partial class MainWindow
{
    async Task ShowGpuBenchmark()
    {
        if (ollama.Compatible || !ollama.Http.BaseAddress!.IsLoopback)
        { status.Text = "GPU benchmarking needs Ollama running on this PC."; return; }
        if(busy)return;
        var selected=SelectedModel;
        var dialog=UtilityWindow("Benchmark context profiles",820,530);
        var panel=new StackPanel {Margin=new Thickness(20),Spacing=11};
        var info=EvidenceText("Model · "+selected.Name+". Tests measure model loading and two identical short prompts at each selected context.");
        panel.Children.Add(info);
        var sizes=new WrapPanel {Orientation=Orientation.Horizontal};
        var contexts=new[]{8192,16384,32768,65536}.Select((n,i)=>(Size:n,Box:new CheckBox{Content=$"{n/1024}K",IsChecked=i is 1 or 2,Margin=new Thickness(0,0,10,0)})).ToArray();
        foreach(var choice in contexts)sizes.Children.Add(choice.Box);
        panel.Children.Add(sizes);
        var samples=preferences.Benchmarks.GetValueOrDefault(selected.Id)?.ToList()??[];
        var list=new ListBox {ItemsSource=samples.ToArray(),Height=148};
        panel.Children.Add(list);
        var note=EvidenceText("Pick context sizes and run the probe. Model VRAM comes from Ollama's resident model report.");
        panel.Children.Add(note);
        var actions=new WrapPanel {Orientation=Orientation.Horizontal};
        var start=Button("Run benchmark",null);
        var stopBenchmark=Button("Stop benchmark",null);
        var fast=Button("Save fastest as default",null);
        var heavy=Button("Save largest as default",null);
        CancellationTokenSource? running=null;
        stopBenchmark.IsEnabled=false;
        fast.IsEnabled=heavy.IsEnabled=samples.Count>0;
        void Store(GpuBenchmarkSample? sample)
        {
            if(sample==null)return;
            var profile=new ModelProfile(sample.Context,Math.Min(current.OutputTokens,sample.Context/4),current.Thinking);
            preferences.Profiles[selected.Id]=profile;
            preferences.Save(store.Root);
            ApplyProfile(selected.Id);
            Save();
            note.Text=$"Saved and applied the {sample.Context:N0}-token profile. It will load with the next request.";
        }
        fast.Click+=(_,_)=>Store(samples.OrderBy(item=>item.Repeat+96/Math.Max(1,item.Rate)).FirstOrDefault());
        heavy.Click+=(_,_)=>Store(samples.OrderByDescending(item=>item.Context).FirstOrDefault());
        stopBenchmark.Click+=(_,_)=>running?.Cancel();
        start.Click+=async(_,_)=>
        {
            var enabled=contexts.Where(item=>item.Box.IsChecked==true).Select(item=>item.Size).ToArray();
            if(enabled.Length==0){note.Text="Choose at least one context size.";return;}
            using var ct=new CancellationTokenSource(TimeSpan.FromMinutes(8));
            running=ct;
            start.IsEnabled=fast.IsEnabled=heavy.IsEnabled=false;
            stopBenchmark.IsEnabled=true;
            SetBusy(true);
            SetMiniPhase("Benchmarking model");
            agent.Dispose();
            loadedModel=null;
            try
            {
                foreach(var size in enabled)
                {
                    note.Text=$"Testing {size:N0} context…";
                    try
                    {
                        var sample=await GpuBenchmark.Measure(ollama,selected.Id,size,ct.Token);
                        samples.RemoveAll(item=>item.Context==size);
                        samples.Add(sample);
                        list.ItemsSource=samples.OrderBy(item=>item.Context).ToArray();
                        preferences.Benchmarks[selected.Id]=samples.ToList();
                        preferences.Save(store.Root);
                    }
                    catch(Exception error) when(error is not OperationCanceledException)
                    {note.Text=$"{size:N0} context could not complete. "+error.Message;return;}
                }
                note.Text="Benchmark complete. Select a profile to save it as your default.";
            }
            catch(OperationCanceledException){note.Text="Benchmark stopped. Completed samples were saved.";}
            finally
            {
                running=null;
                stopBenchmark.IsEnabled=false;
                start.IsEnabled=true;
                fast.IsEnabled=heavy.IsEnabled=samples.Count>0;
                SetBusy(false);
            }
        };
        dialog.Closing+=(_,e)=>{if(running!=null){e.Cancel=true;running.Cancel();}};
        foreach(var control in new[]{start,stopBenchmark,fast,heavy}){control.Margin=new Thickness(0,0,6,0);actions.Children.Add(control);}
        panel.Children.Add(actions);
        dialog.Content=panel;
        await dialog.ShowDialog(this);
    }
}
