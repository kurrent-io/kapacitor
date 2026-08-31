using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Capacitor.App.Services;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Capacitor.App.Views;

/// Maps the markdown constructs agents actually emit to Avalonia controls. Anything else
/// renders as its literal source text — degraded, never dropped.
public static class MarkdownBlocks {
    // Precise source locations are what let an unmapped inline fall back to its own source text:
    // without them every inline span is empty and that fallback prints the document's first
    // character. They change recorded positions only, never which constructs parse.
    static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAutoLinks().UsePipeTables().UsePreciseSourceLocation().Build();
    static readonly FontFamily Mono = new("Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace");
    const double LineSpacing = 1.6;

    static IBrush Brush(string key) => Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

    public static Control Build(string markdown, ICommand? openLink) {
        var document = Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel { Spacing = 8 };
        // A link's glyph descenders hang below its button, so the text part inside a link must not
        // clip; the part is created after the button's template, so only a style reaches it.
        panel.Styles.Add(new Style(x => x.OfType<HyperlinkButton>().Descendant().Is<TextBlock>()) {
            Setters = { new Setter(Visual.ClipToBoundsProperty, false) },
        });
        foreach (var block in document) panel.Children.Add(BuildBlock(markdown, block, openLink));
        return panel;
    }

    static Control BuildBlock(string source, Block block, ICommand? openLink) => block switch {
        ParagraphBlock p     => InlineText(source, p.Inline, openLink, 13.5, bold: false),
        HeadingBlock h       => InlineText(source, h.Inline, openLink, h.Level switch { 1 => 18, 2 => 16, _ => 14.5 }, bold: true),
        FencedCodeBlock f    => CodeBlock(f),
        CodeBlock c          => CodeBlock(c),
        ListBlock list       => List(source, list, openLink),
        QuoteBlock quote     => Quote(source, quote, openLink),
        Table table          => Table(source, table, openLink),
        ThematicBreakBlock   => new Border { Height = 1, Background = Brush("KcapBorderBrush"), Margin = new Thickness(0, 4) },
        _                    => Literal(source, block),
    };

    static Control InlineText(string source, ContainerInline? inlines, ICommand? openLink, double fontSize, bool bold) {
        // A hard break is a segment boundary rather than an inline, for the reason on Flat.
        var segments = new List<List<MdInline>> { new() };
        if (inlines is not null)
            foreach (var inline in inlines) {
                if (inline is LineBreakInline { IsHard: true }) segments.Add([]);
                else segments[^1].Add(inline);
            }

        if (segments.Count == 1) return Segment(source, segments[0], openLink, fontSize, bold);
        var panel = new StackPanel();
        foreach (var segment in segments) panel.Children.Add(Segment(source, segment, openLink, fontSize, bold));
        return panel;
    }

    static SelectableTextBlock Segment(
            string source, List<MdInline> inlines, ICommand? openLink, double fontSize, bool bold) {
        var text = new SelectableTextBlock {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            LineHeight = fontSize * LineSpacing,
            Foreground = Brush("KcapTextBrush"),
        };
        foreach (var inline in inlines) AddInline(source, text.Inlines!, inline, openLink, fontSize, text.FontWeight);
        return text;
    }

    // Avalonia 12's line breaker never finishes laying out a text block whose inlines carry a
    // newline while the parent leaves height unconstrained — every StackPanel and ScrollViewer.
    // Inline content is single-line: a soft break is a space, as CommonMark renders it. Multi-line
    // source belongs on Text, which lays out fine.
    static string Flat(string text) => text.ReplaceLineEndings(" ");

    static string SpanText(string source, SourceSpan span) =>
        span.Start >= 0 && span.End >= span.Start && span.End < source.Length
            ? source.Substring(span.Start, span.Length)
            : "";

    static void AddInlines(string source, InlineCollection target, ContainerInline? container, ICommand? openLink, double fontSize, FontWeight weight) {
        if (container is null) return;
        foreach (var inline in container) AddInline(source, target, inline, openLink, fontSize, weight);
    }

