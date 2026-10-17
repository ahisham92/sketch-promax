using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SketchProMax.UI;
using MdBlock = SketchProMax.Chat.Markdown.Block;

namespace SketchProMax.Chat;

/// <summary>
/// Shows Markdown as a read-only, selectable FlowDocument in the palette's colours. Text wraps to the pane's width;
/// tables and code sit in their own boxes that scroll sideways when wider than the pane, so the pane itself never does.
/// Code blocks have a language label and a Copy button. Links open in the browser (http and https only).
///
/// While an answer streams, only the blocks whose source changed are drawn again (usually just the last one), and a
/// growing code block keeps its box and only gets the new text: no flicker, and the cost per update stays small.
/// </summary>
internal sealed class MarkdownView : RichTextBox
{
    private sealed class Drawn
    {
        public string Source = "";
        public Block Element = null!;
        public TextBox? Code;
        public string Language = "";
    }

    private string _shown = "\u0001";
    private readonly List<Drawn> _drawn = [];
    private bool _plain;

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true; // links, code and table boxes take clicks
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        Focusable = true;
        IsTabStop = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, Brand.K.Text);
        SetResourceReference(SelectionBrushProperty, Brand.K.Accent);
        Document = NewDocument();
        // The message list scrolls, not each message: hand the wheel to the list.
        PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled || Keyboard.Modifiers == ModifierKeys.Shift) return; // Shift+wheel moves a table or code sideways
            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(this) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
        };
    }

    /// <summary>Shows <paramref name="markdown"/>, drawing again only the blocks that changed.</summary>
    public void SetMarkdown(string markdown)
    {
        if (markdown == _shown) return;
        _shown = markdown;
        var doc = Document;
        try
        {
            if (_plain)
            {
                doc.Blocks.Clear();
                _plain = false;
            }
            var parsed = Markdown.ParseWithSource(markdown);
            var from = StreamRender.FirstChanged(_drawn.Select(d => d.Source).ToList(), parsed.Select(p => p.Source).ToList());
            // A code block that is still streaming: give its box the new text instead of drawing it again.
            if (from < _drawn.Count && from < parsed.Count && parsed[from].Block is Markdown.Code code &&
                _drawn[from].Code is { } box && _drawn[from].Language == code.Language)
            {
                if (box.Text != code.Text) box.Text = code.Text;
                _drawn[from].Source = parsed[from].Source;
                from++;
            }
            for (var k = _drawn.Count - 1; k >= from; k--)
            {
                doc.Blocks.Remove(_drawn[k].Element);
                _drawn.RemoveAt(k);
            }
            for (var k = from; k < parsed.Count; k++)
            {
                var drawn = new Drawn { Source = parsed[k].Source };
                drawn.Element = Render(parsed[k].Block, drawn);
                doc.Blocks.Add(drawn.Element);
                _drawn.Add(drawn);
            }
        }
        catch (Exception)
        {
            // Never lose an answer to a rendering problem: show it as plain text, and draw it whole next time.
            doc.Blocks.Clear();
            _drawn.Clear();
            doc.Blocks.Add(new Paragraph(new Run(markdown)));
            _plain = true;
        }
        AutomationProperties.SetName(this, markdown.Length > 400 ? markdown.Substring(0, 400) + "…" : markdown);
    }

    private static FlowDocument NewDocument()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = ChatUi.BodySize, TextAlignment = TextAlignment.Left,
            LineHeight = double.NaN,
        };
        doc.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Text);
        return doc;
    }

    private static Block Render(MdBlock block, Drawn? drawn = null)
    {
        switch (block)
        {
            case Markdown.Heading h:
            {
                var p = Para(h.Text);
                p.FontWeight = FontWeights.SemiBold;
                p.FontSize = h.Level switch { 1 => 17, 2 => 15, 3 => 13.5, _ => ChatUi.BodySize };
                p.Margin = new Thickness(0, h.Level <= 2 ? ChatUi.S3 : ChatUi.S2, 0, ChatUi.S1);
                if (h.Level <= 2)
                {
                    p.BorderThickness = new Thickness(0, 0, 0, 1);
                    p.Padding = new Thickness(0, 0, 0, 2);
                    p.SetResourceReference(Block.BorderBrushProperty, Brand.K.Border);
                }
                return p;
            }
            case Markdown.Paragraph para:
                return Para(para.Text);
            case Markdown.ListBlock list:
                return RenderList(list);
            case Markdown.Code code:
                return RenderCode(code, drawn);
            case Markdown.Table table:
                return RenderTable(table);
            case Markdown.Quote quote:
            {
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S2) };
                section.SetResourceReference(Block.BorderBrushProperty, Brand.K.Border);
                section.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Muted);
                foreach (var b in Markdown.Parse(quote.Text)) section.Blocks.Add(Render(b));
                return section;
            }
            case Markdown.Rule:
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, ChatUi.S2, 0, ChatUi.S2) };
                line.SetResourceReference(Border.BackgroundProperty, Brand.K.Border);
                return new BlockUIContainer(line);
            }
            default:
                return new Paragraph();
        }
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, ChatUi.S2) };
        AddInlines(p.Inlines, text);
        return p;
    }

    /// <summary>Adds Markdown inline text (bold, italic, code, links; newlines as line breaks).</summary>
    public static void AddInlines(InlineCollection inlines, string text)
    {
        foreach (var span in Markdown.Inline(text))
        {
            var parts = span.Text.Split('\n');
            for (var k = 0; k < parts.Length; k++)
            {
                if (k > 0) inlines.Add(new LineBreak());
                if (parts[k].Length == 0) continue;
                var code = (span.Style & Markdown.Style.Code) != 0;
                // Thin spaces either side of inline code stand in for padding, which a Run doesn't have.
                Inline run = new Run(code ? "\u2009" + parts[k] + "\u2009" : parts[k]);
                if ((span.Style & Markdown.Style.Bold) != 0) run.FontWeight = FontWeights.SemiBold;
                if ((span.Style & Markdown.Style.Italic) != 0) run.FontStyle = FontStyles.Italic;
                if ((span.Style & Markdown.Style.Strike) != 0) run.TextDecorations = TextDecorations.Strikethrough;
                if (code)
                {
                    run.FontFamily = ChatUi.Mono;
                    run.FontSize = ChatUi.CodeSize;
                    run.SetResourceReference(TextElement.BackgroundProperty, Brand.K.SurfaceHover);
                }
                if (span.Link is { } url && Markdown.IsUrl(url))
                {
                    var link = new Hyperlink(run) { NavigateUri = new Uri(url), ToolTip = url };
                    link.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Link);
                    link.Click += (_, _) => ChatUi.Open(url);
                    link.RequestNavigate += (_, e) => e.Handled = true;
                    AutomationProperties.SetHelpText(link, "Opens " + url + " in the browser");
                    run = link;
                }
                inlines.Add(run);
            }
        }
    }

    /// <summary>A code block: language and Copy on top, the code in a monospace box that scrolls sideways when wide.</summary>
    private static Block RenderCode(Markdown.Code code, Drawn? drawn)
    {
        var text = new TextBox
        {
            Text = code.Text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, FontFamily = ChatUi.Mono,
            FontSize = ChatUi.CodeSize, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2), MinHeight = 0, IsTabStop = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        text.SetResourceReference(Control.ForegroundProperty, Brand.K.Text);
        var label = code.Language.Length > 0 ? code.Language : "code";
        AutomationProperties.SetName(text, $"Code ({label})");
        var scroll = new ScrollViewer
        {
            Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        SideScroll(scroll);

        // A header bar (language and Copy) in a slightly deeper tint over the code, inside one rounded box.
        var header = new DockPanel { Margin = new Thickness(ChatUi.S3, 2, ChatUi.S1, 2) };
        var copy = ChatUi.CopyButton(() => text.Text, "code");
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(ChatUi.Small(label));
        var bar = new Border { Child = header, CornerRadius = new CornerRadius(ChatUi.RadiusSmall - 1, ChatUi.RadiusSmall - 1, 0, 0) };
        bar.SetResourceReference(Border.BackgroundProperty, Brand.K.SurfaceHover);

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(scroll);
        var box = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(ChatUi.RadiusSmall) };
        box.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        box.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        if (drawn is not null)
        {
            drawn.Code = text;
            drawn.Language = code.Language;
        }
        return new BlockUIContainer(box) { Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S2) };
    }

    /// <summary>The wheel scrolls the message list; Shift+wheel or the scroll bar moves a wide box sideways.</summary>
    private static void SideScroll(ScrollViewer scroll)
    {
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Shift) return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
    }

    private static Block RenderList(Markdown.ListBlock block)
    {
        // Nested lists: a stack of List elements, one per depth.
        var root = NewList(block.Items.Count > 0 && block.Items[0].Ordered, block.Items.Count > 0 ? block.Items[0].Number : 1, 0);
        var stack = new List<System.Windows.Documents.List> { root };
        foreach (var item in block.Items)
        {
            while (stack.Count - 1 > item.Depth) stack.RemoveAt(stack.Count - 1);
            while (stack.Count - 1 < item.Depth)
            {
                var parent = stack[stack.Count - 1];
                if (parent.ListItems.Count == 0) parent.ListItems.Add(new System.Windows.Documents.ListItem());
                var nested = NewList(item.Ordered, item.Number, stack.Count);
                parent.ListItems.LastListItem.Blocks.Add(nested);
                stack.Add(nested);
            }
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AddInlines(p.Inlines, item.Text);
            stack[stack.Count - 1].ListItems.Add(new System.Windows.Documents.ListItem(p));
        }
        return root;
    }

    private static System.Windows.Documents.List NewList(bool ordered, int start, int depth) => new()
    {
        MarkerStyle = ordered ? TextMarkerStyle.Decimal : depth == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
        StartIndex = ordered ? Math.Max(1, start) : 1,
        Margin = new Thickness(0, 0, 0, depth == 0 ? ChatUi.S2 : 0),
        Padding = new Thickness(20, 0, 0, 0),
    };

    private static Block RenderTable(Markdown.Table table)
    {
        var grid = new Grid();
        var columns = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Row(IReadOnlyList<string> cells, int row, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns; c++)
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 260, FontSize = 12 };
                AddInlines(text.Inlines, c < cells.Count ? cells[c] : "");
                if (header) text.FontWeight = FontWeights.SemiBold;
                var align = c < table.Align.Count ? table.Align[c] : 'l';
                text.TextAlignment = align == 'r' ? TextAlignment.Right : align == 'c' ? TextAlignment.Center : TextAlignment.Left;
                var cell = new Border
                {
                    Child = text, Padding = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S2, ChatUi.S1),
                    BorderThickness = new Thickness(0, 0, c == columns - 1 ? 0 : 1, 1),
                };
                cell.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
                if (header) cell.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        Row(table.Header, 0, header: true);
        for (var r = 0; r < table.Rows.Count; r++) Row(table.Rows[r], r + 1, header: false);
        var scroll = new ScrollViewer
        {
            Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, IsTabStop = true,
        };
        AutomationProperties.SetName(scroll, $"Table, {columns} columns, {table.Rows.Count} rows. Scroll sideways if it is wider than the pane.");
        SideScroll(scroll);
        // The outer frame draws the top and side edges; the cells draw the lines between them and the bottom edge.
        var frame = new Border { Child = scroll, BorderThickness = new Thickness(1, 1, 1, 0), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        frame.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        return new BlockUIContainer(frame) { Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S2) };
    }
}
