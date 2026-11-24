using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LocalDesk;

public sealed partial class MainWindow
{
    static readonly MarkdownPipeline ChatMarkdown = new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().Build();
    static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
    readonly Dictionary<string, Control> messageCards = new(StringComparer.Ordinal);
    readonly Button jumpToLatest = Button("↓  Jump to latest", null, "#284345");
    bool followChat = true;

    void ConfigureChatScroll(Grid pane)
    {
        jumpToLatest.IsVisible = false;
        jumpToLatest.HorizontalAlignment = HorizontalAlignment.Center;
        jumpToLatest.VerticalAlignment = VerticalAlignment.Bottom;
        jumpToLatest.Margin = new Thickness(0, 0, 0, 12);
        jumpToLatest.Click += (_, _) => FollowLatest();
        pane.Children.Add(jumpToLatest);
        scroller.ScrollChanged += (_, e) =>
        {
            var nearBottom = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y < 42;
            if (nearBottom) followChat = true;
            else if (Math.Abs(e.OffsetDelta.Y) > .01 && Math.Abs(e.ExtentDelta.Y) < .01) followChat = false;
            jumpToLatest.IsVisible = !followChat;
        };
    }

    void FollowLatest()
    {
        followChat = true;
        jumpToLatest.IsVisible = false;
        Scroll();
    }

    Control RenderMarkdown(string markdown, double fontSize = 15)
    {
        var content = new StackPanel { Spacing = 9 };
        if (markdown.Length > 100_000) { content.Children.Add(ChatText(markdown, fontSize)); return content; }
        foreach (var block in Markdown.Parse(markdown, ChatMarkdown)) content.Children.Add(RenderBlock(block, fontSize));
        return content;
    }

