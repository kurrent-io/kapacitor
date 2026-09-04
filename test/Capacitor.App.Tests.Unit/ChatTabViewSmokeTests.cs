using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string ReadResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}}""";

    static string CallLine(int n) => ToolCallLine.Replace("\"t1\"", $"\"t{n}\"");
    static string ResultLine(int n) => ToolResultLine.Replace("\"t1\"", $"\"t{n}\"");

    static List<Control> ToolRows(ChatTabView view) => view.GetVisualDescendants().OfType<Control>()
        .Where(c => c.Name == "ToolCallRow" && c.DataContext is ToolCallItem).ToList();
    static Button Summary(ChatTabView view) => view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("toolSummary"));
    static Button? SummaryOrNull(ChatTabView view) => view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("toolSummary"));
    static ToolGroupItem OnlyGroup(Host host) => (ToolGroupItem)host.Chat.Items.Single();

    /// A synthetic pointer event hit-tests the compositor's last committed scene, which layout alone
    /// does not refresh: a control shown since the last frame is invisible to the click until the
    /// render timer ticks once more.
    static Point PresentAndLocate(Host host, Control target) {
        host.Settle();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return target.TranslatePoint(new Point(2, 2), host.Window)!.Value;
    }

    static void Click(Host host, Control target) {
        var origin = PresentAndLocate(host, target);
        host.Window.MouseDown(origin, MouseButton.Left);
        host.Window.MouseUp(origin, MouseButton.Left);
        host.Settle();
    }

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

        public async Task AppendLinesAndTickAsync(string path, params string[] lines) {
            File.AppendAllLines(path, lines);
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
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

    /// Pins the tool row's outcome colour: the status pill takes the brush ToolOutcomeBrushConverter
    /// maps for the paired result, danger for an error and accent for a success.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_paired_tool_row_paints_its_status_dot_with_the_outcome_brush() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("tools.jsonl",
                [ToolCallLine, ToolResultLine, ToolCallLine.Replace("t1", "t2"), ToolErrorLine.Replace("t1", "t2")]));
            OnlyGroup(host).Toggle();
            host.Settle();

            await Assert.That(OnlyGroup(host).Calls.Select(i => i.Outcome))
                .IsEquivalentTo([ToolOutcome.Done, ToolOutcome.Error], CollectionOrdering.Matching);
            var pills = ToolRows(host.View)
                .Select(row => row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toolStatus") && b.IsVisible))
                .ToList();

            await Assert.That(pills).Count().IsEqualTo(2);
            await Assert.That(pills[0].Background).IsSameReferenceAs(Brush(isError: false));
            await Assert.That(pills[1].Background).IsSameReferenceAs(Brush(isError: true));
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
            ((ToolGroupItem)host.Chat.Items[0]).Toggle();
            host.Settle();
            var rows = ToolRows(host.View);
            var text = host.View.GetVisualDescendants().OfType<MarkdownView>().Single();
            double Top(Control c) => c.TranslatePoint(new Point(0, 0), host.View)!.Value.Y;
            double Bottom(Control c) => Top(c) + c.Bounds.Height;

            await Assert.That(rows).Count().IsEqualTo(2);
            await Assert.That(Top(rows[1]) - Bottom(rows[0])).IsLessThan(10);
            await Assert.That(Top(text) - Bottom(rows[1])).IsGreaterThanOrEqualTo(18);
            await host.CloseAsync();
        });
    }

    /// Pins the fold: settled calls become one summary line, live calls stay as rows, and a click
    /// on the summary reveals every call and hides them again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_folds_settled_calls_into_a_summary_and_expands_on_click() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("fold.jsonl", [ToolCallLine, ToolResultLine, ReadCallLine, ReadResultLine, CallLine(3)]));

            await Assert.That(host.Chat.Items).Count().IsEqualTo(1);
            var summary = Summary(host.View);
            await Assert.That(summary.IsVisible).IsTrue();
            await Assert.That(OnlyGroup(host).SummaryLine).IsEqualTo("Searched files, read a file · ls -la");
            await Assert.That(summary.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))
                .Contains("Searched files, read a file · ls -la");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await Assert.That(((ToolCallItem)ToolRows(host.View)[0].DataContext!).Outcome).IsEqualTo(ToolOutcome.Running);

            Click(host, summary);
            await Assert.That(OnlyGroup(host).IsExpanded).IsTrue();
            await Assert.That(OnlyGroup(host).SummaryLine).IsEqualTo("Searched files, read a file");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(3);

            Click(host, summary);
            await Assert.That(OnlyGroup(host).IsExpanded).IsFalse();
            await Assert.That(OnlyGroup(host).SummaryLine).IsEqualTo("Searched files, read a file · ls -la");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await host.CloseAsync();
        });
    }

    /// A lone settled call is the row itself — no "Ran a command" summary, but a kind chip names it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_single_settled_call_shows_the_row_without_a_summary() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("one.jsonl", [ToolCallLine, ToolResultLine]));
            await Assert.That(OnlyGroup(host).ShowsSummaryHeader).IsFalse();
            await Assert.That(OnlyGroup(host).ShowsKindChip).IsTrue();
            await Assert.That(OnlyGroup(host).KindChip).IsEqualTo("Search");
            await Assert.That(SummaryOrNull(host.View)?.IsVisible ?? false).IsFalse();
            var chip = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("toolKindChip") && t.IsEffectivelyVisible);
            await Assert.That(chip.Text).IsEqualTo("Search");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await Assert.That(((ToolCallItem)ToolRows(host.View)[0].DataContext!).Outcome).IsEqualTo(ToolOutcome.Done);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_lone_task_call_shows_a_task_kind_chip() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            const string taskCall = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t9","name":"Task","input":{"prompt":"Build the CLI project"}}]}}""";
            const string taskResult = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t9","content":"ok"}]}}""";
            await host.LoadAsync(Tmp.CreateFile("task.jsonl", [taskCall, taskResult]));
            await Assert.That(OnlyGroup(host).KindChip).IsEqualTo("Task");
            await Assert.That(OnlyGroup(host).ShowsKindChip).IsTrue();
            await host.CloseAsync();
        });
    }

    /// A group with nothing settled has no summary line at all.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_of_only_live_calls_shows_rows_and_no_summary() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("live.jsonl", [ToolCallLine, ReadCallLine]));
            await Assert.That(SummaryOrNull(host.View)?.IsVisible ?? false).IsFalse();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(2);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_call_inside_a_multi_call_group_marks_the_summary() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("fail.jsonl", [
                ToolCallLine, ToolErrorLine, ReadCallLine, ReadResultLine,
            ]));
            var summary = Summary(host.View);
            await Assert.That(summary.IsVisible).IsTrue();
            var failPill = summary.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("toolStatus") && b.IsVisible);
            await Assert.That(failPill.Background).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapDangerBrush"));
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_awaiting_row_paints_the_question_glyph_with_the_accent_brush() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("ask.jsonl", [ToolCallLine]));
            host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => OnlyGroup(host).Calls[0].IsAwaitingPermission, what: "the mark");
            host.Settle();
            var call = OnlyGroup(host).Calls[0];
            await Assert.That(call.IsRunning).IsFalse();
            await Assert.That(call.ShowRowStatus).IsFalse();
            var glyph = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.DataContext is ToolCallItem && t.Text == "?" && t.IsEffectivelyVisible);
            await Assert.That(glyph.Foreground).IsSameReferenceAs(Brush(isError: false));
            await Assert.That(host.View.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsEqualTo(0);
            await host.CloseAsync();
        });
    }

    /// A live row that is not waiting on permission shows the pulsing status pill.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_running_row_shows_the_pulsing_status_dot() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("run.jsonl", [ToolCallLine]));
            var call = OnlyGroup(host).Calls[0];
            await Assert.That(call.IsRunning).IsTrue();
            await Assert.That(call.HasDetail).IsTrue();
            await Assert.That(call.ShowRowStatus).IsFalse();
            var pulse = host.View.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible);
            await Assert.That(pulse.Background).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapWarningBrush"));
            var detail = ToolRows(host.View)[0].GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.IsEffectivelyVisible && t.Text == "ls -la");
            await Assert.That(detail.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapTextBrush"));
            await host.CloseAsync();
        });
    }

    /// The inner lists mutate without an outer collection notification; follow-tail still reads
    /// the extent change.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_tracks_inner_group_growth_and_folding_and_leaves_a_scrolled_up_reader_alone() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("inner.jsonl", [.. Enumerable.Repeat(UserLine, 60), CallLine(1)]);
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendLinesAndTickAsync(path, CallLine(2), CallLine(3));
            await Assert.That(host.Chat.Items).Count().IsEqualTo(61);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendLinesAndTickAsync(path, ResultLine(1));
            await Assert.That(host.AtBottom()).IsTrue();

            host.Scroll.Offset = new Vector(0, 0);
            host.Window.UpdateLayout();
            await host.AppendLinesAndTickAsync(path, CallLine(4), ResultLine(2));
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.CloseAsync();
        });
    }

    /// Expanding keeps the viewport: the hold is one-shot, so a reader who returns to the bottom is
    /// followed again on the next append.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Expanding_the_trailing_group_keeps_the_offset_and_the_hold_is_one_shot() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>(Enumerable.Repeat(UserLine, 60));
            for (var i = 1; i <= 30; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            var path = Tmp.CreateFile("expand.jsonl", lines.ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();
            var before = host.Scroll.Offset.Y;

            Click(host, Summary(host.View));
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(((ToolGroupItem)host.Chat.Items[^1]).IsExpanded).IsTrue();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(before);
            await Assert.That(host.AtBottom()).IsFalse();

            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 5);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// An append that lands in the same dispatcher turn as the click is held with the expansion:
    /// the reader stays put, and only a later append, after they return to the bottom, follows.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_append_queued_behind_the_expansion_click_does_not_steal_the_hold() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>(Enumerable.Repeat(UserLine, 60));
            for (var i = 1; i <= 30; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            var path = Tmp.CreateFile("race.jsonl", lines.ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();
            var before = host.Scroll.Offset.Y;

            var origin = PresentAndLocate(host, Summary(host.View));
            host.Window.MouseDown(origin, MouseButton.Left);
            host.Window.MouseUp(origin, MouseButton.Left);
            File.AppendAllLines(path, Enumerable.Repeat(UserLine, 5));
            host.Time.Advance(ChatTabViewModel.PollInterval);
            await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
            host.Settle();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            await Assert.That(((ToolGroupItem)host.Chat.Items[^6]).IsExpanded).IsTrue();
            await Assert.That(host.Chat.Items).Count().IsEqualTo(66);
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(before);

            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 5);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Expansion realizes every row; folding releases them again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thousand_call_group_realizes_on_expansion_and_releases_on_fold() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>();
            for (var i = 1; i <= 1000; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            lines.Add(CallLine(1001));
            await host.LoadAsync(Tmp.CreateFile("thousand.jsonl", lines.ToArray()));
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;

            await Assert.That(host.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(items.GetRealizedContainers().Count()).IsEqualTo(1);
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);

            OnlyGroup(host).Toggle();
            host.Settle();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1001);
            await Assert.That(items.GetRealizedContainers().Count()).IsEqualTo(1);

            OnlyGroup(host).Toggle();
            host.Settle();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
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
            var row = host.View.FindControl<Border>("NeedsYouRow")!;
            await Assert.That(row.IsVisible).IsFalse();

            host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Bash"));
            await WaitUntilAsync(() => host.Chat.PendingCards.Count == 1, what: "the card");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.IsVisible).IsTrue();
            var buttons = row.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString() ?? "").ToArray();
            await Assert.That(buttons).IsEquivalentTo(new[] { "Deny", "Allow always", "Allow" });

            host.Permissions.Remove("r1");
            await WaitUntilAsync(() => host.Chat.PendingCards.Count == 0, what: "cleared");
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
