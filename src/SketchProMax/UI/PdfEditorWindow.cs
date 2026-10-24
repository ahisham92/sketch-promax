using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace SketchProMax.UI;

/// <summary>
/// The PDF editor (Documents panel): every page of a PDF as a picture, to pick with a click (Ctrl and Shift for more)
/// and then delete, turn, move (drag, or Earlier and Later), replace with pages of another PDF, add a PDF or a blank
/// page, extract, or split. Several PDFs merge by adding one to another, or by dropping them on the window. Nothing is
/// stored in the model and nothing is written until Save as (a folder and name you pick) or Save over (which keeps the
/// old file in Documents\Sketch ProMax\PDF backups); Ctrl+Z undoes.
/// Pages are drawn by PDFium off Revit's thread, only those in view, so a PDF of thousands of pages opens at once.
/// The window is modeless; nothing in the model is read or changed.
/// </summary>
internal sealed class PdfEditorWindow : Window
{
    private static readonly string Backups = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "PDF backups");

    /// <summary>Picture box sizes (width in device-independent pixels) for Small, Medium and Large.</summary>
    private static readonly int[] Sizes = [110, 160, 240];

    private PdfEditSession? _doc;
    private readonly PageList _grid = new();
    private readonly Dictionary<PdfPage, Tile> _tiles = new(new SameObject());
    private readonly List<Tile> _order = [];
    private readonly Grid _empty = new();
    private readonly TextBlock _counts = Brand.Text("", Brand.K.Muted, 12);
    private readonly TextBlock _note = Brand.Text("", Brand.K.Text, 12);
    private readonly ProgressBar _bar = new() { Height = 6, Width = 160, Minimum = 0, Maximum = 1, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _zoom = new() { Width = 96, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _open, _add, _blank, _left, _right, _earlier, _later, _delete, _replace, _extract, _split, _undo, _redo, _save, _saveAs, _show;
    private readonly List<Button> _needsDoc = [], _needsSelection = [];
    private readonly Border _header;
    private ScrollViewer? _scroller;
    private readonly DispatcherTimer _viewTimer;
    private readonly ThumbCache _cache = new(160L * 1024 * 1024);
    private int _generation;
    private bool _busy;
    private string? _lastOutput;
    private CancellationTokenSource? _running;

    public static Autodesk.Revit.UI.Result Open(string? file = null)
    {
        var window = new PdfEditorWindow();
        window.Show();
        if (file is not null && File.Exists(file)) _ = window.OpenFiles([file]);
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    /// <summary>The window, not shown (the field test builds it).</summary>
    internal static Window ForTest() => new PdfEditorWindow();

    private PdfEditorWindow()
    {
        Title = "Sketch ProMax: PDF editor";
        Width = 1120;
        Height = 780;
        MinWidth = 640;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
        SetResourceReference(BackgroundProperty, Brand.K.Bg);

        // ---- Toolbar: groups of what you do to a document, then to the picked pages.
        _open = Tool(RichUi.Icon.Open, "Open", "Open a PDF (Ctrl+O). Several at once are merged into one.", () => _ = OpenPicked(), false);
        _add = Tool(RichUi.Icon.Add, "Add PDF", "Adds the pages of other PDFs after the picked page, or at the end: merges PDFs.", () => _ = AddPdf(), true);
        _blank = Tool(Page, "Blank page", "Adds a blank page after the picked page, the size of its neighbour.", AddBlank, true);
        _left = Tool(Turn, "", "Turn the picked pages left (Ctrl+L).", () => Rotate(-1), true, true);
        Mirror(_left);
        _right = Tool(Turn, "", "Turn the picked pages right (Ctrl+R).", () => Rotate(1), true, true);
        _earlier = Tool(RichUi.Icon.Back, "", "Move the picked pages one place earlier (Ctrl+Left). You can also drag pages.", () => Nudge(-1), true, true);
        _later = Tool(RichUi.Icon.Next, "", "Move the picked pages one place later (Ctrl+Right). You can also drag pages.", () => Nudge(1), true, true);
        _replace = Tool(Swap, "Replace", "Replaces the picked pages with pages of another PDF.", () => _ = ReplacePicked(), true, true);
        _delete = Tool(RichUi.Icon.Delete, "Delete", "Deletes the picked pages (Delete key). Ctrl+Z brings them back.", DeletePicked, true, true);
        _extract = Tool(RichUi.Icon.Export, "Extract", "Saves the picked pages as a new PDF (Ctrl+E). This document doesn't change.", () => _ = Extract(), true, true);
        _split = Tool(Cut, "Split", "Cuts this document into several PDFs: every few pages, at the picked pages, or by size.", () => _ = SplitDoc(), true);
        _undo = Tool(RichUi.Icon.Undo, "", "Undo (Ctrl+Z).", Undo, true);
        _redo = Tool(Redo, "", "Redo (Ctrl+Y).", RedoEdit, true);
        foreach (var (b, name) in new[] { (_left, "Turn left"), (_right, "Turn right"), (_earlier, "Move earlier"), (_later, "Move later"), (_undo, "Undo"), (_redo, "Redo") })
            AutomationProperties.SetName(b, name);

        foreach (var s in new[] { "Small", "Medium", "Large" }) _zoom.Items.Add(s);
        _zoom.SelectedIndex = 1;
        _zoom.ToolTip = "Page picture size";
        AutomationProperties.SetName(_zoom, "Page picture size");
        _zoom.SelectionChanged += (_, _) => Refresh(keepSelection: true);

        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 4) };
        foreach (var group in new[] { new[] { _open, _add, _blank }, [_left, _right, _earlier, _later], [_replace, _delete], [_extract, _split], [_undo, _redo] })
        {
            var g = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
            foreach (var b in group) g.Children.Add(b);
            bar.Children.Add(g);
        }
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        zoomRow.Children.Add(_zoom);
        bar.Children.Add(zoomRow);

        // ---- The pages
        _grid.SelectionMode = SelectionMode.Extended;
        _grid.BorderThickness = new Thickness(0);
        _grid.Padding = new Thickness(6);
        _grid.SetResourceReference(BackgroundProperty, Brand.K.Surface);
        _grid.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(_grid, false);
        _grid.ItemContainerStyle = TileStyle();
        _grid.AllowDrop = true;
        _grid.FocusVisualStyle = null;
        AutomationProperties.SetName(_grid, "Pages");
        _grid.SelectionChanged += (_, _) => UpdateState();
        _grid.MouseDoubleClick += (_, e) => { if (TileAt(e.OriginalSource) is { } t) Preview(t); };
        _grid.DragOver += OnDragOver;
        _grid.DragLeave += (_, _) => Mark(null, false);
        _grid.Drop += OnDrop;
        _grid.ContextMenu = PageMenu();
        _grid.Loaded += (_, _) =>
        {
            _scroller ??= Find<ScrollViewer>(_grid);
            if (_scroller is not null) _scroller.ScrollChanged += (_, _) => ViewChanged();
        };
        _grid.SizeChanged += (_, _) => ViewChanged();

        BuildEmpty();
        var middle = new Grid();
        middle.Children.Add(_grid);
        middle.Children.Add(_empty);

        // ---- Footer: counts and the last result on the left, Save on the right.
        _show = RichUi.Button(RichUi.Icon.Folder, "Show in folder");
        _show.Visibility = Visibility.Collapsed;
        _show.Click += (_, _) => ShowOutput();
        // Save as is the main action: it asks for the folder and the name every time. Save writes over the file opened.
        _saveAs = RichUi.Button(RichUi.Icon.Save, "Save as…", Brand.Weight.Primary);
        _saveAs.ToolTip = "Saves the PDF in a folder and under a name you choose (Ctrl+Shift+S). The files you opened don't change.";
        _saveAs.Click += async (_, _) => await SaveDoc(saveAs: true);
        _save = RichUi.Button(RichUi.Icon.Save, "Save over");
        _save.ToolTip = "Saves over the file you opened (Ctrl+S). The old file is kept in Documents\\Sketch ProMax\\PDF backups.";
        _save.Click += async (_, _) => await SaveDoc(saveAs: false);
        _needsDoc.AddRange([_saveAs, _save]);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _note.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_note, AutomationLiveSetting.Polite);
        texts.Children.Add(_counts);
        texts.Children.Add(_note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_show);
        buttons.Children.Add(_save);
        buttons.Children.Add(_saveAs);
        var footer = new DockPanel { Margin = new Thickness(14, 8, 18, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(_bar, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_bar);
        footer.Children.Add(texts);

        _header = Brand.Header("PDF editor", "Pick pages, then delete, turn, move or replace them. Add PDFs to merge.");
        var root = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(_header);
        root.Children.Add(bar);
        root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        AllowDrop = true;
        DragOver += (_, e) => { if (_doc is null) { e.Effects = Pdfs(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; } };
        Drop += (_, e) => { if (_doc is null && Pdfs(e.Data) is { Count: > 0 } files) { e.Handled = true; _ = OpenFiles(files); } };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _running?.Cancel();
            _viewTimer?.Stop();
            _doc?.Dispose();
            _doc = null;
            _cache.Clear();
        };
        _viewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(70) };
        _viewTimer.Tick += (_, _) => { _viewTimer.Stop(); LoadVisible(); };
        Loaded += (_, _) => _open.Focus();
        UpdateState();
    }

    // ---- Glyphs that RichUi.Icon doesn't have (Segoe MDL2 Assets / Fluent Icons)
    private const string Turn = "\uE7AD", Redo = "\uE7A6", Cut = "\uE8C6", Swap = "\uE8AB", Page = "\uE8A5";

    private Button Tool(string glyph, string text, string tip, Action click, bool needsDoc, bool needsSelection = false)
    {
        var b = RichUi.Button(glyph, text);
        b.MinWidth = text.Length == 0 ? 36 : 0;
        b.Margin = new Thickness(0, 0, 4, 0);
        b.ToolTip = tip;
        ToolTipService.SetShowOnDisabled(b, true);
        b.Click += (_, _) => { if (!_busy) click(); };
        if (needsDoc) _needsDoc.Add(b);
        if (needsSelection) _needsSelection.Add(b);
        return b;
    }

    /// <summary>The turn arrow mirrored, for Turn left.</summary>
    private static void Mirror(Button button)
    {
        if (button.Content is StackPanel { Children.Count: > 0 } row && row.Children[0] is TextBlock icon)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform(-1, 1);
        }
    }

    private void BuildEmpty()
    {
        _empty.SetResourceReference(BackgroundProperty, Brand.K.Surface);
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460 };
        var icon = new TextBlock { Text = RichUi.Icon.Pdf, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center };
        Brand.Fg(icon, Brand.K.Muted);
        box.Children.Add(icon);
        var title = Brand.Text("Open a PDF to edit its pages", Brand.K.Text, 16, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 10, 0, 4);
        box.Children.Add(title);
        var hint = Brand.Text("Or drop PDF files here. Several files are merged into one.", Brand.K.Muted, 12);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextAlignment = TextAlignment.Center;
        box.Children.Add(hint);
        var open = RichUi.Button(RichUi.Icon.Open, "Open PDF", Brand.Weight.Primary);
        open.Click += (_, _) => _ = OpenPicked();
        var merge = RichUi.Button(RichUi.Icon.Add, "Merge PDFs…");
        merge.ToolTip = "Pick two or more PDFs: they become one document, in the order of their names. Change the order after.";
        merge.Click += (_, _) => _ = OpenPicked(merge: true);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(merge);
        row.Children.Add(open);
        box.Children.Add(row);
        _empty.Children.Add(box);
    }

    // ---- Tiles ---------------------------------------------------------------------------------------------------

    /// <summary>One page in the grid: its paper (a white box the page's shape until its picture comes) and number.</summary>
    private sealed class Tile
    {
        public required PdfPage Page { get; init; }
        public required ListBoxItem Item { get; init; }
        public required Border Paper { get; init; }
        public required Image Image { get; init; }
        public required TextBlock Number { get; init; }
        public required TextBlock From { get; init; }
        public int Position;
        public int DrawnAt;   // the picture size drawn (pixels), 0 for none
        public bool Asked;    // a picture is being drawn
        public bool Wanted;   // in view (or near it): draw it, and keep its picture
    }

    private Tile TileFor(PdfPage page)
    {
        if (_tiles.TryGetValue(page, out var tile)) return tile;
        var image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var paper = new Border { Background = Brushes.White, BorderThickness = new Thickness(1), Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        paper.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        var number = Brand.Text("", Brand.K.Text, 12, bold: true);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        var from = Brand.Text("", Brand.K.Muted, 11);
        from.HorizontalAlignment = HorizontalAlignment.Center;
        from.TextTrimming = TextTrimming.CharacterEllipsis;
        var stack = new StackPanel();
        var box = new Grid();
        box.Children.Add(paper);
        stack.Children.Add(box);
        stack.Children.Add(number);
        stack.Children.Add(from);
        var item = new ListBoxItem { Content = stack };
        tile = new Tile { Page = page, Item = item, Paper = paper, Image = image, Number = number, From = from };
        item.Tag = tile;
        item.PreviewMouseLeftButtonDown += (_, e) => PressTile(tile, e);
        item.PreviewMouseLeftButtonUp += (_, e) => ReleaseTile(tile, e);
        item.PreviewMouseMove += (_, e) => MoveOverTile(tile, e);
        _tiles[page] = tile;
        return tile;
    }

    private void SizeTile(Tile tile, int box)
    {
        var grid = (Grid)((StackPanel)tile.Item.Content).Children[0];
        grid.Width = box;
        grid.Height = box * 1.3;
        var aspect = tile.Page.Aspect;
        double w = box, h = box / Math.Max(0.05, aspect);
        if (h > box * 1.3) { h = box * 1.3; w = h * aspect; }
        tile.Paper.Width = Math.Max(8, w);
        tile.Paper.Height = Math.Max(8, h);
        ((StackPanel)tile.Item.Content).Width = box;
        tile.From.MaxWidth = box;
    }

    /// <summary>Shows the session's pages: their tiles in order, numbered, the picked ones kept picked.</summary>
    private void Refresh(bool keepSelection = false, IEnumerable<int>? select = null)
    {
        var picked = keepSelection ? _grid.SelectedItems.Cast<ListBoxItem>().Select(i => ((Tile)i.Tag).Page).ToList() : null;
        _generation++;
        var pages = _doc?.Pages ?? [];
        var box = Sizes[Math.Max(0, _zoom.SelectedIndex)];
        var mainSource = pages.Count > 0 ? pages.GroupBy(p => p.Source).OrderByDescending(g => g.Count()).First().Key : 0;
        var keep = new HashSet<PdfPage>(pages, new SameObject());
        foreach (var gone in _tiles.Keys.Where(p => !keep.Contains(p)).ToList()) _tiles.Remove(gone);
        _order.Clear();
        _grid.Items.Clear();
        for (var i = 0; i < pages.Count; i++)
        {
            var tile = TileFor(pages[i]);
            tile.Position = i;
            tile.Number.Text = (i + 1).ToString();
            var p = tile.Page;
            var source = p.Source < 0 ? "Blank page" : _doc!.SourceName(p.Source);
            tile.From.Text = p.Source < 0 ? "Blank" : p.Source == mainSource ? "" : $"{source}, p. {p.Index + 1}";
            tile.Item.ToolTip = p.Source < 0 ? $"Page {i + 1}: a blank page" : $"Page {i + 1}: page {p.Index + 1} of {source}" + (p.Turns == 0 ? "" : $", turned {p.Turns * 90}°");
            AutomationProperties.SetName(tile.Item, $"Page {i + 1}" + (tile.From.Text.Length > 0 ? ", " + tile.From.Text : ""));
            if (tile.DrawnAt > 0 && Math.Abs(tile.DrawnAt - Px(box)) > 2) { tile.Image.Source = null; tile.DrawnAt = 0; }
            SizeTile(tile, box);
            _order.Add(tile);
            _grid.Items.Add(tile.Item);
        }
        _empty.Visibility = _doc is null ? Visibility.Visible : Visibility.Collapsed;
        _grid.Visibility = _doc is null ? Visibility.Collapsed : Visibility.Visible;
        if (picked is not null) _grid.Pick(picked.Where(p => _tiles.ContainsKey(p)).Select(p => _tiles[p].Item));
        else if (select is not null) _grid.Pick(select.Where(n => n >= 0 && n < _order.Count).Select(n => _order[n].Item));
        if (_grid.SelectedItems.Count > 0 && _grid.SelectedItems[0] is ListBoxItem first)
        {
            _grid.UpdateLayout();
            first.BringIntoView();
            first.Focus();
        }
        UpdateState();
        ViewChanged();
    }

    private int Px(int box) => (int)Math.Round(box * 1.3 * Dpi);

    private double Dpi
    {
        get
        {
            try { return VisualTreeHelper.GetDpi(this).DpiScaleY; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return 1; }
        }
    }

    // ---- Pictures of the pages in view -------------------------------------------------------------------------------

    private void ViewChanged()
    {
        _viewTimer.Stop();
        _viewTimer.Start();
    }

    /// <summary>
    /// Asks for pictures of the pages in view and a screen above and below, top first; drops the pictures of pages
    /// far out of view so a long document doesn't fill the memory.
    /// </summary>
    private void LoadVisible()
    {
        if (_doc is null || _scroller is null || _order.Count == 0) return;
        var view = _scroller.ViewportHeight;
        if (view <= 0) return;
        var near = new List<Tile>();
        foreach (var tile in _order)
        {
            if (!tile.Item.IsVisible) continue;
            double top;
            try { top = tile.Item.TransformToAncestor(_scroller).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { continue; }
            var bottom = top + tile.Item.ActualHeight;
            var wanted = bottom > -view && top < view * 2;
            tile.Wanted = wanted;
            if (wanted) near.Add(tile);
            else if (bottom < -view * 3 || top > view * 4)
            {
                tile.Image.Source = null;
                tile.DrawnAt = 0;
            }
        }
        var px = Px(Sizes[Math.Max(0, _zoom.SelectedIndex)]);
        foreach (var tile in near.OrderBy(t => t.Position))
        {
            if (tile.DrawnAt == px || tile.Asked) continue;
            if (_cache.Get(CacheKey(tile.Page, px)) is { } kept)
            {
                tile.Image.Source = kept;
                tile.DrawnAt = px;
                continue;
            }
            _ = Draw(tile, px, _generation);
        }
    }

    private static (int, int, int, int) CacheKey(PdfPage p, int px) => (p.Source, p.Index, p.Turns, px);

    private async Task Draw(Tile tile, int px, int generation)
    {
        var doc = _doc;
        if (doc is null) return;
        tile.Asked = true;
        try
        {
            var width = (int)Math.Round(px / 1.3);
            var picture = await doc.DrawLater(tile.Page, width, px, () => tile.Wanted && generation == _generation && ReferenceEquals(doc, _doc));
            if (picture is null || !ReferenceEquals(doc, _doc)) return;
            var bitmap = BitmapSource.Create(picture.Width, picture.Height, 96, 96, PixelFormats.Bgr32, null, picture.Pixels, picture.Stride);
            bitmap.Freeze();
            _cache.Add(CacheKey(tile.Page, px), bitmap, picture.Pixels.Length);
            if (!tile.Wanted && generation != _generation) return;
            tile.Image.Source = bitmap;
            tile.DrawnAt = px;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or ArgumentException or IOException)
        {
            tile.Item.ToolTip = $"This page couldn't be drawn: {ex.Message}";
        }
        finally { tile.Asked = false; }
    }

    // ---- Opening -----------------------------------------------------------------------------------------------------

    private async Task OpenPicked(bool merge = false)
    {
        if (!AskToSave()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = merge ? "PDFs to merge (pick two or more)" : "PDF to edit (pick several to merge them)",
            Filter = "PDF files|*.pdf",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await OpenFiles(dialog.FileNames.OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase).ToList(), confirmed: true);
    }

    /// <summary>Opens one PDF, or merges several (in the order given) into a new document.</summary>
    private async Task OpenFiles(IReadOnlyList<string> files, bool confirmed = false)
    {
        if (files.Count == 0 || _busy) return;
        if (!confirmed && !AskToSave()) return;
        var next = new PdfEditSession();
        var opened = false;
        await Busy($"Opening {Path.GetFileName(files[0])}…", async ct =>
        {
            // One file is the document (Save writes back to it); several start a merge in the order given.
            var first = await Load(next, files[0], asDocument: files.Count == 1);
            if (first is null) return;
            if (files.Count > 1)
            {
                next.StartMerge(first);
                foreach (var file in files.Skip(1))
                {
                    ct.ThrowIfCancellationRequested();
                    Info($"Adding {Path.GetFileName(file)}…");
                    if (await Load(next, file) is { } pages) next.Insert(next.Pages.Count, pages);
                }
            }
            opened = true;
        });
        if (!opened) { next.Dispose(); return; }
        _doc?.Dispose();
        _doc = next;
        _cache.Clear();
        _tiles.Clear();
        _lastOutput = null;
        _show.Visibility = Visibility.Collapsed;
        Refresh();
        var n = _doc.Pages.Count;
        Info(files.Count == 1 ? $"{n} page{S(n)}. Click a page to pick it; Ctrl or Shift to pick more; double-click to see it large."
            : $"{files.Count} PDFs merged: {n} page{S(n)}. Change the order if needed, then Save as.");
        _grid.Focus();
    }

    /// <summary>
    /// Reads a PDF's pages into <paramref name="session"/> (as the document when <paramref name="asDocument"/>), asking
    /// for its password when it has one. Null when it couldn't (and says why).
    /// </summary>
    private async Task<List<PdfPage>?> Load(PdfEditSession session, string file, bool asDocument = false)
    {
        string? password = null;
        while (true)
        {
            try
            {
                var pages = await Task.Run(() =>
                {
                    if (!asDocument) return session.Load(file, password);
                    session.Open(file, password);
                    return session.Pages.ToList();
                });
                if (pages.Count == 0) { Error($"{Path.GetFileName(file)} has no pages."); return null; }
                return pages;
            }
            catch (PdfEngine.OpenException ex) when (ex.Password)
            {
                password = PasswordWindow.Ask(this, Path.GetFileName(file), ex.Message);
                if (password is null) { Info($"{Path.GetFileName(file)} wasn't opened."); return null; }
            }
            catch (PdfEngine.OpenException ex) { Error(ex.Message); return null; }
            catch (InvalidOperationException ex) { Error(ex.Message); return null; }
        }
    }

    private async Task<List<PdfPage>> LoadMany(IEnumerable<string> files)
    {
        var all = new List<PdfPage>();
        foreach (var file in files)
        {
            Info($"Reading {Path.GetFileName(file)}…");
            if (await Load(_doc!, file) is { } pages) all.AddRange(pages);
        }
        return all;
    }

    // ---- Edits -------------------------------------------------------------------------------------------------------

    private List<int> Picked() => _grid.SelectedItems.Cast<ListBoxItem>().Select(i => ((Tile)i.Tag).Position).OrderBy(p => p).ToList();

    /// <summary>Where added pages go: after the last picked page, or at the end.</summary>
    private int InsertAt() => Picked() is { Count: > 0 } p ? p[p.Count - 1] + 1 : _doc?.Pages.Count ?? 0;

    private async Task AddPdf(int? at = null)
    {
        if (_doc is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "PDFs to add", Filter = "PDF files|*.pdf", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await AddFiles(dialog.FileNames.OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase).ToList(), at ?? InsertAt());
    }

    private async Task AddFiles(IReadOnlyList<string> files, int at)
    {
        List<PdfPage> pages = [];
        await Busy("Adding…", async _ => pages = await LoadMany(files));
        if (pages.Count == 0 || _doc is null) return;
        _doc.Insert(at, pages);
        Refresh(select: Enumerable.Range(at, pages.Count));
        Info($"Added {pages.Count} page{S(pages.Count)} from {Names(files)} as page{S(pages.Count)} {PdfEditSession.Describe(Enumerable.Range(at, pages.Count).ToList())}.");
    }

    private void AddBlank()
    {
        if (_doc is null) return;
        var at = InsertAt();
        var like = _doc.Pages.Count == 0 ? null : _doc.Pages[Math.Max(0, Math.Min(_doc.Pages.Count - 1, at - 1))];
        _doc.Insert(at, [PdfEditSession.Blank(like)]);
        Refresh(select: [at]);
        Info($"Blank page added as page {at + 1}.");
    }

    private void Rotate(int turns)
    {
        if (_doc is null || Picked() is not { Count: > 0 } picked) return;
        _doc.Rotate(picked, turns);
        Refresh(select: picked);
        Info($"Turned {Pages(picked)} {(turns > 0 ? "right" : "left")}.");
    }

    private void Nudge(int step)
    {
        if (_doc is null || Picked() is not { Count: > 0 } picked) return;
        var now = _doc.Nudge(picked, step);
        Refresh(select: now);
        Info($"Moved to {(now.Count == 1 ? "page" : "pages")} {PdfEditSession.Describe(now)}.");
    }

    private void DeletePicked()
    {
        if (_doc is null || Picked() is not { Count: > 0 } picked) return;
        if (picked.Count == _doc.Pages.Count) { Error("A PDF needs at least one page: keep one, or use Extract to save only some pages."); return; }
        var said = Pages(picked);
        _doc.Delete(picked);
        Refresh(select: [Math.Min(picked[0], _doc.Pages.Count - 1)]);
        Info($"Deleted {said}. Ctrl+Z brings {(picked.Count == 1 ? "it" : "them")} back.");
    }

    private async Task ReplacePicked()
    {
        if (_doc is null || Picked() is not { Count: > 0 } picked) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"PDF with the new {(picked.Count == 1 ? "page" : "pages")} for {Pages(picked)}", Filter = "PDF files|*.pdf", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        List<PdfPage> pages = [];
        await Busy("Reading…", async _ => pages = await LoadMany([dialog.FileName]));
        if (pages.Count == 0 || _doc is null) return;
        var use = pages;
        if (pages.Count > 1 && pages.Count != picked.Count)
        {
            // Which pages of the new file: the first as many as are picked, unless the person says otherwise.
            var guess = picked.Count == 1 ? "1" : $"1-{Math.Min(picked.Count, pages.Count)}";
            var typed = PagesWindow.Ask(this, Path.GetFileName(dialog.FileName), pages.Count, guess, picked.Count);
            if (typed is null) { Info("Nothing replaced."); return; }
            use = typed.Select(i => pages[i]).ToList();
        }
        var said = Pages(picked);
        var at = _doc.Replace(picked, use);
        Refresh(select: Enumerable.Range(at, use.Count));
        Info($"Replaced {said} with {use.Count} page{S(use.Count)} of {Path.GetFileName(dialog.FileName)}.");
    }

    private void Undo()
    {
        if (_doc?.Undo() == true) { Refresh(); Info("Undone."); }
    }

    private void RedoEdit()
    {
        if (_doc?.Redo() == true) { Refresh(); Info("Done again."); }
    }

    // ---- Writing -----------------------------------------------------------------------------------------------------

    private async Task<bool> SaveDoc(bool saveAs)
    {
        if (_doc is null) return false;
        var target = saveAs || _doc.File is null ? AskTarget(_doc.File is null ? Suggest(" merged") : _doc.File, "Save the PDF as") : _doc.File;
        if (target is null) return false;
        var overwrite = string.Equals(Path.GetFullPath(target), _doc.File, StringComparison.OrdinalIgnoreCase) || File.Exists(target);
        string? backup = null;
        var ok = false;
        await Busy($"Saving {Path.GetFileName(target)}…", async ct =>
        {
            var doc = _doc;
            backup = await Task.Run(() => doc.Save(target, overwrite ? Backups : null, Progress(), ct), ct);
            ok = true;
        });
        if (!ok) return false;
        _cache.Clear();
        _tiles.Clear();
        Refresh();
        _lastOutput = target;
        _show.Visibility = Visibility.Visible;
        Success($"Saved {Path.GetFileName(target)} ({_doc.Pages.Count} page{S(_doc.Pages.Count)})." + (backup is null ? "" : " The old file is in Documents\\Sketch ProMax\\PDF backups."));
        return true;
    }

    private async Task Extract()
    {
        if (_doc is null || Picked() is not { Count: > 0 } picked) return;
        var target = AskTarget(Suggest($" page{S(picked.Count)} {PdfEditSession.Describe(picked).Replace(",", "").Replace(" and more", "+")}"), "Save the picked pages as");
        if (target is null) return;
        var pages = picked.Select(i => _doc.Pages[i]).ToList();
        var ok = false;
        await Busy($"Saving {Path.GetFileName(target)}…", async ct =>
        {
            var doc = _doc;
            await Task.Run(() => doc.WriteCopy(pages, target, Progress(), ct), ct);
            ok = true;
        });
        if (!ok) return;
        _lastOutput = target;
        _show.Visibility = Visibility.Visible;
        Success($"Saved {Pages(picked)} as {Path.GetFileName(target)}. This document is unchanged.");
    }

    private async Task SplitDoc()
    {
        if (_doc is null) return;
        var picked = Picked();
        var file = _doc.File ?? Suggest(" merged");
        var name = Path.GetFileNameWithoutExtension(file);
        // "name parts" next to the file (made only when the split runs; Change… picks another folder).
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)) ?? "", name + " parts");
        var choice = SplitChoiceWindow.Ask(this, _doc.Pages.Count, picked, folder);
        if (choice is null) return;
        List<PdfEditSession.Part> parts = [];
        await Busy("Splitting…", async ct =>
        {
            var doc = _doc;
            parts = await Task.Run(() => doc.Split(choice.Folder, name, choice.By, choice.Every, picked, (long)(choice.Mb * 1024 * 1024), Progress(), ct), ct);
        });
        if (parts.Count == 0) return;
        _lastOutput = parts[0].File;
        _show.Visibility = Visibility.Visible;
        Success($"Split into {parts.Count} PDF{S(parts.Count)} in {choice.Folder}. This document is unchanged.");
    }

    /// <summary>A file name next to the document (or its first file, for a merge) with <paramref name="suffix"/> added.</summary>
    private string Suggest(string suffix)
    {
        var from = _doc?.File ?? _doc?.Pages.Where(p => p.Source >= 0).Select(p => _doc.SourcePath(p.Source)).FirstOrDefault()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Document.pdf");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(from)) ?? "", Path.GetFileNameWithoutExtension(from) + suffix + ".pdf");
    }

    private string? AskTarget(string suggested, string title)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = "PDF files|*.pdf",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested),
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = ".pdf",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private Action<int, int> Progress() => (done, total) => Dispatcher.BeginInvoke(new Action(() =>
    {
        _bar.IsIndeterminate = false;
        _bar.Value = total == 0 ? 0 : done / (double)total;
    }));

    private void ShowOutput()
    {
        if (_lastOutput is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { Error($"Couldn't open the folder: {ex.Message}"); }
    }

    /// <summary>Runs <paramref name="work"/> with the toolbar off and the bar showing; says what went wrong, if anything.</summary>
    private async Task Busy(string what, Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        _running = new CancellationTokenSource();
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = true;
        Info(what);
        UpdateState();
        Cursor = Cursors.AppStarting;
        try { await work(_running.Token); }
        catch (OperationCanceledException) { Info("Stopped."); }
        catch (PdfEngine.OpenException ex) { Error(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Error(ex.Message);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _busy = false;
            _bar.Visibility = Visibility.Collapsed;
            Cursor = null;
            UpdateState();
        }
    }

    // ---- State, keys, closing --------------------------------------------------------------------------------------

    private void UpdateState()
    {
        var has = _doc is not null;
        var picked = has ? _grid.SelectedItems.Count : 0;
        foreach (var b in _needsDoc) b.IsEnabled = has && !_busy;
        foreach (var b in _needsSelection) b.IsEnabled = has && !_busy && picked > 0;
        _open.IsEnabled = !_busy;
        _undo.IsEnabled = has && !_busy && _doc!.CanUndo;
        _redo.IsEnabled = has && !_busy && _doc!.CanRedo;
        _zoom.IsEnabled = has;
        var name = _doc?.File is { } f ? Path.GetFileName(f) : has ? "New merged PDF" : null;
        Title = "Sketch ProMax: PDF editor" + (name is null ? "" : $" – {name}{(_doc!.Changed ? " *" : "")}");
        if (!has) { _counts.Text = ""; return; }
        var n = _doc!.Pages.Count;
        _counts.Text = $"{name} · {n} page{S(n)}" + (picked > 0 ? $" · {picked} picked" : "") + (_doc.Changed ? " · not saved yet" : "");
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_busy)
        {
            if (e.Key == Key.Escape) { _running?.Cancel(); e.Handled = true; }
            return;
        }
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (Keyboard.FocusedElement is TextBox) return;
        Action? run = (e.Key, ctrl, shift) switch
        {
            (Key.O, true, _) => () => _ = OpenPicked(),
            (Key.S, true, false) => () => _ = SaveDoc(saveAs: false),
            (Key.S, true, true) => () => _ = SaveDoc(saveAs: true),
            (Key.Z, true, false) => Undo,
            (Key.Y, true, _) or (Key.Z, true, true) => RedoEdit,
            (Key.Delete, false, _) => DeletePicked,
            (Key.R, true, _) => () => Rotate(1),
            (Key.L, true, _) => () => Rotate(-1),
            (Key.E, true, _) => () => _ = Extract(),
            (Key.Left, true, _) => () => Nudge(-1),
            (Key.Right, true, _) => () => Nudge(1),
            (Key.Enter, false, false) when _grid.IsKeyboardFocusWithin && _grid.SelectedItem is ListBoxItem { Tag: Tile t } => () => Preview(t),
            _ => null,
        };
        if (run is null || _doc is null && e.Key != Key.O) return;
        e.Handled = true;
        run();
    }

    /// <summary>True when it is fine to leave the document: saved, nothing to save, or the person said not to save.</summary>
    private bool AskToSave()
    {
        if (_doc is not { Changed: true }) return true;
        var answer = MessageBox.Show(this, $"Save the changes to {(_doc.File is { } f ? Path.GetFileName(f) : "the merged PDF")} first? You pick the folder and the name.", "Sketch ProMax: PDF editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        // Saving runs off this thread; wait for it here with the window still responding.
        var frame = new DispatcherFrame();
        var saved = false;
        _ = SaveDoc(saveAs: true).ContinueWith(t => { saved = t.Status == TaskStatus.RanToCompletion && t.Result; frame.Continue = false; }, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return saved;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) { _running?.Cancel(); e.Cancel = true; Info("Stopping… close again in a moment."); return; }
        if (!AskToSave()) e.Cancel = true;
    }

    // ---- Picking and dragging pages ----------------------------------------------------------------------------------

    private Point _pressedAt;
    private Tile? _pressed;
    private bool _deferredPick;

    private void PressTile(Tile tile, MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(_grid);
        _pressed = tile;
        // Pressing a page that is already picked (one of several) keeps them all picked, so they can be dragged together.
        _deferredPick = tile.Item.IsSelected && _grid.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None;
        if (_deferredPick) { e.Handled = true; tile.Item.Focus(); }
    }

    private void ReleaseTile(Tile tile, MouseButtonEventArgs e)
    {
        if (_deferredPick && ReferenceEquals(_pressed, tile)) _grid.Pick([tile.Item]);
        _deferredPick = false;
        _pressed = null;
    }

    private void MoveOverTile(Tile tile, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed || _busy) return;
        var now = e.GetPosition(_grid);
        if (Math.Abs(now.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!_pressed.Item.IsSelected) _grid.Pick([_pressed.Item]);
        var positions = Picked();
        _pressed = null;
        _deferredPick = false;
        DragDrop.DoDragDrop(_grid, new DataObject(PagesFormat, positions.ToArray()), DragDropEffects.Move);
        Mark(null, false);
    }

    private const string PagesFormat = "SketchProMax.PdfPages";
    private Tile? _marked;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var pages = e.Data.GetDataPresent(PagesFormat);
        var files = !pages && Pdfs(e.Data).Count > 0;
        if (_doc is null || !pages && !files) { e.Effects = DragDropEffects.None; return; }
        e.Effects = pages ? DragDropEffects.Move : DragDropEffects.Copy;
        var (tile, after) = DropPlace(e);
        Mark(tile, after);
        // Scroll while dragging near the top or bottom edge.
        if (_scroller is not null)
        {
            var y = e.GetPosition(_scroller).Y;
            if (y < 40) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 20);
            else if (y > _scroller.ViewportHeight - 40) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 20);
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var (tile, after) = DropPlace(e);
        Mark(null, false);
        if (_doc is null) return;
        var at = tile is null ? _doc.Pages.Count : tile.Position + (after ? 1 : 0);
        if (e.Data.GetData(PagesFormat) is int[] moving)
        {
            var first = _doc.MoveTo(moving, at);
            Refresh(select: Enumerable.Range(first, moving.Length));
            Info($"Moved to {(moving.Length == 1 ? "page" : "pages")} {PdfEditSession.Describe(Enumerable.Range(first, moving.Length).ToList())}.");
        }
        else if (Pdfs(e.Data) is { Count: > 0 } files) _ = AddFiles(files, at);
    }

    /// <summary>The page under the mouse and whether the drop goes after it (right half) rather than before.</summary>
    private (Tile? Tile, bool After) DropPlace(DragEventArgs e)
    {
        var hit = _grid.InputHitTest(e.GetPosition(_grid)) as DependencyObject;
        if (TileAt(hit) is not { } tile) return (null, false);
        var x = e.GetPosition(tile.Item).X;
        return (tile, x > tile.Item.ActualWidth / 2);
    }

    /// <summary>Shows where dropped pages go: a thick accent edge on the left or right of a page.</summary>
    private void Mark(Tile? tile, bool after)
    {
        if (_marked is not null && !ReferenceEquals(_marked, tile)) _marked.Item.BorderThickness = new Thickness(0);
        _marked = tile;
        if (tile is null) return;
        tile.Item.SetResourceReference(Control.BorderBrushProperty, Brand.K.Accent);
        tile.Item.BorderThickness = after ? new Thickness(0, 0, 4, 0) : new Thickness(4, 0, 0, 0);
    }

    private static Tile? TileAt(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem { Tag: Tile t }) return t;
        return null;
    }

    private static List<string> Pdfs(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(f)).ToList() : [];

    private ContextMenu PageMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, string keys, Action run, bool needsPick = true)
        {
            var m = new MenuItem { Header = text, InputGestureText = keys };
            m.Click += (_, _) => { if (!_busy) run(); };
            m.Tag = needsPick;
            menu.Items.Add(m);
            return m;
        }
        Item("See it large", "Enter", () => { if (_grid.SelectedItem is ListBoxItem { Tag: Tile t }) Preview(t); });
        menu.Items.Add(new Separator());
        Item("Turn right", "Ctrl+R", () => Rotate(1));
        Item("Turn left", "Ctrl+L", () => Rotate(-1));
        Item("Move earlier", "Ctrl+Left", () => Nudge(-1));
        Item("Move later", "Ctrl+Right", () => Nudge(1));
        menu.Items.Add(new Separator());
        Item("Add a PDF after this", "", () => _ = AddPdf(), needsPick: false);
        Item("Add a blank page after this", "", AddBlank, needsPick: false);
        Item("Replace with pages of another PDF…", "", () => _ = ReplacePicked());
        Item("Save these pages as a new PDF…", "Ctrl+E", () => _ = Extract());
        menu.Items.Add(new Separator());
        Item("Delete", "Delete", DeletePicked);
        menu.Opened += (_, _) =>
        {
            var picked = _grid.SelectedItems.Count > 0;
            foreach (var m in menu.Items.OfType<MenuItem>()) m.IsEnabled = _doc is not null && !_busy && (!(bool)m.Tag || picked);
        };
        return menu;
    }

    private void Preview(Tile tile)
    {
        if (_doc is null) return;
        new PdfPreviewWindow(this, _doc, tile.Position).Show();
    }

    // ---- Small helpers -----------------------------------------------------------------------------------------------

    private static string S(int n) => n == 1 ? "" : "s";
    private static string Pages(IReadOnlyList<int> picked) => (picked.Count == 1 ? "page " : "pages ") + PdfEditSession.Describe(picked);
    private static string Names(IReadOnlyList<string> files) => files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} PDFs";

    private void Info(string text) { _note.Text = text; Brand.Fg(_note, Brand.K.Text); }
    private void Success(string text) { _note.Text = text; Brand.Fg(_note, Brand.K.SuccessText); }
    private void Error(string text) { _note.Text = text; Brand.Fg(_note, Brand.K.DangerText); }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>The page tile: a rounded box that is tinted and edged in the accent colour when picked, a soft hover otherwise.</summary>
    private static Style TileStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
              <Setter Property="Margin" Value="6"/>
              <Setter Property="Padding" Value="8,8,8,6"/>
              <Setter Property="BorderThickness" Value="0"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ListBoxItem">
                    <Grid>
                      <Border x:Name="card" CornerRadius="6" Background="Transparent" BorderBrush="Transparent" BorderThickness="2" Padding="{TemplateBinding Padding}">
                        <ContentPresenter/>
                      </Border>
                      <Border x:Name="edge" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Margin="-5,0,-5,0"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="card" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
                      </Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="card" Property="Background" Value="{DynamicResource SpmAccentSoft}"/>
                        <Setter TargetName="card" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/>
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="card" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        return (Style)XamlReader.Parse(xaml);
    }

    /// <summary>A ListBox that can pick several items in one go (SetSelectedItems is protected).</summary>
    private sealed class PageList : ListBox
    {
        public void Pick(IEnumerable<ListBoxItem> items)
        {
            var list = items.ToList();
            SetSelectedItems(list);
            if (list.Count > 0) SetCurrentValue(SelectedItemProperty, list[0]);
        }
    }

    /// <summary>Compares pages by reference (the same page added twice is two pages).</summary>
    private sealed class SameObject : IEqualityComparer<PdfPage>
    {
        public bool Equals(PdfPage? x, PdfPage? y) => ReferenceEquals(x, y);
        public int GetHashCode(PdfPage obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>Page pictures by (file, page, turns, size), the least recently used dropped past a memory limit.</summary>
    private sealed class ThumbCache(long limit)
    {
        private readonly Dictionary<(int, int, int, int), LinkedListNode<( (int, int, int, int) Key, BitmapSource Picture, long Bytes)>> _map = [];
        private readonly LinkedList<((int, int, int, int) Key, BitmapSource Picture, long Bytes)> _used = new();
        private long _bytes;

        public BitmapSource? Get((int, int, int, int) key)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _used.Remove(node);
            _used.AddFirst(node);
            return node.Value.Picture;
        }

        public void Add((int, int, int, int) key, BitmapSource picture, long bytes)
        {
            if (_map.TryGetValue(key, out var old)) { _used.Remove(old); _bytes -= old.Value.Bytes; }
            _map[key] = _used.AddFirst((key, picture, bytes));
            _bytes += bytes;
            while (_bytes > limit && _used.Last is { } last)
            {
                _used.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }
        }

        public void Clear()
        {
            _map.Clear();
            _used.Clear();
            _bytes = 0;
        }
    }
}
