using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Zest.Services.Walkthroughs;

/// <summary>A run of text in a reader block: bold/italic, and a link when <see cref="Href"/> is set.</summary>
public sealed record TextSpan(string Text, bool Bold = false, bool Italic = false, string? Href = null);

public abstract record Block;
public sealed record HeadingBlock(int Level, string Text, string? Id) : Block;
public sealed record ParagraphBlock(IReadOnlyList<TextSpan> Spans, int Indent = 0) : Block;
public sealed record ListItemBlock(IReadOnlyList<TextSpan> Spans, int Depth, string Marker) : Block;
public sealed record TableCell(IReadOnlyList<TextSpan> Spans, bool Header, int ColSpan);
public sealed record TableBlock(IReadOnlyList<IReadOnlyList<TableCell>> Rows) : Block;
public sealed record ImageBlock(string Url, int Width, int Height, string? Caption) : Block;
public sealed record GalleryBlock(IReadOnlyList<ImageBlock> Images) : Block;

/// <summary>
/// Turns a MediaWiki page's parsed HTML into the few kinds of blocks Zest's reader draws (headings, paragraphs, list
/// items, tables, images), leaving out the wiki's own furniture: navigation boxes, notices, ads, video embeds.
/// </summary>
public static partial class HtmlBlocks
{
    private static readonly string[] SkipClasses =
    [
        "nav_box", "t_tag", "noprint", "section-ad", "embedvideo", "mw-empty-elt", "top_link", "footer_nav",
        "mw-editsection", "reference", "toc", "t_subtoc", "mw-cite-backlink", "navbox", "metadata",
    ];

    /// <summary>The site of the page being parsed (parsing runs on worker threads, one page each).</summary>
    private static readonly ThreadLocal<string?> Site = new();

    private static readonly HashSet<string> SkipTags = ["STYLE", "SCRIPT", "BUTTON", "LINK", "NOSCRIPT", "SUP"];

    /// <param name="site">Where relative image and link paths point ("https://zeldawiki.wiki"); StrategyWiki by default.</param>
    public static IReadOnlyList<Block> Parse(string html, string site = "https://strategywiki.org")
    {
        Site.Value = site;
        var doc = new HtmlParser().ParseDocument("<body>" + html + "</body>");
        var w = new Walker();
        w.Blocks(doc.Body!, 0);
        w.Flush(0);
        return w.Output;
    }

    private static bool Skip(IElement e) =>
        SkipTags.Contains(e.TagName)
        || (e.TagName == "SUP" && e.ClassList.Contains("reference"))
        || e.ClassList.Any(c => SkipClasses.Any(s => c == s || c.StartsWith(s + "_", StringComparison.Ordinal) || c.StartsWith(s + "-", StringComparison.Ordinal)))
        || (e.GetAttribute("style") is { } style && style.Replace(" ", "").Contains("display:none"));

    private static string? ImageUrl(IElement img)
    {
        var src = img.GetAttribute("src");
        if (string.IsNullOrEmpty(src) || src.StartsWith("data:"))
            return null;
        return src.StartsWith("//") ? "https:" + src : src.StartsWith('/') ? (Site.Value ?? "https://strategywiki.org") + src : src;
    }

    /// <summary>A button or key icon in the text ("press the [C-Down] button"): its alt text in brackets.</summary>
    private static string? IconText(IElement e)
    {
        var img = e.TagName == "IMG" ? e : e.GetAttribute("typeof") == "mw:File" ? e.QuerySelector("img") : null;
        if (img is null || Int(img, "width") is 0 or > 32 || img.GetAttribute("alt") is not { Length: > 0 } alt)
            return null;
        return "[" + alt.Trim() + "]";
    }

    /// <summary>Another wiki's "/wiki/X" links point at that wiki (StrategyWiki's stay relative: the reader follows them).</summary>
    private static string Absolute(string href) =>
        Site.Value is { } site && site != "https://strategywiki.org" && href.StartsWith('/') && !href.StartsWith("//") ? site + href : href;