    static void AddInline(string source, InlineCollection target, MdInline inline, ICommand? openLink, double fontSize, FontWeight weight) {
        switch (inline) {
            case LiteralInline literal:
                target.Add(new Run(Flat(literal.Content.ToString())));
                break;
            case EmphasisInline emphasis: {
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(source, span.Inlines, emphasis, openLink, fontSize, weight);
                target.Add(span);
                break;
            }
            case CodeInline code:
                target.Add(new Run(Flat(code.Content)) { FontFamily = Mono });
                break;
            case HtmlEntityInline entity:
                target.Add(new Run(Flat(entity.Transcoded.ToString())));
                break;
            case LineBreakInline:
                target.Add(new Run(" "));
                break;
            case LinkInline { IsImage: true } image:
                target.Add(new Run(Flat(SpanText(source, image.Span))));
                break;
            case LinkInline link when LinkPolicy.IsOpenable(link.Url):
                target.Add(Link(LinkButton(Flat(PlainText(source, link)), link.Url!, openLink, fontSize, weight)));
                break;
            case LinkInline link:
                AddInlines(source, target, link, openLink, fontSize, weight);
                break;
            case AutolinkInline auto when LinkPolicy.IsOpenable(auto.Url):
                target.Add(Link(LinkButton(auto.Url, auto.Url, openLink, fontSize, weight)));
                break;
            case AutolinkInline auto:
                target.Add(new Run(Flat(auto.Url)));
                break;
            case HtmlInline html:
                target.Add(new Run(Flat(html.Tag)));
                break;
            case ContainerInline nested:
                AddInlines(source, target, nested, openLink, fontSize, weight);
                break;
            default:
                target.Add(new Run(Flat(SpanText(source, inline.Span))));
                break;
        }
    }

