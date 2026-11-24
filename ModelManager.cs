using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System.Text.Json.Nodes;
using System.Diagnostics;
namespace LocalDesk;
public static class SummaryPreview
{
 public static string Create(string text){if(text.Length<=700)return text;var lines=text.Split('\n').Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();var labeled=lines.Where(l=>new[]{"outcome","checks","remaining"}.Any(k=>l.TrimStart(' ','-','*','•').StartsWith(k,StringComparison.OrdinalIgnoreCase))).ToList();if(labeled.Count>=3)return string.Join("\n",labeled.Take(3).Select(l=>l.Length>250?l[..247]+"…":l));return text[..Math.Min(650,text.Length)].TrimEnd()+"…";}
}
public sealed partial class MainWindow
{
 async Task RefreshModels()
 {
  var old=changing;changing=true;ollama.ClearCapabilities();
  try{await ollama.Connect(CancellationToken.None);using var r=await ollama.Http.GetAsync("/api/tags");r.EnsureSuccessStatusCode();availableModels=(JsonNode.Parse(await r.Content.ReadAsStringAsync())?["models"]?.AsArray()??[]).Select(m=>m?["name"]?.ToString()??"").Where(n=>n.Length>0&&!n.StartsWith("localdesk-",StringComparison.OrdinalIgnoreCase)).Select(LocalModel.FromTag).OrderBy(m=>m.Id==LocalModel.Fast.Id?0:m.Id==LocalModel.Large.Id?1:2).ToList();}
  catch(Exception e){status.Text="Could not refresh models: "+e.Message;}
  finally{if(!availableModels.Any(m=>m.Id==current.Model))availableModels.Add(LocalModel.FromTag(current.Model));model.ItemsSource=availableModels.ToList();model.SelectedItem=availableModels.First(m=>m.Id==current.Model);ToolTip.SetTip(model,SelectedModel.Note);changing=old;}
 }
 async Task Preferences()
 {
  if(busy)return;
  var dialog=new Window{Title="LocalDesk settings",Width=550,Height=480,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#1C232B")};
  var agentDefault=new CheckBox{Content="Start new chats in Project agent mode",IsChecked=preferences.StartInAgent};
  var startup=new CheckBox{Content="Load a model at startup",IsChecked=preferences.LoadModelAtStartup};
  var alerts=new CheckBox{Content="Show Windows notifications for background runs",IsChecked=preferences.RunNotifications};
  var tokenGuard=new CheckBox{Content="Token Guard · shorten repeated file results",IsChecked=preferences.ReduceDuplicateReads};
  var folder=new TextBox{Text=preferences.Folder,PlaceholderText="Default working folder"};
  var note=new TextBlock{Text="New chats use this folder. Drop files from anywhere; their original paths are available to the agent. Existing chats keep their settings.",TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=13};
  var panel=new StackPanel{Margin=new Thickness(24),Spacing=16};panel.Children.Add(new TextBlock{Text="Make yourself at home",FontSize=22});panel.Children.Add(agentDefault);panel.Children.Add(startup);panel.Children.Add(alerts);panel.Children.Add(tokenGuard);panel.Children.Add(folder);panel.Children.Add(note);
  var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};row.Children.Add(Button("Cancel",(_,_)=>dialog.Close()));row.Children.Add(Button("Save",(_,_)=>{var path=folder.Text?.Trim()??"";if(!Path.IsPathFullyQualified(path)||!Directory.Exists(path)){note.Text="Enter an existing, absolute folder path.";return;}preferences.StartInAgent=agentDefault.IsChecked==true;preferences.LoadModelAtStartup=startup.IsChecked==true;preferences.RunNotifications=alerts.IsChecked==true;preferences.ReduceDuplicateReads=tokenGuard.IsChecked==true;preferences.WorkingFolder=path;preferences.Save(store.Root);dialog.Close();status.Text="Settings saved.";}));panel.Children.Add(row);dialog.Content=panel;await dialog.ShowDialog(this);
 }
 async Task ManageModels()
 {
  if(busy)return;await RefreshModels();
  var dialog=new Window{Title="LocalDesk · Models",Width=700,Height=700,MinHeight=560,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#1C232B")};
  var panel=new StackPanel{Margin=new Thickness(24),Spacing=12};
  var heading=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
  heading.Children.Add(new TextBlock{Text="Model library",FontSize=24,FontWeight=Avalonia.Media.FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center});
  var storage=Button("Storage…",async(_,_)=>await ManageModelStorage(dialog));
  Grid.SetColumn(storage,1);
  heading.Children.Add(storage);
  panel.Children.Add(heading);
  panel.Children.Add(new TextBlock{Text="Download an Ollama model tag, or import local GGUF files. Image input and project tools depend on the model. Models larger than your 16 GB VRAM may run slowly.",TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=13});
  var installed=new ListBox{ItemsSource=availableModels.ToList(),SelectedItem=availableModels.FirstOrDefault(m=>m.Id==current.Model),Height=145};panel.Children.Add(installed);
  var useCase = new TextBlock { FontSize = 14, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
  var capabilities = new TextBlock { FontSize = 12, Foreground = Brush("#AFC9BA"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
  var description = new StackPanel { Spacing = 6, Children = { useCase, capabilities } };
  panel.Children.Add(new Border { Child = description, Background = Brush("#252E38"), Padding = new Thickness(12), CornerRadius = new CornerRadius(8) });

  int modelDetailsVersion = 0;
  async Task ShowModelDetails()
  {
      var version = ++modelDetailsVersion;
      if (installed.SelectedItem is not LocalModel selected)
      {
          useCase.Text = "Select a model to see what it can do.";
          capabilities.Text = "";
          return;
      }
      useCase.Text = selected.Note;
      capabilities.Text = "Checking capabilities…";
      try
      {
          using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
          var values = await ollama.Capabilities(selected.Id, timeout.Token);
          if (version != modelDetailsVersion) return;
          if (values is not { Count: > 0 }) { capabilities.Text = "Ollama did not report capabilities."; return; }
          var labels = new List<string> { values.Contains("vision") ? "Image input" : "Text input only",
              values.Contains("tools") ? "Project agent tools" : "Chat mode only" };
          capabilities.Text = string.Join(" · ", labels);
          if (selected.Id != LocalModel.Fast.Id && selected.Id != LocalModel.Large.Id && selected.Id != LocalModel.Gemma.Id)
              useCase.Text = values.Contains("tools") ? "Try it for project tasks on a small copy first."
                  : "Use it in Chat mode for questions, writing and explanations.";
      }
      catch (Exception)
      {
          if (version == modelDetailsVersion) capabilities.Text = "Could not read capabilities from Ollama.";
      }
  }
  installed.SelectionChanged += async (_, _) => await ShowModelDetails();
  dialog.Opened += async (_, _) => await ShowModelDetails();
  var name=new TextBox{PlaceholderText="Ollama tag to download, or name for GGUF import"};panel.Children.Add(name);
  var progress=new ProgressBar{Minimum=0,Maximum=100,Height=5};panel.Children.Add(progress);
  var info=new TextBlock{Text="Choose an installed model to use, or enter a model tag below.",TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=13};panel.Children.Add(info);
  var localFiles=new List<string>();
  var picker=Button("Choose GGUF files…",async(_,_)=>{var files=await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="Select model GGUF (include matching vision projector if needed)",AllowMultiple=true,FileTypeFilter=[new FilePickerFileType("GGUF models"){Patterns=["*.gguf"]}]});if(files.Count>0){localFiles=files.Select(f=>f.Path.LocalPath).ToList();info.Text=string.Join(", ",localFiles.Select(Path.GetFileName));}});panel.Children.Add(picker);
  panel.Children.Add(new TextBlock{Text="For GGUF import, select the model and its matching vision projector together. Use original names for split shards. Compatibility depends on Ollama support.",TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=12,Foreground=Brush("#A2AAA7")});
  var row=new WrapPanel{Orientation=Orientation.Horizontal};panel.Children.Add(row);
  CancellationTokenSource? operation=null;bool running=false;
  Button? download=null,import=null,use=null,refresh=null;
  void Toggle(bool value){running=value;foreach(var c in new Control[]{download!,import!,use!,refresh!,picker,name,installed,storage})c.IsEnabled=!value;}
  async Task Install(bool fromFile)
  {
   var tag=name.Text?.Trim()??"";
   if(!System.Text.RegularExpressions.Regex.IsMatch(tag,@"^[a-zA-Z0-9][a-zA-Z0-9._:/-]*$")||tag.StartsWith("localdesk-",StringComparison.OrdinalIgnoreCase)||tag.Contains("..")){info.Text="Enter a valid model name, such as qwen3.5:4b or my-model:latest.";return;}
   if(fromFile&&availableModels.Any(m=>m.Id==(tag.Contains(':')?tag:tag+":latest"))){info.Text="That model name already exists. Choose a new name for your import.";return;}
   if(fromFile&&localFiles.Count==0){info.Text="Choose the GGUF files first.";return;}
   operation=new CancellationTokenSource();Toggle(true);progress.Value=0;info.Text=fromFile?"Importing GGUF…":"Downloading model…";
   try
   {
    if(fromFile)
    {
     var file=Path.Combine(store.Root,"import-"+Guid.NewGuid().ToString("N")+".Modelfile");
     try{await File.WriteAllTextAsync(file,string.Join("\n",localFiles.Select(p=>"FROM \""+p.Replace("\\","/")+"\"")),operation.Token);
      var exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ollama","ollama.exe");
      var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in new[]{"create",tag,"-f",file})start.ArgumentList.Add(a);
      using var process=Process.Start(start)??throw new Exception("Could not start Ollama import.");using var registration=operation.Token.Register(()=>{try{process.Kill(true);}catch{}});var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync(operation.Token);await output;var detail=await error;if(process.ExitCode!=0)throw new Exception(detail.Length>600?detail[^600..]:detail);
     }finally{if(File.Exists(file))File.Delete(file);}
    }
    else await ModelDownloads.Pull(ollama,tag,(label,percent)=>{info.Text=label;progress.Value=percent;},operation.Token);
    progress.Value=100;await RefreshModels();installed.ItemsSource=availableModels.ToList();installed.SelectedItem=availableModels.FirstOrDefault(m=>m.Id==(tag.Contains(':')?tag:tag+":latest"));info.Text="Installed. Select it and choose Use model.";
   }
   catch(OperationCanceledException){info.Text="Installation cancelled. Completed downloads remain available to resume.";}
   catch(Exception e){info.Text="Could not install: "+e.Message;}
   finally{operation.Dispose();operation=null;Toggle(false);}
  }
  download=Button("Download",async(_,_)=>await Install(false));import=Button("Import GGUF",async(_,_)=>await Install(true));
  refresh=Button("Refresh",async(_,_)=>{await RefreshModels();installed.ItemsSource=availableModels.ToList();});
  string? selection=null;use=Button("Use model",(_,_)=>{if(installed.SelectedItem is LocalModel m){selection=m.Id;dialog.Close();}});
  foreach(var b in new[]{download,import,refresh,use}){b.Margin=new Thickness(0,0,6,6);row.Children.Add(b);}row.Children.Add(Button("Cancel download",(_,_)=>operation?.Cancel()));
  dialog.Closing+=(_,e)=>{if(running){operation?.Cancel();e.Cancel=true;info.Text="Cancelling… close again when finished.";}};
  dialog.Content=new ScrollViewer{Content=panel};await dialog.ShowDialog(this);
  if(selection!=null){changing=true;model.SelectedItem=availableModels.First(m=>m.Id==selection);changing=false;await SwitchModel(selection);}
 }
}
public static class ModelDownloads
{
 public static async Task Pull(OllamaClient client,string tag,Action<string,double> progress,CancellationToken ct)
 {
  using var r=await client.Post("/api/pull",new{model=tag,stream=true},ct);await OllamaClient.Check(r,ct);using var reader=new StreamReader(await r.Content.ReadAsStreamAsync(ct));bool done=false;
  while(await reader.ReadLineAsync(ct) is {} line){if(string.IsNullOrWhiteSpace(line))continue;var data=JsonNode.Parse(line)!;if(data["error"]!=null)throw new Exception(data["error"]!.ToString());var total=data["total"]?.GetValue<double>()??0;var count=data["completed"]?.GetValue<double>()??0;var label=data["status"]?.ToString()??"Downloading…";done|=label=="success";progress(label+(total>0?$" · {count/1073741824:F2} / {total/1073741824:F2} GB":""),total>0?100*count/total:0);}
  if(!done)throw new Exception("Download ended before Ollama confirmed success. Try again to resume.");
 }
}
