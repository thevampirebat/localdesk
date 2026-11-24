using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalDesk;

internal sealed record GpuMemory(string Name, long UsedMiB, long TotalMiB);
internal sealed record ResidentMemory(string Name, long Bytes, long? VramBytes, int? Context);

internal static class ResourceReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public static (ulong Used,ulong Total)? Ram()
    {
        var data = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref data) && data.TotalPhysical >= data.AvailablePhysical
            ? (data.TotalPhysical-data.AvailablePhysical,data.TotalPhysical) : null;
    }

    public static List<GpuMemory> ParseGpus(string text)
    {
        var gpus = new List<GpuMemory>();
        foreach(var line in text.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            var columns=line.Split(',',StringSplitOptions.TrimEntries);
            if(columns.Length==3 && long.TryParse(columns[1],NumberStyles.None,CultureInfo.InvariantCulture,out var used) &&
                long.TryParse(columns[2],NumberStyles.None,CultureInfo.InvariantCulture,out var total) && total>0)
                gpus.Add(new(columns[0],used,total));
        }
        return gpus;
    }

    public static async Task<List<GpuMemory>> Gpus(CancellationToken ct)
    {
        var exe=Path.Combine(Environment.SystemDirectory,"nvidia-smi.exe");
        if(!File.Exists(exe))exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NVIDIA Corporation","NVSMI","nvidia-smi.exe");
        if(!File.Exists(exe))return [];
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
            start.ArgumentList.Add("--query-gpu=name,memory.used,memory.total");
            start.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process=Process.Start(start);
            if(process==null)return [];
            var output=process.StandardOutput.ReadToEndAsync(ct);
            var errors=process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct); }
            catch(OperationCanceledException){try{process.Kill();}catch(InvalidOperationException){}throw;}
            await errors;
            return process.ExitCode==0?ParseGpus(await output):[];
        }
        catch(Exception e) when(e is System.ComponentModel.Win32Exception or IOException){return [];}
    }

    public static List<ResidentMemory> ParseResidents(JsonNode? json)
    {
        var result=new List<ResidentMemory>();
        foreach(var item in json?["models"]?.AsArray()??[])
        {
            var name=item?["name"]?.GetValue<string>()??item?["model"]?.GetValue<string>();
            var bytes=item?["size"]?.GetValue<long>()??0;
            var vram=item?["size_vram"]?.GetValue<long>();
            if(name!=null&&bytes>0)result.Add(new(name,bytes,vram,item?["context_length"]?.GetValue<int>()));
        }
        return result;
    }

    public static async Task<List<ResidentMemory>?> Residents(HttpClient client,CancellationToken ct)
    {
        try
        {
            using var reply=await client.GetAsync("/api/ps",ct);
            if(!reply.IsSuccessStatusCode)return null;
            return ParseResidents(JsonNode.Parse(await reply.Content.ReadAsStringAsync(ct)));
        }
        catch(Exception e) when(e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException){return null;}
    }
}

public sealed partial class MainWindow
{
    Window? memoryWindow;

    void ShowMemory()
    {
        if(memoryWindow!=null){memoryWindow.Activate();return;}
        var dialog=UtilityWindow("Live memory",540,560);
        memoryWindow=dialog;
        var panel=new StackPanel { Margin=new Thickness(22),Spacing=10 };
        panel.Children.Add(new TextBlock { Text="Live memory",FontSize=24,FontWeight=FontWeight.SemiBold });
        var context=EvidenceText("");
        var readings=new StackPanel { Spacing=9 };
        panel.Children.Add(context);
        panel.Children.Add(readings);
        dialog.Content=new ScrollViewer { Content=panel };
        var lifetime=new CancellationTokenSource();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(3) };
        bool sampling=false;
        static string GiB(double bytes)=>$"{bytes/1073741824d:F1} GiB";
        async Task Read()
        {
            if(sampling||lifetime.IsCancellationRequested)return;
            sampling=true;
            using var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            request.CancelAfter(TimeSpan.FromSeconds(2.5));
            async Task<T> Available<T>(Task<T> task,T fallback)
            {
                try{return await task;}catch(OperationCanceledException){return fallback;}
            }
            try
            {
                var ram=ResourceReader.Ram();
                var gpuTask=Available(ResourceReader.Gpus(request.Token),[]);
                var modelTask=Available<List<ResidentMemory>?>(ResourceReader.Residents(ollama.Http,request.Token),null);
                await Task.WhenAll(gpuTask,modelTask);
                if(lifetime.IsCancellationRequested)return;
                context.Text=$"Context · {(current.ContextUsageEstimated?"≈":"")}{current.LastContextUsed:N0} / {current.ContextTokens:N0} tokens";
                readings.Children.Clear();
                void Meter(string title,string description,double used,double total)
                {
                    readings.Children.Add(new TextBlock{Text=title,FontWeight=FontWeight.SemiBold});
                    readings.Children.Add(EvidenceText(description));
                    readings.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=Math.Clamp(used/total*100,0,100),Height=6});
                }
                if(ram is { } physical)Meter("System RAM",$"{GiB(physical.Used)} used of {GiB(physical.Total)}",physical.Used,physical.Total);
                else readings.Children.Add(EvidenceText("System RAM reading unavailable."));
                var gpus=gpuTask.Result;
                foreach(var gpu in gpus)Meter(gpu.Name,$"{gpu.UsedMiB/1024d:F1} GiB dedicated VRAM used of {gpu.TotalMiB/1024d:F1} GiB",gpu.UsedMiB,gpu.TotalMiB);
                if(gpus.Count==0)readings.Children.Add(EvidenceText("Dedicated NVIDIA GPU memory reading unavailable."));
                readings.Children.Add(new TextBlock { Text="Ollama resident models",FontWeight=FontWeight.SemiBold,Margin=new Thickness(0,8,0,0) });
                var models=modelTask.Result;
                if(models==null)readings.Children.Add(EvidenceText("Ollama is unavailable."));
                else if(models.Count==0)readings.Children.Add(EvidenceText("No models are loaded."));
                else foreach(var model in models)
                {
                    string label=model.Name==OllamaClient.RuntimeModel(current.Model)?SelectedModel.Name:model.Name;
                    string size=model.VramBytes.HasValue
                        ? $"{GiB(model.VramBytes.Value)} on GPU · {GiB(Math.Max(0,model.Bytes-model.VramBytes.Value))} outside GPU"
                        : $"{GiB(model.Bytes)} resident · GPU split unavailable";
                    readings.Children.Add(EvidenceText(label+"\n"+size+(model.Context is { } length?$" · {length:N0} context":"")));
                }
                readings.Children.Add(EvidenceText("Ollama reports per-model estimates. System and GPU meters include other apps."));
            }
            catch(Exception error){if(!lifetime.IsCancellationRequested){readings.Children.Clear();readings.Children.Add(EvidenceText(error.Message));}}
            finally{sampling=false;}
        }
        timer.Tick+=async(_,_)=>await Read();
        dialog.Opened+=async(_,_)=>{timer.Start();await Read();};
        dialog.Closed+=(_,_)=>{timer.Stop();lifetime.Cancel();memoryWindow=null;};
        dialog.Show(this);
    }
}