    private static int Int(IElement e, string attribute) => int.TryParse(e.GetAttribute(attribute), out var v) ? v : 0;

    private sealed class Walker
    {
        public List<Block> Output { get; } = [];
        private readonly List<TextSpan> _pending = [];

        public void Blocks(INode parent, int indent)
        {
            foreach (var node in parent.ChildNodes)
            {
                if (node is IText text)
                {
                    Inline(text, _pending, false, false, null);
                    continue;
                }
                if (node is not IElement e || Skip(e))
                    continue;
                switch (e.TagName)
                {
                    case "H1" or "H2" or "H3" or "H4" or "H5" or "H6":
                        Flush(indent);
                        var heading = Collapse(e.TextContent);
                        if (heading.Length > 0)
                            Output.Add(new HeadingBlock(e.TagName[1] - '0', heading, e.Id));
                        break;
                    case "P":
                        Flush(indent);
                        Images(e);
                        var spans = new List<TextSpan>();
                        InlineChildren(e, spans, false, false, null);
                        AddParagraph(spans, indent);
                        break;
                    case "UL" when e.ClassList.Contains("gallery"):
                        Flush(indent);
                        Gallery(e);
                        break;
                    case "UL" or "OL":
                        Flush(indent);
                        List(e, 0);
                        break;
                    case "DL":
                        Flush(indent);
                        foreach (var item in e.Children)
                        {
                            var s = new List<TextSpan>();
                            InlineChildren(item, s, item.TagName == "DT", false, null);
                            AddParagraph(s, indent + (item.TagName == "DD" ? 1 : 0));
                        }
                        break;
                    case "TABLE":
                        Flush(indent);
                        Table(e, indent);
                        break;
                    case "FIGURE":
                        Flush(indent);
                        Figure(e);
                        break;
                    case "DIV" when e.ClassList.Contains("thumb"):
                        Flush(indent);
                        Figure(e);
                        break;
                    case "SPAN" or "IMG" when IconText(e) is { } icon:
                        _pending.Add(new TextSpan(icon, Italic: true));
                        break;
                    case "SPAN" when e.GetAttribute("typeof") == "mw:File":
                    case "IMG":
                        Flush(indent);
                        Figure(e);
                        break;
                    case "BLOCKQUOTE":
                        Flush(indent);
                        Blocks(e, indent + 1);
                        Flush(indent + 1);
                        break;
                    case "BR":
                        _pending.Add(new TextSpan("\n"));
                        break;
                    case "B" or "STRONG" or "I" or "EM" or "A" or "SMALL" or "CITE" or "CODE" or "U" or "S" when !HasBlocks(e):
                        Inline(e, _pending, false, false, null);
                        break;
                    case "SPAN" when !HasBlocks(e):
                        Inline(e, _pending, false, false, null);
                        break;
                    default:
                        // A container (div, section, center, a span holding blocks): its children in order
                        Flush(indent);
                        Blocks(e, indent);
                        Flush(indent);
                        break;
                }
            }
        }

        public void Flush(int indent)
        {
            if (_pending.Count == 0)
                return;
            AddParagraph([.. _pending], indent);
            _pending.Clear();
        }

        private static bool HasBlocks(IElement e) =>
            e.QuerySelector("p, div, ul, ol, table, figure, h1, h2, h3, h4, h5, h6, blockquote, dl") is not null;

        private void AddParagraph(List<TextSpan> spans, int indent)
        {
            var tidy = Tidy(spans);
            if (tidy.Count > 0)
                Output.Add(new ParagraphBlock(tidy, indent));
        }

