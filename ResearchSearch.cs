using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LocalDesk;

internal sealed record WebHit(string Title, Uri Url, string Summary);

internal static class ResearchSearch
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(20), MaxResponseContentBufferSize=2*1024*1024 };

    internal static async Task<IReadOnlyList<WebHit>> Find(string provider,string query,string? key,CancellationToken ct,HttpClient? client=null)
    {
        var terms=Uri.EscapeDataString(query);
        bool brave=provider=="Brave Search";
        var endpoint=brave?"https://api.search.brave.com/res/v1/web/search?count=8&q="+terms:
            "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=8&srsearch="+terms;
        using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
        request.Headers.UserAgent.ParseAdd("LocalDesk/1.1");
        request.Headers.Accept.ParseAdd("application/json");
        if(brave)
        {
            if(string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("Enter a Brave Search API key for general web search.");
            request.Headers.Add("X-Subscription-Token",key);
        }
        using var response=await (client??Http).SendAsync(request,ct);
        response.EnsureSuccessStatusCode();
        var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var results=(brave?json?["web"]?["results"]:json?["query"]?["search"]) as JsonArray??[];
        return results.OfType<JsonObject>().Select(result =>
        {
            var address=brave?result["url"]?.ToString():"https://en.wikipedia.org/wiki/"+Uri.EscapeDataString((result["title"]?.ToString()??"").Replace(' ','_'));
            if(!Uri.TryCreate(address,UriKind.Absolute,out var url)||url.Scheme is not ("https" or "http")||url.UserInfo.Length>0)return null;
            var title=Clean(result["title"]?.ToString(),150);
            return title.Length==0?null:new WebHit(title,url,Clean(result[brave?"description":"snippet"]?.ToString(),420));
        }).OfType<WebHit>().Take(8).ToArray();
    }

    static string Clean(string? value,int limit)
    {
        var text=WebUtility.HtmlDecode(Regex.Replace(value??"","<[^>]*>",""));
        text=Regex.Replace(text,"\\s+"," ").Trim();
        return text.Length<=limit?text:text[..limit]+"…";
    }
}

public sealed partial class MainWindow
{
    string? braveKey;

    async Task ShowWebSearch()
    {
        if(busy)return;
        var dialog=UtilityWindow("Web search",795,650);
        var layout=new Grid {RowDefinitions=new RowDefinitions("Auto,*,Auto,Auto"),Margin=new Thickness(20)};
        var form=new StackPanel {Spacing=8};
        var provider=new ComboBox {ItemsSource=new[]{"Wikipedia","Brave Search"},SelectedIndex=0};
        var query=new TextBox {PlaceholderText="Search terms",MaxLength=400};
        var key=new TextBox {PasswordChar='●',PlaceholderText="Brave API key for this session",Text=braveKey??Environment.GetEnvironmentVariable("BRAVE_SEARCH_API_KEY"),IsVisible=false};
        var note=EvidenceText("Wikipedia searches its articles. Brave Search covers the general web with your API key.");
        var list=new ListBox {SelectionMode=SelectionMode.Multiple};
        var preview=EvidenceText("");
        preview.Margin=new Thickness(0,10,0,10);
        var add=Button("Add selected citations to draft",null);
        add.IsEnabled=false;
        var search=Button("Search",null);
        provider.SelectionChanged+=(_,_)=>key.IsVisible=provider.SelectedIndex==1;
        list.SelectionChanged+=(_,_)=>
        {
            add.IsEnabled=(list.SelectedItems?.Count??0)>0;
            preview.Text=list.SelectedItem is SearchRow row?row.Hit.Url.AbsoluteUri+"\n"+row.Hit.Summary:"";
        };
        using var lifetime=new CancellationTokenSource();
        dialog.Closed+=(_,_)=>lifetime.Cancel();
        async Task Search()
        {
            var term=query.Text?.Trim();
            if(!search.IsEnabled||string.IsNullOrEmpty(term))return;
            search.IsEnabled=false;
            list.ItemsSource=null;
            note.Text="Searching…";
            var selected=provider.SelectedItem?.ToString()??"Wikipedia";
            if(selected=="Brave Search")braveKey=key.Text?.Trim();
            try
            {
                var results=await ResearchSearch.Find(selected,term,braveKey,lifetime.Token);
                list.ItemsSource=results.Select(result=>new SearchRow(result)).ToArray();
                note.Text=results.Count==0?"No results found.":"Select sources to add to the draft. Open a result to read the full page.";
            }
            catch(Exception error) when(error is HttpRequestException or OperationCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
            {if(!lifetime.IsCancellationRequested)note.Text="Search failed. "+error.Message;}
            finally {search.IsEnabled=true;}
        }
        search.Click+=async(_,_)=>await Search();
        query.KeyDown+=async(_,e)=>{if(e.Key==Avalonia.Input.Key.Enter){e.Handled=true;await Search();}};
        foreach(var control in new Control[]{provider,query,key,search,note})form.Children.Add(control);
        layout.Children.Add(form);
        Grid.SetRow(list,1);
        layout.Children.Add(list);
        var buttons=new WrapPanel {Orientation=Orientation.Horizontal};
        buttons.Children.Add(add);
        buttons.Children.Add(Button("Open selected source",(_,_)=>
        {
            if(list.SelectedItem is not SearchRow row)return;
            try{Process.Start(new ProcessStartInfo(row.Hit.Url.AbsoluteUri){UseShellExecute=true});}
            catch(Exception error){note.Text=error.Message;}
        }));
        add.Click+=(_,_)=>
        {
            var citations=list.SelectedItems?.OfType<SearchRow>().Select((row,i)=>$"[{i+1}] {row.Hit.Title}\n{row.Hit.Url.AbsoluteUri}\n{row.Hit.Summary}").ToArray()??[];
            if(citations.Length>0)input.Text=(input.Text??"").TrimEnd()+"\n\nWeb search results (reference snippets, not full pages):\n"+string.Join("\n\n",citations);
            dialog.Close();
        };
        Grid.SetRow(preview,2);
        layout.Children.Add(preview);
        Grid.SetRow(buttons,3);
        layout.Children.Add(buttons);
        dialog.Content=layout;
        await dialog.ShowDialog(this);
    }

    sealed record SearchRow(WebHit Hit)
    {
        public override string ToString()=>Hit.Title+" · "+Hit.Url.Host;
    }
}
