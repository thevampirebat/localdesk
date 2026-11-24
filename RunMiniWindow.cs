using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalDesk;

internal enum MiniRunState { Idle, Working, Waiting, Finished, Incomplete }

internal sealed record MiniRunView(MiniRunState State,string Phase,string Chat,string Model,
    TimeSpan? Elapsed,string Speed,int Used,int Context,int Queued,bool QueuePaused,bool HasResult);

internal sealed class RunMiniWindow : Window
{
    readonly DispatcherTimer refresh = new(){Interval=TimeSpan.FromMilliseconds(250)};
    readonly Func<MiniRunView> snapshot;
    readonly Border dot = new(){Width=8,Height=8,CornerRadius=new CornerRadius(4),VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock phase = new(){FontSize=15,FontWeight=FontWeight.SemiBold};
    readonly TextBlock chat = new(){TextTrimming=TextTrimming.CharacterEllipsis,FontSize=13};
    readonly TextBlock model = new(){TextTrimming=TextTrimming.CharacterEllipsis,FontSize=12};
    readonly TextBlock metrics = new(){FontSize=12};
    readonly TextBlock context = new(){FontSize=12};
    readonly ProgressBar meter = new(){Height=4,Maximum=100,Background=SolidColorBrush.Parse("#293744")};
    readonly Button jump = new(){Content="Open LocalDesk"};
    readonly Button previous = new(){Content="Last result"};

    internal RunMiniWindow(Func<MiniRunView> snapshot,Action<bool> goToRun,bool topmost)
    {
        this.snapshot=snapshot;
        Title="LocalDesk · Run monitor";
        Icon=new WindowIcon(Path.Combine(AppContext.BaseDirectory,"Assets","LocalDesk.ico"));
        Width=384;Height=222;MinWidth=384;MinHeight=222;
        CanResize=false;ShowInTaskbar=false;Topmost=topmost;SizeToContent=SizeToContent.Height;
        Background=SolidColorBrush.Parse("#17202A");
        var panel=new StackPanel {Spacing=7,Margin=new Thickness(16,12)};
        var head=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        var label=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center,Children={dot,phase}};
        head.Children.Add(label);
        var pin=new ToggleSwitch {OnContent="Pinned",OffContent="Unpinned",IsChecked=topmost,VerticalAlignment=VerticalAlignment.Center};
        pin.IsCheckedChanged+=(_,_)=>Topmost=pin.IsChecked==true;
        ToolTip.SetTip(pin,"Keep this window above other apps");
        Grid.SetColumn(pin,1);head.Children.Add(pin);
        panel.Children.Add(head);
        panel.Children.Add(chat);
        var muted=SolidColorBrush.Parse("#B2C8CD");
        model.Foreground=metrics.Foreground=context.Foreground=muted;
        panel.Children.Add(model);
        panel.Children.Add(metrics);
        panel.Children.Add(context);
        panel.Children.Add(meter);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8,Margin=new Thickness(0,2,0,0)};
        jump.Click+=(_,_)=>goToRun(false);
        previous.Click+=(_,_)=>goToRun(true);
        buttons.Children.Add(jump);buttons.Children.Add(previous);panel.Children.Add(buttons);
        Content=panel;
        refresh.Tick+=(_,_)=>Update();
        Opened+=(_,_)=>{Update();refresh.Start();};
        Closed+=(_,_)=>refresh.Stop();
    }

    internal MiniRunView Update()
    {
        var view=snapshot();
        var accent=SolidColorBrush.Parse(view.State switch
        {
            MiniRunState.Waiting or MiniRunState.Incomplete=>"#F0BD79",
            MiniRunState.Finished or MiniRunState.Working=>"#9BE1CE",
            _=>"#8EACB9"
        });
        dot.Background=phase.Foreground=accent;
        phase.Text=view.Phase;
        chat.Text=view.Chat;
        model.Text=view.Model;
        var elapsed=view.Elapsed is { } time?(time.TotalHours>=1?$"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}":$"{time.Minutes}:{time.Seconds:00}"):"—";
        metrics.Text=$"{elapsed} elapsed  ·  {view.Speed}  ·  {view.Queued} queued"+(view.QueuePaused?" (paused)":"");
        context.Text=view.Context>0?$"{view.Used:N0} / {view.Context:N0} context tokens":"Context awaiting first request";
        meter.Foreground=accent;
        meter.IsVisible=view.Context>0;
        meter.Value=view.Context>0?100.0*Math.Clamp(view.Used,0,view.Context)/view.Context:0;
        jump.Content=view.State switch {MiniRunState.Waiting=>"Review approval",MiniRunState.Finished or MiniRunState.Incomplete=>"Open result",MiniRunState.Working=>"Open run",_=>"Open LocalDesk"};
        previous.IsVisible=view.HasResult&&view.State is MiniRunState.Working or MiniRunState.Waiting;
        return view;
    }
}

