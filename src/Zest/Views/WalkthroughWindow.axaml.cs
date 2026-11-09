using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Zest.Services.Walkthroughs;
using Zest.ViewModels;

namespace Zest.Views;

/// <summary>
/// The walkthrough reader: a window of its own (it can sit on another monitor while the game runs), one per game.
/// Draws the page's blocks as controls, opens links in the guide here and others in the browser, and keeps the
/// scroll position so the next open returns to the same spot.
/// </summary>
public partial class WalkthroughWindow : Window
{
    private static readonly Dictionary<string, WalkthroughWindow> Open = [];
    private readonly Dictionary<string, Control> _anchors = [];
    private CancellationTokenSource _images = new();
    private readonly DispatcherTimer _scrollSave;

    public WalkthroughWindow()
    {
        InitializeComponent();
        _scrollSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _scrollSave.Tick += (_, _) =>
        {
            _scrollSave.Stop();
            Vm?.RememberScroll(PageScroll.Offset.Y);
        };
        PageScroll.ScrollChanged += (_, _) => _scrollSave.Start();
        NewItemBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Vm?.AddItemCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private WalkthroughViewModel? Vm => DataContext as WalkthroughViewModel;

    /// <summary>Shows the game's reader (brought to the front if it's open already), on the guide or the notes tab.</summary>
    public static void Show(WalkthroughViewModel vm, bool notes)
    {
        if (!Open.TryGetValue(vm.GameTitle, out var window))
        {
            window = new WalkthroughWindow { DataContext = vm };
            Open[vm.GameTitle] = window;
            window.Closed += (_, _) => Open.Remove(vm.GameTitle);
            window.Show();
            _ = vm.OpenReaderAsync();
        }
        window.Tabs.SelectedIndex = notes ? 1 : 0;
        window.Activate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmChanged;
            Render(vm.Blocks);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Vm?.RememberScroll(PageScroll.Offset.Y);
        if (Vm is { } vm)
            vm.PropertyChanged -= OnVmChanged;
        _images.Cancel();
        base.OnClosing(e);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalkthroughViewModel.Blocks) && Vm is { } vm)
        {
            Render(vm.Blocks);
            var anchor = vm.PendingAnchor;
            var scroll = vm.PendingScroll;
            // After layout, so the offsets exist
            Dispatcher.UIThread.Post(() =>
            {
                if (anchor is not null)
                    ScrollTo(anchor);
                else
                    PageScroll.Offset = new Vector(0, scroll);
            }, DispatcherPriority.Background);
        }
    }

    private void ScrollTo(string anchor)
    {
        if (_anchors.TryGetValue(anchor.Replace(' ', '_'), out var target) && target.TranslatePoint(default, PageContent) is { } p)
            PageScroll.Offset = new Vector(0, Math.Max(0, p.Y - 8));
    }

    // ---- drawing the blocks

    private void Render(IReadOnlyList<Block> blocks)
    {
        _images.Cancel();
        _images = new CancellationTokenSource();
        _anchors.Clear();
        PageContent.Children.Clear();
        foreach (var block in blocks)
            if (Draw(block) is { } control)
                PageContent.Children.Add(control);
    }

    private Control? Draw(Block block)
    {
        switch (block)
        {
            case HeadingBlock h:
                var heading = new TextBlock
                {
                    Text = h.Text,
                    FontWeight = FontWeight.Bold,
                    FontSize = h.Level switch { <= 2 => 22, 3 => 18, _ => 16 },
                    Margin = new Thickness(0, h.Level <= 2 ? 18 : 10, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                if (h.Id is { } id)
                    _anchors[id] = heading;
                return heading;
            case ParagraphBlock p:
                var para = Text(p.Spans);
                para.Margin = new Thickness(20 * p.Indent, 0, 0, 0);
                return para;
            case ListItemBlock l:
                var item = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(6 + 22 * l.Depth, -4, 0, 0) };
                var marker = new TextBlock { Text = l.Marker, FontSize = 15, Margin = new Thickness(0, 0, 8, 0), MinWidth = 14 };
                var body = Text(l.Spans);
                Grid.SetColumn(body, 1);
                item.Children.Add(marker);
                item.Children.Add(body);
                return item;
            case TableBlock t:
                return Table(t);
            case ImageBlock i:
                return Image(i);
            case GalleryBlock g:
                var wrap = new WrapPanel();
                foreach (var i in g.Images)
                {
                    var img = Image(i);
                    img.Margin = new Thickness(0, 0, 10, 10);
                    wrap.Children.Add(img);
                }
                return wrap;
        }
        return null;
    }

    /// <summary>A paragraph as one TextBlock: links are underlined runs, found under the pointer by text position
    /// (embedded controls would sit off the text's baseline).</summary>
    private TextBlock Text(IReadOnlyList<TextSpan> spans)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 23 };
        var inlines = new InlineCollection();
        var links = new List<(int start, int end, string href)>();
        var at = 0;
        var primary = this.FindResource("PrimaryBrush") as IBrush;
        foreach (var s in spans)
        {
            var run = new Run(s.Text)
            {
                FontWeight = s.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = s.Italic ? FontStyle.Italic : FontStyle.Normal,
            };
            if (s.Href is { } href)
            {
                run.Foreground = primary;
                run.TextDecorations = TextDecorations.Underline;
                links.Add((at, at + s.Text.Length, href));
            }
            inlines.Add(run);
            at += s.Text.Length;
        }
        text.Inlines = inlines;
        if (links.Count == 0)
            return text;

        string? LinkAt(Point p)
        {
            var hit = text.TextLayout.HitTestPoint(p);
            if (!hit.IsInside)
                return null;
            var i = hit.TextPosition;
            return links.FirstOrDefault(l => i >= l.start && i < l.end).href;
        }
        var hand = new Cursor(StandardCursorType.Hand);
        text.PointerMoved += (_, e) =>
        {
            var href = LinkAt(e.GetPosition(text));
            text.Cursor = href is null ? Cursor.Default : hand;
            ToolTip.SetTip(text, href is null ? null : href.StartsWith("/wiki/") ? Uri.UnescapeDataString(href[6..]).Replace('_', ' ') : href);
        };
        text.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(text).Properties.IsLeftButtonPressed && LinkAt(e.GetPosition(text)) is { } href)
            {
                e.Handled = true;
                Vm?.FollowLink(href, ScrollTo);
            }
        };
        return text;
    }

    private Control Table(TableBlock t)
    {
        var columns = t.Rows.Max(r => r.Sum(c => c.ColSpan));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))) };
        var header = this.FindResource("ContainerHighBrush") as IBrush;
        var line = this.FindResource("OutlineVariantBrush") as IBrush;
        for (var r = 0; r < t.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var col = 0;
            foreach (var cell in t.Rows[r])
            {
                if (col >= columns)
                    break;
                var text = Text(cell.Spans);
                text.FontSize = 14;
                if (cell.Header)
                    text.FontWeight = FontWeight.Bold;
                var border = new Border
                {
                    Child = text,
                    Padding = new Thickness(8, 5),
                    BorderBrush = line,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = cell.Header ? header : null,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, col);
                Grid.SetColumnSpan(border, Math.Min(cell.ColSpan, columns - col));
                grid.Children.Add(border);
                col += cell.ColSpan;
            }
        }
        return new Border
        {
            Child = grid,
            BorderBrush = line,
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 4),
        };
    }

    private Control Image(ImageBlock i)
    {
        var width = Math.Min(i.Width > 0 ? i.Width : 300, 640);
        var height = i.Width > 0 && i.Height > 0 ? width * i.Height / (double)i.Width : double.NaN;
        var image = new Image { Width = width, Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = Math.Max(width, 200) };
        panel.Children.Add(new Border
        {
            Child = image,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = this.FindResource("ContainerHighBrush") as IBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        if (i.Caption is { } caption)
            panel.Children.Add(new TextBlock { Text = caption, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = width });
        _ = LoadAsync(image, i.Url, _images.Token);
        return panel;
    }

    private static async Task LoadAsync(Image image, string url, CancellationToken cancel)
    {
        try
        {
            var bytes = await StrategyWiki.ImageAsync(url, cancel);
            if (cancel.IsCancellationRequested)
                return;
            // SVG icons can't be decoded as bitmaps; those are left out
            if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return;
            using var stream = new MemoryStream(bytes);
            image.Source = new Bitmap(stream);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // a picture that won't load just stays empty
        }
    }
}
