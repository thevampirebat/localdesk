using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalDesk;

public sealed partial class MainWindow
{
    async Task ShowContextInspector()
    {
        if(busy)return;
        var dialog=UtilityWindow("Context breakdown",610,480);
        var panel=new StackPanel {Margin=new Thickness(20),Spacing=9};
        panel.Children.Add(EvidenceText($"{current.LastContextUsed:N0} / {current.ContextTokens:N0} tokens on the latest reading"));
        if(current.LastBreakdown is { } budget)
        {
            var rows=new (string,int)[] {("Conversation",budget.Conversation),("Recognized file excerpts and images",budget.Files),
                ("Tool results",budget.ToolResults),("System instructions",budget.Instructions),("Tool definitions",budget.Definitions),
                ("Reserved output",budget.Reserved),("Automatic compaction threshold",budget.Budget)};
            foreach(var row in rows)panel.Children.Add(EvidenceText($"{row.Item1} · {row.Item2:N0} tokens"));
            panel.Children.Add(EvidenceText("Input categories are estimates from the latest prepared request. Server usage also includes generated tokens."));
        }
        else panel.Children.Add(EvidenceText("Send a request to measure the input categories."));
        panel.Children.Add(Button("Preview compaction…",(_,_)=>dialog.Close(true)));
        dialog.Content=new ScrollViewer {Content=panel};
        if(await dialog.ShowDialog<bool>(this))await CompactContext(true);
    }

    async Task<string?> ConfirmCompaction(string summary,CancellationToken ct)
    {
        var dialog=UtilityWindow("Compaction preview",850,660);
        var layout=new Grid {Margin=new Thickness(20),RowDefinitions=new RowDefinitions("Auto,*,Auto")};
        layout.Children.Add(EvidenceText("Review or edit what LocalDesk will carry into the next model request."));
        var text=new TextBox {Text=summary,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12)};
        Grid.SetRow(text,1);
        layout.Children.Add(text);
        var controls=new WrapPanel();
        controls.Children.Add(Button("Apply notes",(_,_)=>{if(!string.IsNullOrWhiteSpace(text.Text))dialog.Close(text.Text.Trim());}));
        controls.Children.Add(Button("Keep current context",(_,_)=>dialog.Close()));
        Grid.SetRow(controls,2);
        layout.Children.Add(controls);
        dialog.Content=layout;
        using var registration=ct.Register(()=>Dispatcher.UIThread.Post(()=>dialog.Close()));
        ct.ThrowIfCancellationRequested();
        var result=await dialog.ShowDialog<string?>(this);
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
