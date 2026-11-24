using System.Diagnostics;
namespace LocalDesk;

public record GenerationSpeed(double TokensPerSecond, int Tokens, bool Estimated, bool Finished, bool TokenCountEstimated=false);

// Streaming chunks are only a live estimate. Replace their count with server usage at completion.
public sealed class GenerationMeter(Action<GenerationSpeed> report)
{
    readonly Stopwatch clock=new();
    int chunks;
    double lastOutput;
    public void Tick()
    {
        if(!clock.IsRunning)clock.Start();
        chunks++;lastOutput=clock.Elapsed.TotalSeconds;
        if(chunks>1&&lastOutput>=0.1)report(new((chunks-1)/lastOutput,chunks,true,false));
    }
    public void Complete(int? tokens=null,double? seconds=null,bool estimated=true)
    {
        var count=tokens??chunks;
        var duration=seconds??(chunks>1?lastOutput:0);
        if(count>0)report(new(duration>0?count/duration:0,count,estimated,true,tokens==null));
    }
}
