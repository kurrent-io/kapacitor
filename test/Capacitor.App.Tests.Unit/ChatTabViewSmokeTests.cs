using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the Chat tab on its own: the view is hosted directly with a
/// ChatTabViewModel DataContext, so what is under test is exactly ChatTabView's list virtualization
/// and follow-tail, not WorkspaceView's tab swap. Same session rules as every UI suite here —
/// RunOnUiAsync plus [NotInParallel("AvaloniaSession")].
public class ChatTabViewSmokeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLinkLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"See [docs](https://example.com/docs) now."}]}}""";
    const string ToolCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
    const string ToolResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string ToolErrorLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"boom","is_error":true}]}}""";
    static readonly TimeSpan CrDelay = TimeSpan.FromMilliseconds(150);

    sealed class Host {
        bool _shown;

        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeTerminalAttachClientFactory Attach { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }
        public ChatTabView View { get; }
        public Window Window { get; }
        public ScrollViewer Scroll => View.GetVisualDescendants().OfType<ScrollViewer>().First();
        public bool HasScroll => View.GetVisualDescendants().OfType<ScrollViewer>().Any();
        public TextBox Composer => View.FindControl<TextBox>("ComposerInput")!;

        /// `show: false` leaves the window unshown, so the view has no template and no
        /// ScrollViewer until Show() is called — the order production takes, where the tab's
        /// first read starts before the workspace view exists.
        public Host(bool show = true) {
            Terminal = new TerminalTabViewModel("a1", Daemon, Attach.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel("a1", Daemon, Terminal, TranscriptProjection.For("claude"), Opener, Time, Permissions);
            View = new ChatTabView { DataContext = Chat };
            Window = new Window { Content = View, Width = 800, Height = 600 };
            if (!show) return;
            Show();
        }

        public void Show() {
            Window.Show();
            _shown = true;
            Settle();
        }

        public void Settle() {
            Dispatcher.UIThread.RunJobs();
            if (_shown) Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// A loaded transcript over a read-write attached terminal — the one state in which the
        /// composer's send is accepted.
        public async Task<FakeTerminalAttachClient> AttachAsync(string path) {
            await LoadAsync(path);
            var client = Attach.Created[^1];
            await client.TriggerAttached([]);
            Dispatcher.UIThread.RunJobs();
            return client;
        }

        /// Real key events into the focused composer: the TextBox's own key handling is exactly
        /// what these tests are about, so the text cannot be poked into the view model instead.
        public void Type(string text) {
            Composer.Focus();
            Dispatcher.UIThread.RunJobs();
            Window.KeyTextInput(text);
            Dispatcher.UIThread.RunJobs();
        }

        public void PressEnter(RawInputModifiers modifiers) {
            Window.KeyPressQwerty(PhysicalKey.Enter, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        public async Task LoadAsync(string path) {
            Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path });
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
            Settle();
        }

        public async Task AppendAndTickAsync(string path, int lines) {
            File.AppendAllLines(path, Enumerable.Repeat(UserLine, lines));
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public bool AtBottom() => Scroll.Offset.Y + Scroll.Viewport.Height >= Scroll.Extent.Height - 1;

        public async Task CloseAsync() {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            await Chat.TeardownAsync();
            await Terminal.TeardownAsync();
        }
    }

    /// Pins the two costs a long transcript could impose on the UI thread: one collection
    /// notification for the whole initial load, and containers for the viewport only.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_large_initial_load_is_one_notification_into_a_bounded_number_of_containers() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("big.jsonl", Enumerable.Repeat(UserLine, 5000).ToArray());
            var notifications = 0;
            ((INotifyCollectionChanged)host.Chat.Items).CollectionChanged += (_, _) => notifications++;

            await host.LoadAsync(path);

            await Assert.That(host.Chat.Items).Count().IsEqualTo(5000);
            await Assert.That(notifications).IsEqualTo(1);
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            await Assert.That(items.GetRealizedContainers().Count()).IsLessThan(200);
            await host.CloseAsync();
        });
    }

    /// Pins follow-tail's whole contract: it tracks the bottom, leaves a scrolled-up reader where
    /// they are, and abandons a scroll it had already decided on if the reader moves first.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_tracks_the_bottom_and_leaves_a_scrolled_up_reader_alone() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("t.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendAndTickAsync(path, 20);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();

            host.Scroll.Offset = new Vector(0, 0);
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 20);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);

            // At the bottom, append, then scroll up before the layout pass completes. The view
            // subscribes first, so this handler runs after it has decided to follow and captured
            // the offset — the one ordering that reaches the abandon path.
            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await Assert.That(host.AtBottom()).IsTrue();
            void ScrollUp(object? sender, NotifyCollectionChangedEventArgs e) => host.Scroll.Offset = new Vector(0, 0);
            ((INotifyCollectionChanged)host.Chat.Items).CollectionChanged += ScrollUp;
            await host.AppendAndTickAsync(path, 20);
            ((INotifyCollectionChanged)host.Chat.Items).CollectionChanged -= ScrollUp;
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.CloseAsync();
        });
    }

    /// Pins that the initial load lands the reader at the bottom even when it completes before the
    /// view's first layout pass — an unshown window, and a tab collapsed from the start — so there
    /// is no ScrollViewer to read "was at end" from when the rows arrive.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_initial_load_before_the_first_layout_still_lands_the_reader_at_the_bottom() {
        await RunOnUiAsync(async () => {
            var unshown = new Host(show: false);
            await unshown.LoadAsync(Tmp.CreateFile("unshown.jsonl", Enumerable.Repeat(UserLine, 60).ToArray()));
            await Assert.That(unshown.Chat.Items).Count().IsEqualTo(60);
            await Assert.That(unshown.HasScroll).IsFalse();

            unshown.Show();

            await Assert.That(unshown.AtBottom()).IsTrue();
            await unshown.CloseAsync();

            var collapsed = new Host(show: false);
            collapsed.View.IsVisible = false;
            collapsed.Show();
            await collapsed.LoadAsync(Tmp.CreateFile("hidden.jsonl", Enumerable.Repeat(UserLine, 60).ToArray()));
            await Assert.That(collapsed.HasScroll).IsFalse();

            collapsed.View.IsVisible = true;
            collapsed.Settle();

            await Assert.That(collapsed.AtBottom()).IsTrue();
            await collapsed.CloseAsync();
        });
    }

    /// Pins that appends arriving while the surface is collapsed still leave the reader at the
    /// bottom once it is laid out again — the shape a Chat tab sitting behind the Terminal tab is
    /// in, where the view arms at most one pending scroll however many appends land.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Appends_while_the_surface_is_collapsed_still_land_the_reader_at_the_bottom() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("collapsed.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            host.View.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await host.AppendAndTickAsync(path, 20);
            await host.AppendAndTickAsync(path, 20);
            await Assert.That(host.Chat.Items).Count().IsEqualTo(100);

            host.View.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Pins the assistant template's one silent binding: a link rendered inside a chat row opens
    /// through the tab's own command, reached across the item boundary, and opens exactly once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_in_an_assistant_row_opens_through_the_tabs_command() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("link.jsonl", [AssistantLinkLine]));

            await Assert.That(host.Chat.Items).Count().IsEqualTo(1);

            var link = host.View.GetVisualDescendants().OfType<HyperlinkButton>().Single();
            var origin = link.TranslatePoint(new Point(2, 2), host.Window)!.Value;
            host.Window.MouseDown(origin, MouseButton.Left);
            host.Window.MouseUp(origin, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(host.Opener.Opened).IsEquivalentTo(new[] { "https://example.com/docs" });
            await host.CloseAsync();
        });
    }

    /// Pins the tool row's outcome colour: the glyph takes the brush ToolOutcomeBrushConverter
    /// maps for the paired result, danger for an error and accent for a success.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_paired_tool_row_paints_its_glyph_with_the_outcome_brush() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("tools.jsonl",
                [ToolCallLine, ToolResultLine, ToolCallLine.Replace("t1", "t2"), ToolErrorLine.Replace("t1", "t2")]));

            await Assert.That(host.Chat.Items.Cast<ToolCallItem>().Select(i => i.Outcome))
                .IsEquivalentTo([ToolOutcome.Done, ToolOutcome.Error], CollectionOrdering.Matching);
            var glyphs = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text is "✓" or "✕").ToList();

            await Assert.That(glyphs.Select(g => g.Text!)).IsEquivalentTo(["✓", "✕"], CollectionOrdering.Matching);
            await Assert.That(glyphs[0].Foreground).IsSameReferenceAs(Brush(isError: false));
            await Assert.That(glyphs[1].Foreground).IsSameReferenceAs(Brush(isError: true));
            await host.CloseAsync();
        });
    }

    static object? Brush(bool isError) =>
        ToolOutcomeBrushConverter.Instance.Convert(isError, typeof(IBrush), null, CultureInfo.InvariantCulture);

    /// Pins that Enter reaches the send: the composer consumes it, the typed text leaves as one
    /// bracketed paste followed by the CR, and no newline is left behind in the box.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_sends_the_typed_text_and_leaves_no_newline_behind() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var client = await host.AttachAsync(Tmp.CreateFile("send.jsonl", UserLine));
            host.Type("hi");
            await Assert.That(host.Composer.Text).IsEqualTo("hi");

            host.PressEnter(RawInputModifiers.None);

            await Assert.That(host.Composer.Text ?? "").IsEqualTo("");
            await Assert.That(host.Chat.ComposerText).IsEqualTo("");
            await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");
            await Assert.That(client.SentInput[0]).IsEquivalentTo(TerminalInputEncoder.Paste("hi"));

            host.Time.Advance(CrDelay);
            await host.Terminal.PendingDeliveryForTesting!;
            await Assert.That(client.SentInput.Select(Encoding.UTF8.GetString))
                .IsEquivalentTo(["\x1b[200~hi\x1b[201~", "\r"], CollectionOrdering.Matching);
            await host.CloseAsync();
        });
    }

    /// Pins the other half of the key contract: Shift+Enter stays the TextBox's own newline and
    /// sends nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Shift_enter_inserts_a_newline_and_sends_nothing() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var client = await host.AttachAsync(Tmp.CreateFile("newline.jsonl", UserLine));
            host.Type("hi");

            host.PressEnter(RawInputModifiers.Shift);

            await Assert.That(host.Composer.Text).IsEqualTo("hi" + Environment.NewLine);
            await Assert.That(client.SentInput).IsEmpty();
            await host.CloseAsync();
        });
    }

    /// Pins that a refused send still consumes Enter: nothing goes out and the text survives
    /// intact, so the user can send it again once the hint's reason clears.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_on_a_refused_send_neither_sends_nor_inserts_a_newline() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Type("hi");
            await Assert.That(host.Terminal.CanAcceptText).IsFalse();

            host.PressEnter(RawInputModifiers.None);

            await Assert.That(host.Composer.Text).IsEqualTo("hi");
            await Assert.That(host.Chat.ComposerText).IsEqualTo("hi");
            await Assert.That(host.Attach.Created).IsEmpty();
            await host.CloseAsync();
        });
    }

    /// Pins the composer's width: it spans the pane like the Home goal box rather than capping
    /// at the assistant column's width on the left.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_composer_spans_the_pane() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Window.UpdateLayout();

            await Assert.That(host.Composer.Bounds.Width).IsGreaterThan(host.View.Bounds.Width - 100);
            await host.CloseAsync();
        });
    }

    /// Pins that focusing the composer draws no ring of its own: the card is the input's
    /// boundary, so the theme's focused border and fill stay off.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_focused_composer_draws_no_ring_inside_its_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Composer.Focus();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            var ring = host.Composer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
            await Assert.That(host.Composer.IsFocused).IsTrue();
            await Assert.That(ring.BorderThickness).IsEqualTo(new Thickness(0));
            await Assert.That(ring.Background is null || ring.Background is ISolidColorBrush { Color.A: 0 }).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Pins the timeline's rhythm: consecutive tool rows sit close together, and a run of them
    /// keeps a clear gap before the assistant text that follows.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_rows_stack_densely_and_keep_their_distance_from_text() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("rows.jsonl",
                [ToolCallLine, ToolResultLine, ToolCallLine.Replace("t1", "t2"), ToolResultLine.Replace("t1", "t2"), AssistantLinkLine]));

            var rows = host.View.GetVisualDescendants().OfType<StackPanel>()
                .Where(p => p.Orientation == Orientation.Horizontal && p.DataContext is ToolCallItem).ToList();
            var text = host.View.GetVisualDescendants().OfType<MarkdownView>().Single();
            double Top(Control c) => c.TranslatePoint(new Point(0, 0), host.View)!.Value.Y;
            double Bottom(Control c) => Top(c) + c.Bounds.Height;

            await Assert.That(rows).Count().IsEqualTo(2);
            await Assert.That(Top(rows[1]) - Bottom(rows[0])).IsLessThan(10);
            await Assert.That(Top(text) - Bottom(rows[1])).IsGreaterThanOrEqualTo(12);
            await host.CloseAsync();
        });
    }

    /// Pins follow-tail against virtualization's estimate: an appended row far taller than the
    /// rows around it still leaves the reader at the real bottom once it is measured.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_lands_at_the_bottom_when_the_appended_row_is_taller_than_the_estimate() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("tall.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            var reply = string.Join("\\n\\n", Enumerable.Range(1, 25).Select(i => $"Paragraph {i} of a long reply."));
            File.AppendAllLines(path, ["{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + reply + "\"}]}}"]);
            host.Time.Advance(ChatTabViewModel.PollInterval);
            await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
            host.Settle();

            await Assert.That(host.Chat.Items.Count).IsEqualTo(61);
            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Pins the system note's surface: a muted card carrying the note as markdown, distinct from
    /// the user bubble and the assistant prose around it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_system_note_renders_as_a_muted_markdown_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("note.jsonl", [
                """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll **good**.\n</result>\n</task-notification>"}}""",
            ]));

            var card = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("systemNote"));
            var text = card.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
            await Assert.That(text.Select(t => t.Inlines?.Text ?? t.Text ?? "")).IsEquivalentTo(new[] { "Agent finished", "All good." }, CollectionOrdering.Matching);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Card_renders_with_its_buttons_and_the_row_collapses_when_empty() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var row = host.View.FindControl<Border>("PermissionRow")!;
            await Assert.That(row.IsVisible).IsFalse();

            host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Bash"));
            await WaitUntilAsync(() => host.Chat.PendingPermissions.Count == 1, what: "the card");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.IsVisible).IsTrue();
            var buttons = row.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString() ?? "").ToArray();
            await Assert.That(buttons).IsEquivalentTo(new[] { "Deny", "Allow always", "Allow" });

            host.Permissions.Remove("r1");
            await WaitUntilAsync(() => host.Chat.PendingPermissions.Count == 0, what: "cleared");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.IsVisible).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Question_card_renders_options_other_and_coexists_with_a_permission_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Permissions.Add(PermissionEntries.Entry("r1"));
            host.Permissions.Add(PermissionEntries.Question("q1"));
            host.Settle();

            // A plain-text button (Allow, Deny, Submit…) yields its Content directly; an option
            // button's Content is the Label/Description StackPanel, so its first TextBlock stands in.
            var buttons = host.View.GetVisualDescendants().OfType<Button>()
                .Select(b => b.Content as string ?? b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text)
                .ToList();
            await Assert.That(buttons).Contains("Allow");
            await Assert.That(buttons).Contains("A");
            var otherBoxes = host.View.GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.PlaceholderText == "Other…").ToList();
            await Assert.That(otherBoxes.Count).IsEqualTo(1);
            await Assert.That(host.View.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Pick")).IsTrue();
        });
    }
}
