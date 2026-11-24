using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Text;

namespace LocalDesk;

static class PreviewFiles
{
    public static async Task<(string Text, bool Truncated)> ReadText(string path, int limit, CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[limit + 1];
        int read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return (new string(buffer, 0, Math.Min(limit, read)), read > limit);
    }

    public static Bitmap Image(string path, int width)
    {
        using var codec = SkiaSharp.SKCodec.Create(path) ?? throw new IOException("Could not decode this image.");
        var size = codec.Info;
        using var stream = File.OpenRead(path);
        if (Math.Max(size.Width, size.Height) <= width) return new Bitmap(stream);
        return size.Width >= size.Height
            ? Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality)
            : Bitmap.DecodeToHeight(stream, width, BitmapInterpolationMode.MediumQuality);
    }

    public static async Task<string> ReadAround(string path,int line,CancellationToken ct)
    {
        using var reader=new StreamReader(path,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
        var output=new StringBuilder();
        for(int number=1;number<=line+20&&await reader.ReadLineAsync(ct) is { } text;number++)
            if(number>=Math.Max(1,line-12))output.Append(number==line?"> ":"  ").Append(number).Append("  ").AppendLine(text.Length>900?text[..900]+"…":text);
        return output.Length==0?"The cited line is no longer available. Refresh the index to update this result.":output.ToString();
    }
}

public sealed partial class MainWindow
{
    async Task PreviewFile(string path, Window? owner = null, string? original = null, string? name = null, bool fullToolOutput = false,int? firstLine=null)
    {
        var dialog = UtilityWindow("Preview · " + (name ?? Path.GetFileName(path)), 1000, 740);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20) };
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = name ?? Path.GetFileName(path), FontSize = 20, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(EvidenceText(original ?? path));
        layout.Children.Add(header);
        var view = new ScrollViewer { Background = Brush("#141A20"), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(view, 1);
        layout.Children.Add(view);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        var reveal = Button("Show in Explorer", (_, _) => RevealFile(original != null && File.Exists(original) ? original : path));
        var close = Button("Close", (_, _) => dialog.Close());
        reveal.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(reveal);
        actions.Children.Add(close);
        dialog.Content = layout;

        using var lifetime = new CancellationTokenSource();
        Bitmap? bitmap = null;
        dialog.Closed += (_, _) => lifetime.Cancel();
        dialog.Opened += async (_, _) =>
        {
            var ct = lifetime.Token;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException("This file is no longer available.");
                header.Children.Add(EvidenceText($"{file.Length:N0} bytes" + (original == null ? "" : " · Saved attachment copy")));
                if (AttachedFiles.IsImage(path) && file.Length <= 30 * 1024 * 1024)
                {
                    bitmap = await Task.Run(() => PreviewFiles.Image(path, 1400), ct);
                    if (ct.IsCancellationRequested) { bitmap.Dispose(); bitmap = null; return; }
                    view.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 580, Margin = new Thickness(16) };
                }
                else if (AttachedFiles.HasTextExtension(path))
                {
                    var result = firstLine is { } line?(Text:await PreviewFiles.ReadAround(path,line,ct),Truncated:false):
                        fullToolOutput ? (Text: await File.ReadAllTextAsync(path, ct), Truncated: false) : await PreviewFiles.ReadText(path, 150_000, ct);
                    if (ct.IsCancellationRequested) return;
                    view.Content = new SelectableTextBlock
                    {
                        Text = result.Text + (result.Truncated ? "\n\n[Preview limited to 150,000 characters. Open the file from Explorer for the full contents.]" : ""),
                        Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 13
                    };
                }
                else
                {
                    view.Content = new TextBlock { Text = "This file type or size can be opened from Explorer.", Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!ct.IsCancellationRequested)
                    view.Content = new TextBlock { Text = "Preview unavailable. " + error.Message, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
            }
        };
        try { await dialog.ShowDialog(owner ?? this); }
        finally { bitmap?.Dispose(); }
    }

    Button PreviewLink(string title, string path, Window? owner = null, string? original = null, string? name = null)
    {
        var button = Button(title, async (_, _) => await PreviewFile(path, owner, original, name));
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(button, original ?? path);
        EnableFileDrag(button, original != null && File.Exists(original) ? original : path);
        return button;
    }

    Button AttachmentLink(FileAttachment file)
    {
        var saved = File.Exists(file.StoredPath);
        return PreviewLink(file.Name, saved ? file.StoredPath : file.SourcePath,
            original: saved ? file.SourcePath : null, name: file.Name);
    }

    Button ImagePreviewLink(string path, int width, int height)
    {
        var button = PreviewLink("Open image preview", path, name: "Attached image");
        try
        {
            var bitmap = PreviewFiles.Image(path, width * 2);
            button.Content = new Image { Source = bitmap, Width = width, Height = height, Stretch = Stretch.Uniform };
            button.DetachedFromVisualTree += (_, _) => bitmap.Dispose();
        }
        catch (Exception) { }
        return button;
    }
}
