using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using ReactiveUI.Reactive;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class MarkdownBlocksTests {
    static (Window Window, Control Root, List<string> Opened) Show(string markdown) {
        var opened = new List<string>();
        ICommand open = ReactiveCommand.Create<string>(opened.Add);
        var view = new MarkdownView { Text = markdown, OpenLink = open, Width = 400 };
        var window = new Window { Content = view, Width = 500, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, opened);
    }

    static IEnumerable<T> All<T>(Control root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    /// A text block built from inlines leaves Text null and carries its characters on the
    /// inline collection, so a "what does this block read as" assertion has to consult both.
    static string Reads(TextBlock block) => block.Text ?? block.Inlines?.Text ?? "";

    /// Pins the block map: emphasis and code spans as inlines, fenced code, bullets and a quote.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Paragraph_inlines_code_blocks_lists_and_quotes_render() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("Some **bold** and *em* and `code`.\n\n```\nvar x = 1;\n```\n\n- one\n- two\n\n> quoted\n\n---\n");
            try {
                var paragraph = All<SelectableTextBlock>(root).First();
                await Assert.That(paragraph.Inlines!.OfType<Bold>().Count()).IsEqualTo(1);
                await Assert.That(paragraph.Inlines!.OfType<Italic>().Count()).IsEqualTo(1);
                await Assert.That(paragraph.Inlines!.OfType<Run>().Any(r => r.Text == "code")).IsTrue();
                await Assert.That(All<SelectableTextBlock>(root).Any(t => t.Text == "var x = 1;")).IsTrue();
                await Assert.That(All<TextBlock>(root).Count(t => t.Text == "•")).IsEqualTo(2);
                await Assert.That(All<SelectableTextBlock>(root).Any(t => t.Inlines!.Any(i => i is Run { Text: "quoted" }))).IsTrue();
            } finally { window.Close(); }
        });
    }

    /// Pins the link affordance: a policy-approved link opens through the command, never itself,
    /// and answers to both a pointer and the keyboard.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_allowed_link_is_a_button_that_opens_once_by_pointer_and_once_by_keyboard() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("See [docs](https://example.com/docs) now.");
            try {
                var button = All<HyperlinkButton>(root).Single();
                await Assert.That(button.NavigateUri).IsNull();
                await Assert.That(button.CommandParameter).IsEqualTo("https://example.com/docs");

                var origin = button.TranslatePoint(new Point(2, 2), window)!.Value;
                window.MouseDown(origin, MouseButton.Left);
                window.MouseUp(origin, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(opened).IsEquivalentTo(new[] { "https://example.com/docs" });

                button.Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(opened).Count().IsEqualTo(2);
            } finally { window.Close(); }
        });
    }

    /// Pins the trust boundary and the degrade rule: a refused scheme leaves no clickable
    /// affordance behind, and constructs with no mapping keep their source text.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_disallowed_link_and_unknown_constructs_render_as_plain_text() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("Bad [link](javascript:alert(1)) and <b>html</b>\n\n<div>raw</div>\n");
            try {
                await Assert.That(All<HyperlinkButton>(root)).IsEmpty();
                await Assert.That(All<SelectableTextBlock>(root).Any(t => t.Inlines!.Any(i => i is Run { Text: "link" }))).IsTrue();
                await Assert.That(All<SelectableTextBlock>(root).Any(t => Reads(t).Contains("<div>raw</div>"))).IsTrue();
                await Assert.That(opened).IsEmpty();
                await Assert.That(root.GetVisualDescendants().OfType<Control>().Any(c => c.Focusable && c is not SelectableTextBlock)).IsFalse();
            } finally { window.Close(); }
        });
    }

    /// Pins what a newline may become: a hard break splits the paragraph into stacked blocks, a
    /// soft break is a space, and an entity reaches the block decoded — no newline in any inline.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hard_breaks_stack_soft_breaks_are_spaces_and_entities_decode() {
        await RunOnUiAsync(async () => {
            var (hard, hardRoot, _) = Show("a  \nb");
            try {
                await Assert.That(All<SelectableTextBlock>(hardRoot).Select(Reads))
                    .IsEquivalentTo(["a", "b"], CollectionOrdering.Matching);
            } finally { hard.Close(); }

            var (soft, softRoot, _) = Show("a\nb");
            try {
                await Assert.That(All<SelectableTextBlock>(softRoot).Select(Reads)).IsEquivalentTo(["a b"]);
            } finally { soft.Close(); }

            var (entity, entityRoot, _) = Show("a &amp; b");
            try {
                await Assert.That(All<SelectableTextBlock>(entityRoot).Select(Reads)).IsEquivalentTo(["a & b"]);
            } finally { entity.Close(); }
        });
    }

    /// Pins the image and label rules: an image keeps its source text whatever its scheme, and a
    /// label's code span survives into the button's content.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Images_keep_their_source_text_and_link_labels_keep_their_code_spans() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("![](https://example.com/y.png) and [see `code` here](https://example.com/d)");
            try {
                await Assert.That(All<SelectableTextBlock>(root).Single().Inlines!.OfType<Run>().Any(r => r.Text == "![](https://example.com/y.png)")).IsTrue();
                await Assert.That(All<HyperlinkButton>(root).Single().Content).IsEqualTo("see code here");
            } finally { window.Close(); }
        });
    }

    /// Pins table rendering: a pipe table is a grid with one row per source row, a semibold header,
    /// inline-rendered cells, and the column alignment the separator row declares.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pipe_table_renders_as_a_grid_with_a_header_and_aligned_cells() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("| Issue | Count |\n|---|--:|\n| `x` first | 2 |\n| second | 10 |");
            try {
                var table = All<Grid>(root).Single(g => g.Name == "MarkdownTable");
                await Assert.That(table.RowDefinitions.Count).IsEqualTo(3);
                await Assert.That(table.ColumnDefinitions.Count).IsEqualTo(2);

                var cells = All<SelectableTextBlock>(table).ToList();
                await Assert.That(cells.Select(Reads)).IsEquivalentTo(["Issue", "Count", "x first", "2", "second", "10"], CollectionOrdering.Matching);
                await Assert.That(cells[0].FontWeight).IsEqualTo(FontWeight.SemiBold);
                await Assert.That(cells[2].FontWeight).IsEqualTo(FontWeight.Normal);
                await Assert.That(cells[2].Inlines!.OfType<Run>().First().FontFamily.Name).IsNotEqualTo(cells[4].FontFamily.Name);
                await Assert.That(cells[3].TextAlignment).IsEqualTo(TextAlignment.Right);
                await Assert.That(cells[2].TextAlignment).IsEqualTo(TextAlignment.Left);
            } finally { window.Close(); }
        });
    }

    /// Pins the link's place in the line: a paragraph with a link is no taller than the same
    /// paragraph without one, and the button reads in the paragraph's own size and weight.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_sits_in_the_line_without_changing_its_height() {
        await RunOnUiAsync(async () => {
            var (linked, linkedRoot, _) = Show("PR opened: [pull/588](https://example.com/pull/588) and done.");
            var (plain, plainRoot, _) = Show("PR opened: pull/588 and done.");
            try {
                linked.UpdateLayout();
                plain.UpdateLayout();
                var linkedText = All<SelectableTextBlock>(linkedRoot).Single();
                var plainText = All<SelectableTextBlock>(plainRoot).Single();
                var button = All<HyperlinkButton>(linkedRoot).Single();

                await Assert.That(Math.Abs(linkedText.Bounds.Height - plainText.Bounds.Height)).IsLessThanOrEqualTo(1);
                await Assert.That(button.FontSize).IsEqualTo(linkedText.FontSize);
                await Assert.That(button.FontWeight).IsEqualTo(linkedText.FontWeight);
                await Assert.That(button.Bounds.Height).IsLessThanOrEqualTo(linkedText.Bounds.Height);

                // The line keeps the text's own baseline, and the button's glyphs sit on it.
                var inner = All<TextBlock>(button).Single();
                var innerBaseline = ((Visual)inner).TranslatePoint(new Point(0, inner.TextLayout.TextLines[0].Baseline), linkedText)!.Value.Y;
                var lineBaseline = linkedText.TextLayout.TextLines[0].Baseline;
                await Assert.That(Math.Abs(lineBaseline - plainText.TextLayout.TextLines[0].Baseline)).IsLessThanOrEqualTo(0.5);
                await Assert.That(Math.Abs(innerBaseline - lineBaseline)).IsLessThanOrEqualTo(1);

                // The glyphs' descenders hang below the button, so nothing in it may clip.
                await Assert.That(button.ClipToBounds).IsFalse();
                await Assert.That(button.GetVisualDescendants().OfType<Visual>().Any(v => v.ClipToBounds)).IsFalse();
            } finally { linked.Close(); plain.Close(); }
        });
    }
}
