using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using ReactiveUI;
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
            var (window, root, opened) = Show("Bad [link](javascript:alert(1)) and <b>html</b>\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
            try {
                await Assert.That(All<HyperlinkButton>(root)).IsEmpty();
                await Assert.That(All<SelectableTextBlock>(root).Any(t => t.Inlines!.Any(i => i is Run { Text: "link" }))).IsTrue();
                await Assert.That(All<SelectableTextBlock>(root).Any(t => Reads(t).Contains("| a | b |"))).IsTrue();
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
}