    // NavigateUri stays unset on purpose: set, the control opens the URI itself and the policy
    // in the command would never run.
    static HyperlinkButton LinkButton(string label, string url, ICommand? openLink, double fontSize, FontWeight weight) => new() {
        Content = label,
        Command = openLink,
        CommandParameter = url,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        MinHeight = 0,
        MinWidth = 0,
        FontSize = fontSize,
        FontWeight = weight,
        Cursor = new Cursor(StandardCursorType.Hand),
        Foreground = Brush("KcapAccentBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // A hosted control's baseline is its bottom edge, and the line adopts the largest run
    // baseline: a button as tall as the line drags the whole line's text down to its bottom. The
    // button is therefore laid out at the font's natural line height and sized to its ascent,
    // so its bottom is a baseline no taller than the text's own, its glyphs sit on that line, and
    // their descenders overflow it, which the block's style lets every part draw.
    static InlineUIContainer Link(HyperlinkButton button) {
        button.VerticalContentAlignment = VerticalAlignment.Top;
        button.ClipToBounds = false;
        button.UseLayoutRounding = false;
        button.SetValue(TextBlock.LineHeightProperty, double.NaN);
        button.AttachedToVisualTree += OnLinkAttached;
        return new() { Child = button };
    }

    // Sized once the inherited font is known.
    static void OnLinkAttached(object? sender, VisualTreeAttachmentEventArgs e) {
        if (sender is HyperlinkButton button) button.Height = Ascent(button.FontFamily, button.FontWeight, button.FontSize);
    }

    static readonly Dictionary<(string Family, FontWeight Weight, double Size), double> Ascents = [];

    static double Ascent(FontFamily family, FontWeight weight, double fontSize) {
        var key = (family.Name, weight, fontSize);
        if (!Ascents.TryGetValue(key, out var ascent))
            Ascents[key] = ascent = new TextLayout("x", new Typeface(family, FontStyle.Normal, weight), fontSize, Brushes.Black).TextLines[0].Baseline;
        return ascent;
    }

    static string PlainText(string source, ContainerInline container) =>
        string.Concat(container.Select(inline => inline switch {
            LiteralInline literal    => literal.Content.ToString(),
            CodeInline code          => code.Content,
            HtmlEntityInline entity  => entity.Transcoded.ToString(),
            LineBreakInline          => " ",
            ContainerInline nested   => PlainText(source, nested),
            _                        => SpanText(source, inline.Span),
        }));

    static Control CodeBlock(LeafBlock code) => new Border {
        Background = Brush("KcapSurfaceBrush"),
        BorderBrush = Brush("KcapBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 8),
        Child = new SelectableTextBlock {
            Text = code.Lines.ToString().TrimEnd('\n', '\r'),
            FontFamily = Mono,
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush("KcapTextBrush"),
        },
    };

    static Control List(string source, ListBlock list, ICommand? openLink) {
        var panel = new StackPanel { Spacing = 4 };
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>()) {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
            var marker = new TextBlock {
                Text = list.IsOrdered ? $"{index++}." : "•",
                Foreground = Brush("KcapMutedBrush"),
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var content = new StackPanel { Spacing = 4 };
            foreach (var child in item) content.Children.Add(BuildBlock(source, child, openLink));
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    // Auto columns wrap only once capped; the widest column takes what is left so a prose column
    // wraps inside the pane instead of pushing the table past it.
    const double AutoColumnCap = 240;

    static Control Table(string source, Table table, ICommand? openLink) {
        var rows = table.OfType<TableRow>().ToList();
        var columns = Math.Max(table.ColumnDefinitions.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        var grid = new Grid { Name = "MarkdownTable" };
        if (columns == 0) return grid;

        var weight = new int[columns];
        foreach (var row in rows)
            foreach (var (cell, column) in row.OfType<TableCell>().Select((c, i) => (c, i)))
                if (column < columns) weight[column] += cell.Span.Length;
        var widest = Array.IndexOf(weight, weight.Max());
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(c == widest
                ? new ColumnDefinition(1, GridUnitType.Star)
                : new ColumnDefinition(GridLength.Auto) { MaxWidth = AutoColumnCap });

        for (var r = 0; r < rows.Count; r++) {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var (cell, c) in rows[r].OfType<TableCell>().Select((cell, i) => (cell, i))) {
                if (c >= columns) break;
                var align = table.ColumnDefinitions.ElementAtOrDefault(c)?.Alignment switch {
                    TableColumnAlign.Right  => TextAlignment.Right,
                    TableColumnAlign.Center => TextAlignment.Center,
                    _                       => TextAlignment.Left,
                };
                var content = InlineText(source, (cell.FirstOrDefault() as ParagraphBlock)?.Inline, openLink, 13, bold: rows[r].IsHeader);
                Align(content, align);
                var border = new Border {
                    Padding = new Thickness(8, 5),
                    BorderBrush = Brush("KcapBorderBrush"),
                    BorderThickness = new Thickness(0, 0, 0, r == rows.Count - 1 ? 0 : 1),
                    Child = content,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new Border {
            BorderBrush = Brush("KcapBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid,
        };
    }

    static void Align(Control control, TextAlignment alignment) {
        switch (control) {
            case SelectableTextBlock text: text.TextAlignment = alignment; break;
            case Panel panel: foreach (var child in panel.Children) Align(child, alignment); break;
        }
    }

    static Control Quote(string source, QuoteBlock quote, ICommand? openLink) {
        var content = new StackPanel { Spacing = 6 };
        foreach (var child in quote) content.Children.Add(BuildBlock(source, child, openLink));
        return new Border {
            BorderBrush = Brush("KcapBorderBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Child = content,
        };
    }

    static Control Literal(string source, Block block) => new SelectableTextBlock {
        Text = SpanText(source, block.Span),
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13.5,
        Foreground = Brush("KcapTextBrush"),
    };
}