        private void List(IElement list, int depth)
        {
            var n = int.TryParse(list.GetAttribute("start"), out var start) ? start : 1;
            foreach (var li in list.Children.Where(c => c.TagName == "LI" && !Skip(c)))
            {
                var spans = new List<TextSpan>();
                var nested = new List<IElement>();
                foreach (var child in li.ChildNodes)
                {
                    if (child is IElement { TagName: "UL" or "OL" } sub)
                        nested.Add(sub);
                    else if (child is IElement el && (el.TagName is "FIGURE" || el.ClassList.Contains("thumb")))
                        Figure(el);
                    else
                        Inline(child, spans, false, false, null);
                }
                var tidy = Tidy(spans);
                if (tidy.Count > 0)
                    Output.Add(new ListItemBlock(tidy, depth, list.TagName == "OL" ? $"{n}." : depth % 2 == 0 ? "•" : "◦"));
                n++;
                foreach (var sub in nested)
                    List(sub, depth + 1);
            }
        }

        private void Table(IElement table, int indent)
        {
            var rows = table.QuerySelectorAll("tr").Where(r => r.Closest("table") == table).ToList();
            var cells = rows.Select(r => r.Children.Where(c => c.TagName is "TD" or "TH" && !Skip(c)).ToList()).ToList();
            // A one-cell table is only layout: read what's inside it as blocks
            if (cells.Sum(r => r.Count) <= 1)
            {
                foreach (var c in cells.SelectMany(r => r))
                    Blocks(c, indent);
                Flush(indent);
                return;
            }
            var output = new List<IReadOnlyList<TableCell>>();
            foreach (var row in cells)
            {
                var line = new List<TableCell>();
                foreach (var c in row)
                {
                    var spans = new List<TextSpan>();
                    InlineChildren(c, spans, c.TagName == "TH", false, null);
                    var tidy = Tidy(spans);
                    if (tidy.Count == 0 && c.QuerySelector("img") is { } img && img.GetAttribute("alt") is { Length: > 0 } alt)
                        tidy = [new TextSpan(alt, Italic: true)];
                    line.Add(new TableCell(tidy, c.TagName == "TH", Math.Max(1, Int(c, "colspan"))));
                }
                if (line.Any(c => c.Spans.Count > 0))
                    output.Add(line);
            }
            if (output.Count > 0)
                Output.Add(new TableBlock(output));
        }

        private void Figure(IElement e)
        {
            var img = e.TagName == "IMG" ? e : e.QuerySelector("img");
            if (img is null || ImageUrl(img) is not { } url)
                return;
            int w = Int(img, "width"), h = Int(img, "height");
            if (w is > 0 and < 40)
                return;   // an icon
            var caption = e.QuerySelector("figcaption, .thumbcaption, .gallerytext") is { } c ? Collapse(c.TextContent) : null;
            Output.Add(new ImageBlock(url, w, h, string.IsNullOrEmpty(caption) ? null : caption));
        }

        /// <summary>Pictures inside a paragraph go before its text.</summary>
        private void Images(IElement p)
        {
            foreach (var f in p.QuerySelectorAll("span[typeof='mw:File']").Where(f => f.QuerySelector("img") is { } i && Int(i, "width") is 0 or >= 80))
                Figure(f);
        }

        private void Gallery(IElement ul)
        {
            var images = new List<ImageBlock>();
            foreach (var box in ul.QuerySelectorAll("li.gallerybox"))
                if (box.QuerySelector("img") is { } img && ImageUrl(img) is { } url)
                {
                    var caption = box.QuerySelector(".gallerytext") is { } c ? Collapse(c.TextContent) : null;
                    images.Add(new ImageBlock(url, Int(img, "width"), Int(img, "height"), string.IsNullOrEmpty(caption) ? null : caption));
                }
            if (images.Count > 0)
                Output.Add(new GalleryBlock(images));
        }

        private static void InlineChildren(INode parent, List<TextSpan> spans, bool bold, bool italic, string? href)
        {
            foreach (var child in parent.ChildNodes)
                Inline(child, spans, bold, italic, href);
        }