public sealed partial class MainWindow
{
    RunMiniWindow? miniWindow;
    long miniRunStarted;
    string miniPhase="Working";
    GenerationSpeed? miniSpeed;
    (Conversation Chat,ChatMessage Reply,GenerationSpeed? Speed)? miniResult;

    void SetMiniPhase(string phase) => miniPhase=phase;

    void StartMiniClock()
    {
        miniRunStarted=Stopwatch.GetTimestamp();
        miniPhase="Starting";
        miniSpeed=null;
    }

    MiniRunView GetMiniSnapshot()
    {
        var live=busy||sendingRequest;
        var result=miniResult is { } receipt&&store.Chats.Contains(receipt.Chat)?miniResult:null;
        var state=approvalWindow!=null?MiniRunState.Waiting:live?MiniRunState.Working:
            result is { } ready?ready.Reply.Successful?MiniRunState.Finished:MiniRunState.Incomplete:MiniRunState.Idle;
        var displayed=!live&&approvalWindow==null&&result is { } latest?latest.Chat:current;
        var phase=state switch
        {
            MiniRunState.Waiting=>"Awaiting approval",
            MiniRunState.Working=>miniPhase,
            MiniRunState.Finished=>"Run finished",
            MiniRunState.Incomplete=>"Run needs attention",
            _=>session.Pending.Count>0&&!queueEnabled?"Queue paused":"Ready"
        };
        var elapsed=live&&miniRunStarted!=0?Stopwatch.GetElapsedTime(miniRunStarted):
            result?.Reply.Diagnostics is { } details?TimeSpan.FromSeconds(details.Total):(TimeSpan?)null;
        var rate=(live?miniSpeed:result?.Speed) is {TokensPerSecond:>0} speedValue
            ?$"{(speedValue.Estimated?"≈":"")}{speedValue.TokensPerSecond:F1} tok/s":"— tok/s";
        return new(state,phase,displayed.Title,LocalModel.FromTag(displayed.Model).Name,elapsed,rate,
            displayed.LastContextUsed,displayed.ContextTokens,session.Pending.Count,session.Pending.Count>0&&!queueEnabled,result!=null);
    }

    void ShowMiniWindow()
    {
        if(miniWindow!=null)
        {
            if(miniWindow.WindowState==WindowState.Minimized)miniWindow.WindowState=WindowState.Normal;
            miniWindow.Activate();
            return;
        }
        var window=new RunMiniWindow(GetMiniSnapshot,OpenMiniDestination,session.MiniTopmost);
        miniWindow=window;
        var screen=Screens.ScreenFromWindow(this)??Screens.Primary;
        var stored=session.MiniX is { } x&&session.MiniY is { } y?new PixelPoint(x,y):(PixelPoint?)null;
        window.WindowStartupLocation=WindowStartupLocation.Manual;
        if(stored is { } position&&Screens.All.Any(item=>item.WorkingArea.Contains(position)))window.Position=position;
        else if(screen!=null)
        {
            var area=screen.WorkingArea;
            window.Position=new PixelPoint(Math.Max(area.X,area.Right-(int)((window.Width+22)*screen.Scaling)),area.Y+(int)(24*screen.Scaling));
        }
        window.Closed+=(_,_)=>
        {
            session.MiniX=window.Position.X;
            session.MiniY=window.Position.Y;
            session.MiniTopmost=window.Topmost;
            miniWindow=null;
            if(!closingWorkspace)PersistWorkspace();
        };
        window.Show();
    }

    void OpenMiniDestination(bool preferLast)
    {
        if((preferLast||(!busy&&!sendingRequest&&approvalWindow==null))&&miniResult is { } result&&store.Chats.Contains(result.Chat))
            FocusRun(result.Chat,result.Reply);
        else FocusRun(current,activeMessage);
    }
}
