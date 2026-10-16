using System.Diagnostics;
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
/// a table sits in its own box that scrolls sideways when it is wider than the pane, so the pane itself never does.
/// Links open in the browser (http and https only).
/// </summary>
internal sealed class MarkdownView : RichTextBox
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    private string _shown = "\u0001";

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true; // links and the table boxes take clicks
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
            if (e.Handled || Keyboard.Modifiers == ModifierKeys.Shift) return; // Shift+wheel moves a table sideways
            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(this) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
        };
    }

    /// <summary>Shows <paramref name="markdown"/>; does nothing when it is what is shown already.</summary>
    public void SetMarkdown(string markdown)
    {
        if (markdown == _shown) return;
        _shown = markdown;
        var doc = NewDocument();
        try
        {
            foreach (var block in Markdown.Parse(markdown)) doc.Blocks.Add(Render(block));
        }
        catch (Exception)
        {
            // Never lose an answer to a rendering problem: show it as plain text.
            doc.Blocks.Clear();
            doc.Blocks.Add(new Paragraph(new Run(markdown)));
        }
        Document = doc;
        AutomationProperties.SetName(this, markdown.Length > 400 ? markdown.Substring(0, 400) + "…" : markdown);
    }

    private static FlowDocument NewDocument()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5, TextAlignment = TextAlignment.Left,
            LineHeight = double.NaN,
        };
        doc.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Text);
        return doc;
    }

    private static Block Render(MdBlock block)
    {
        switch (block)
        {
            case Markdown.Heading h:
            {
                var p = Para(h.Text);
                p.FontWeight = FontWeights.SemiBold;
                p.FontSize = h.Level switch { 1 => 17, 2 => 15, 3 => 13.5, _ => 12.5 };
                p.Margin = new Thickness(0, h.Level <= 2 ? 10 : 8, 0, 4);
                return p;
            }
            case Markdown.Paragraph para:
                return Para(para.Text);
            case Markdown.ListBlock list:
                return RenderList(list);
            case Markdown.Code code:
            {
                var p = new Paragraph(new Run(code.Text))
                {
                    FontFamily = Mono, FontSize = 11.5, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 4, 0, 8),
                    BorderThickness = new Thickness(1),
                };
                p.SetResourceReference(TextElement.BackgroundProperty, Brand.K.Surface);
                p.SetResourceReference(Block.BorderBrushProperty, Brand.K.Border);
                return p;
            }
            case Markdown.Table table:
                return RenderTable(table);
            case Markdown.Quote quote:
            {
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 4, 0, 8) };
                section.SetResourceReference(Block.BorderBrushProperty, Brand.K.Border);
                section.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Muted);
                foreach (var b in Markdown.Parse(quote.Text)) section.Blocks.Add(Render(b));
                return section;
            }
            case Markdown.Rule:
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
                line.SetResourceReference(Border.BackgroundProperty, Brand.K.Border);
                return new BlockUIContainer(line);
            }
            default:
                return new Paragraph();
        }
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
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
                Inline run = new Run(parts[k]);
                if ((span.Style & Markdown.Style.Bold) != 0) run.FontWeight = FontWeights.SemiBold;
                if ((span.Style & Markdown.Style.Italic) != 0) run.FontStyle = FontStyles.Italic;
                if ((span.Style & Markdown.Style.Strike) != 0) run.TextDecorations = TextDecorations.Strikethrough;
                if ((span.Style & Markdown.Style.Code) != 0)
                {
                    run.FontFamily = Mono;
                    run.FontSize = 11.5;
                    run.SetResourceReference(TextElement.BackgroundProperty, Brand.K.Surface);
                }
                if (span.Link is { } url && Markdown.IsUrl(url))
                {
                    var link = new Hyperlink(run) { NavigateUri = new Uri(url), ToolTip = url };
                    link.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Link);
                    link.Click += (_, _) => Open(url);
                    link.RequestNavigate += (_, e) => e.Handled = true;
                    run = link;
                }
                inlines.Add(run);
            }
        }
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
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
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            AddInlines(p.Inlines, item.Text);
            stack[stack.Count - 1].ListItems.Add(new System.Windows.Documents.ListItem(p));
        }
        return root;
    }

    private static System.Windows.Documents.List NewList(bool ordered, int start, int depth) => new()
    {
        MarkerStyle = ordered ? TextMarkerStyle.Decimal : depth == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
        StartIndex = ordered ? Math.Max(1, start) : 1,
        Margin = new Thickness(0, 0, 0, depth == 0 ? 8 : 0),
        Padding = new Thickness(18, 0, 0, 0),
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
                    Child = text, Padding = new Thickness(6, 3, 6, 3),
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
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
        // The wheel scrolls the message list; Shift+wheel or the scroll bar moves the table sideways.
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        };
        return new BlockUIContainer(scroll) { Margin = new Thickness(0, 4, 0, 8) };
    }
}
