using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Capacitor.App.Services;
using Markdig;
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
        new MarkdownPipelineBuilder().UseAutoLinks().UsePreciseSourceLocation().Build();
    static readonly FontFamily Mono = new("Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace");

    static IBrush Brush(string key) => Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

    public static Control Build(string markdown, ICommand? openLink) {
        var document = Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel { Spacing = 8 };
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
            LineHeight = fontSize * 1.6,
            Foreground = Brush("KcapTextBrush"),
        };
        foreach (var inline in inlines) AddInline(source, text.Inlines!, inline, openLink);
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

    static void AddInlines(string source, InlineCollection target, ContainerInline? container, ICommand? openLink) {
        if (container is null) return;
        foreach (var inline in container) AddInline(source, target, inline, openLink);
    }

    static void AddInline(string source, InlineCollection target, MdInline inline, ICommand? openLink) {
        switch (inline) {
            case LiteralInline literal:
                target.Add(new Run(Flat(literal.Content.ToString())));
                break;
            case EmphasisInline emphasis: {
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(source, span.Inlines, emphasis, openLink);
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
                target.Add(new InlineUIContainer { Child = LinkButton(Flat(PlainText(source, link)), link.Url!, openLink) });
                break;
            case LinkInline link:
                AddInlines(source, target, link, openLink);
                break;
            case AutolinkInline auto when LinkPolicy.IsOpenable(auto.Url):
                target.Add(new InlineUIContainer { Child = LinkButton(auto.Url, auto.Url, openLink) });
                break;
            case AutolinkInline auto:
                target.Add(new Run(Flat(auto.Url)));
                break;
            case HtmlInline html:
                target.Add(new Run(Flat(html.Tag)));
                break;
            case ContainerInline nested:
                AddInlines(source, target, nested, openLink);
                break;
            default:
                target.Add(new Run(Flat(SpanText(source, inline.Span))));
                break;
        }
    }

    // NavigateUri stays unset on purpose: set, the control opens the URI itself and the policy
    // in the command would never run.
    static HyperlinkButton LinkButton(string label, string url, ICommand? openLink) => new() {
        Content = label,
        Command = openLink,
        CommandParameter = url,
        Padding = new Thickness(0),
        Cursor = new Cursor(StandardCursorType.Hand),
        Foreground = Brush("KcapAccentBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

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