    Control RenderBlock(Block block, double fontSize)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var label = InlineText(heading.Inline, fontSize + Math.Max(0, 7 - heading.Level * 2));
                label.FontWeight = FontWeight.SemiBold;
                return label;
            }
            case ThematicBreakBlock:
                return new Border { Height = 1, Background = Brush("#3A4858") };
            case CodeBlock code:
                return CodeCard(code.Lines.ToString(), (code as FencedCodeBlock)?.Info);
            case QuoteBlock quote:
                return new Border { Child = BlockGroup(quote, fontSize), Padding = new Thickness(12, 5),
                    BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = Brush("#74A89F"), Background = Brush("#212B35") };
            case ListBlock list:
            {
                var rows = new StackPanel { Spacing = 5 };
                if (!int.TryParse(list.OrderedStart, out var number)) number = 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    var marker = ChatText(list.IsOrdered ? $"{number++}." : "•", fontSize);
                    marker.Margin = new Thickness(0, 0, 9, 0);
                    row.Children.Add(marker);
                    var body = BlockGroup(item, fontSize);
                    Grid.SetColumn(body, 1);
                    row.Children.Add(body);
                    rows.Children.Add(row);
                }
                return rows;
            }
            case Table table:
            {
                var rows = table.OfType<TableRow>().ToList();
                var grid = new Grid { ColumnSpacing = 1, RowSpacing = 1, Background = Brush("#3A4858") };
                var columnCount = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
                for (int i = 0; i < columnCount; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                for (int row = 0; row < rows.Count; row++)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    for (int column = 0; column < rows[row].Count; column++)
                    {
                        if (rows[row][column] is not TableCell cell) continue;
                        var box = new Border { Child = BlockGroup(cell, fontSize - 1), Padding = new Thickness(8), Background = Brush(rows[row].IsHeader ? "#293A47" : "#222C37") };
                        Grid.SetRow(box, row);
                        Grid.SetColumn(box, column);
                        grid.Children.Add(box);
                    }
                }
                return grid;
            }
            case LeafBlock leaf:
                return leaf.Inline == null ? ChatText(leaf.Lines.ToString(), fontSize) : InlineText(leaf.Inline, fontSize);
            case ContainerBlock container:
                return BlockGroup(container, fontSize);
            default:
                return ChatText("", fontSize);
        }
    }

    StackPanel BlockGroup(ContainerBlock block, double fontSize)
    {
        var panel = new StackPanel { Spacing = 5 };
        foreach (var item in block) panel.Children.Add(RenderBlock(item, fontSize));
        return panel;
    }

    static SelectableTextBlock ChatText(string text, double size) => new()
    { Text = text, FontSize = size, LineHeight = size * 1.5, TextWrapping = TextWrapping.Wrap };

    SelectableTextBlock InlineText(ContainerInline? root, double size)
    {
        var text = ChatText("", size);
        if (root != null) foreach (var child in root) AddInline(text.Inlines!, child);
        return text;
    }

    void AddInline(InlineCollection runs, Markdig.Syntax.Inlines.Inline item)
    {
        switch (item)
        {
            case LiteralInline literal: runs.Add(new Run(literal.Content.ToString())); break;
            case CodeInline code: runs.Add(new Run(code.Content) { FontFamily = CodeFont, Foreground = Brush("#B8DFD6") }); break;
            case LineBreakInline line:
                if (line.IsHard) runs.Add(new Avalonia.Controls.Documents.LineBreak());
                else runs.Add(new Run(" "));
                break;
            case HtmlEntityInline entity: runs.Add(new Run(entity.Transcoded.ToString())); break;
            case EmphasisInline emphasis:
            {
                var span = new Span { FontWeight = emphasis.DelimiterCount == 2 ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = emphasis.DelimiterCount == 1 ? FontStyle.Italic : FontStyle.Normal };
                foreach (var child in emphasis) AddInline(span.Inlines!, child);
                runs.Add(span);
                break;
            }
            case LinkInline { IsImage: false, Url: { } link } anchor when WebAddress(link) is { } address:
            {
                var display = string.Concat(anchor.Select(child => child is LiteralInline literal ? literal.Content.ToString() : child is CodeInline code ? code.Content : ""));
                AddWebLink(runs, display.Length == 0 ? link : display, address);
                break;
            }
            case AutolinkInline auto when WebAddress(auto.Url) is { } address:
                AddWebLink(runs, auto.Url, address);
                break;
            case AutolinkInline auto: runs.Add(new Run(auto.Url)); break;
            case ContainerInline container:
                foreach (var child in container) AddInline(runs, child);
                break;
        }
    }

    static Uri? WebAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var address) && address.Scheme is "https" or "http" ? address : null;

    void AddWebLink(InlineCollection runs, string label, Uri address)
    {
        var link = Button(label, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { status.Text = "Could not open link. " + error.Message; }
        }, "#263A3B");
        link.Padding = new Thickness(3, 0);
        ToolTip.SetTip(link, address.AbsoluteUri);
        runs.Add(new InlineUIContainer { Child = link });
    }

    Control CodeCard(string code, string? language)
    {
        language = language?.Trim().ToLowerInvariant() ?? "";
        var caption = new DockPanel { Margin = new Thickness(12, 6, 8, 6) };
        var copy = Button("Copy code", async (_, _) =>
        {
            if (Clipboard is not { } clipboard) return;
            try { await clipboard.SetTextAsync(code); status.Text = "Code copied."; }
            catch (Exception error) { status.Text = "Could not copy code. " + error.Message; }
        });
        DockPanel.SetDock(copy, Dock.Right);
        caption.Children.Add(copy);
        caption.Children.Add(new TextBlock { Text = language.Length > 0 ? language : "Code", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#ABC5C4"), FontSize = 12 });
        var text = new SelectableTextBlock { FontFamily = CodeFont, FontSize = 13, Margin = new Thickness(12) };
        if (code.Length > 12_000 || !CodeSyntax.TryGetValue(language, out var pattern)) text.Text = code;
        else
        {
            int offset = 0;
            foreach (Match match in pattern.Matches(code))
            {
                if (match.Index > offset) text.Inlines!.Add(new Run(code[offset..match.Index]));
                var color = match.Groups["comment"].Success ? "#95A6AA" : match.Groups["literal"].Success ? "#B1D998" : match.Groups["number"].Success ? "#F4CD94" : "#CDB4FA";
                text.Inlines!.Add(new Run(match.Value) { Foreground = Brush(color) });
                offset = match.Index + match.Length;
            }
            if (offset < code.Length) text.Inlines!.Add(new Run(code[offset..]));
        }
        var scroll = new ScrollViewer { Content = text, MaxHeight = 460, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        return new Border { CornerRadius = new CornerRadius(9), Background = Brush("#111820"), Child = new StackPanel { Children = { caption, scroll } } };
    }

    static readonly Dictionary<string, Regex> CodeSyntax = CreateCodeSyntax();

    static Dictionary<string, Regex> CreateCodeSyntax()
    {
        var rules = new Dictionary<string, Regex>();
        void Add(string aliases, string comments, string keywords)
        {
            var pattern = new Regex("(?<comment>" + comments + ")|" + """(?<literal>"(?:\\.|[^"\\\r\n])*"|'(?:\\.|[^'\\\r\n])*')|(?<number>\b\d+(?:\.\d+)?\b)|\b(""" + keywords + @")\b", RegexOptions.Compiled);
            foreach (var alias in aliases.Split(' ')) rules.Add(alias, pattern);
        }
        Add("cs csharp c cpp java javascript js ts typescript jsx tsx", @"//[^\r\n]*|/\*[\s\S]*?\*/", "using|class|public|private|static|async|await|void|return|new|if|else|for|foreach|while|true|false|null|var|let|const|function|import|export|namespace|string|int|bool|record|struct|interface|try|catch|throw");
        Add("python py shell bash sh powershell ps1 yaml yml", @"\x23[^\r\n]*", "def|class|from|import|raise|return|if|elif|else|for|while|in|True|False|None|with|async|await|try|except|function|foreach|param|throw|true|false|null|then|fi");
        Add("json jsonc", @"//[^\r\n]*", "true|false|null");
        Add("sql", @"--[^\r\n]*", "SELECT|FROM|WHERE|ORDER|BY|JOIN|AS|INSERT|UPDATE|DELETE|GROUP|select|from|where|order|by|join|as|insert|update|delete|group");
        return rules;
    }
}
