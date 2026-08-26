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

namespace Capacitor.App.Views;

/// Maps the markdown constructs agents actually emit to Avalonia controls. Anything else
/// renders as its literal source text — degraded, never dropped.
public static class MarkdownBlocks {
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAutoLinks().Build();
    static readonly FontFamily Mono = new("Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace");

    static IBrush Brush(string key) => Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray;

    public static Control Build(string markdown, ICommand? openLink) {
        var document = Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in document) panel.Children.Add(BuildBlock(markdown, block, openLink));
        return panel;
    }

    static Control BuildBlock(string source, Block block, ICommand? openLink) => block switch {
        ParagraphBlock p     => InlineText(p.Inline, openLink, 13.5, bold: false),
        HeadingBlock h       => InlineText(h.Inline, openLink, h.Level switch { 1 => 18, 2 => 16, _ => 14.5 }, bold: true),
        FencedCodeBlock f    => CodeBlock(f),
        CodeBlock c          => CodeBlock(c),
        ListBlock list       => List(source, list, openLink),
        QuoteBlock quote     => Quote(source, quote, openLink),
        ThematicBreakBlock   => new Border { Height = 1, Background = Brush("KcapBorderBrush"), Margin = new Thickness(0, 4) },
        _                    => Literal(source, block),
    };

    static SelectableTextBlock InlineText(ContainerInline? inlines, ICommand? openLink, double fontSize, bool bold) {
        var text = new SelectableTextBlock {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            LineHeight = fontSize * 1.6,
            Foreground = Brush("KcapTextBrush"),
        };
        AddInlines(text.Inlines!, inlines, openLink);
        return text;
    }

    // A newline reaching a text block's inlines makes Avalonia's line breaker spin forever
    // whenever the block's height is unconstrained (every scrolled list). Inline content is
    // therefore always single-line — a break inside a paragraph becomes a space, which is also
    // how CommonMark's soft break renders. Multi-line source belongs on Text, which is safe.
    static string Flat(string text) => text.ReplaceLineEndings(" ");

    static void AddInlines(InlineCollection target, ContainerInline? container, ICommand? openLink) {
        if (container is null) return;
        foreach (var inline in container) {
            switch (inline) {
                case LiteralInline literal:
                    target.Add(new Run(Flat(literal.Content.ToString())));
                    break;
                case EmphasisInline emphasis: {
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis, openLink);
                    target.Add(span);
                    break;
                }
                case CodeInline code:
                    target.Add(new Run(Flat(code.Content)) { FontFamily = Mono });
                    break;
                case LineBreakInline:
                    target.Add(new Run(" "));
                    break;
                case LinkInline link when !link.IsImage && LinkPolicy.IsOpenable(link.Url):
                    target.Add(new InlineUIContainer { Child = LinkButton(PlainText(link), link.Url!, openLink) });
                    break;
                case LinkInline link:
                    AddInlines(target, link, openLink);
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
                    AddInlines(target, nested, openLink);
                    break;
                default:
                    target.Add(new Run(Flat(inline.ToString() ?? "")));
                    break;
            }
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

    static string PlainText(ContainerInline container) =>
        string.Concat(container.Select(i => i is LiteralInline l ? l.Content.ToString() : i is ContainerInline c ? PlainText(c) : ""));

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

    static Control Literal(string source, Block block) {
        var span = block.Span;
        var text = span.Start >= 0 && span.End < source.Length && span.End >= span.Start
            ? source.Substring(span.Start, span.Length)
            : block.ToString() ?? "";
        return new SelectableTextBlock {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            Foreground = Brush("KcapTextBrush"),
        };
    }
}