        private static void Inline(INode node, List<TextSpan> spans, bool bold, bool italic, string? href)
        {
            switch (node)
            {
                case IText t:
                    if (t.Data.Length > 0)
                        spans.Add(new TextSpan(t.Data, bold, italic, href));
                    break;
                case IElement e when Skip(e):
                    break;
                case IElement { TagName: "BR" }:
                    spans.Add(new TextSpan("\n"));
                    break;
                case IElement { TagName: "SPAN" or "IMG" } e when IconText(e) is { } icon:
                    spans.Add(new TextSpan(icon, bold, true, null));
                    break;
                case IElement { TagName: "IMG" or "FIGURE" or "UL" or "OL" or "TABLE" }:
                    break;
                case IElement { TagName: "SPAN" } e when e.GetAttribute("typeof") == "mw:File":
                    break;
                case IElement e:
                    var b = bold || e.TagName is "B" or "STRONG" or "TH" or "DT";
                    var i = italic || e.TagName is "I" or "EM" or "CITE";
                    var h = e.TagName == "A" && e.GetAttribute("href") is { Length: > 0 } link ? Absolute(link) : href;
                    InlineChildren(e, spans, b, i, h);
                    if (e.TagName is "P" or "DIV" or "LI")
                        spans.Add(new TextSpan("\n"));
                    break;
            }
        }

        /// <summary>Collapses whitespace like a browser, trims the ends and joins neighbours with the same style.</summary>
        private static List<TextSpan> Tidy(List<TextSpan> spans)
        {
            var output = new List<TextSpan>();
            var text = new StringBuilder();
            TextSpan? style = null;
            var lastSpace = true;
            void Emit()
            {
                if (style is not null && text.Length > 0)
                    output.Add(style with { Text = text.ToString() });
                text.Clear();
            }
            foreach (var s in spans)
            {
                if (s.Text == "\n")
                {
                    if (text.Length > 0 && text[^1] == ' ')
                        text.Length--;
                    if (text.Length > 0 || output.Count > 0)
                        text.Append('\n');
                    lastSpace = true;
                    continue;
                }
                if (style is null || s.Bold != style.Bold || s.Italic != style.Italic || s.Href != style.Href)
                {
                    Emit();
                    style = s;
                }
                foreach (var ch in s.Text)
                {
                    if (char.IsWhiteSpace(ch))
                    {
                        if (!lastSpace)
                            text.Append(' ');
                        lastSpace = true;
                    }
                    else
                    {
                        text.Append(ch);
                        lastSpace = false;
                    }
                }
            }
            Emit();
            for (var k = 0; k < output.Count; k++)
                if (output[k].Text.Contains("\n\n\n"))
                    output[k] = output[k] with { Text = System.Text.RegularExpressions.Regex.Replace(output[k].Text, "\n{3,}", "\n\n") };
            // "Deku Babas ." (a space, then an icon left out): no space before punctuation
            for (var k = 1; k < output.Count; k++)
                if (output[k].Text.Length > 0 && ".,;:!?)".Contains(output[k].Text[0]) && output[k - 1].Text.EndsWith(' '))
                    output[k - 1] = output[k - 1] with { Text = output[k - 1].Text.TrimEnd() };
            for (var k = 0; k < output.Count; k++)
                output[k] = output[k] with { Text = SpacePunct().Replace(output[k].Text, "$1") };
            while (output.Count > 0 && string.IsNullOrWhiteSpace(output[0].Text))
                output.RemoveAt(0);
            while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1].Text))
                output.RemoveAt(output.Count - 1);
            if (output.Count > 0)
            {
                output[0] = output[0] with { Text = output[0].Text.TrimStart() };
                output[^1] = output[^1] with { Text = output[^1].Text.TrimEnd() };
            }
            return output;
        }

        private static string Collapse(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@" +([.,;:!?)])")]
    private static partial System.Text.RegularExpressions.Regex SpacePunct();
}
