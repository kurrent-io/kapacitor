using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
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

    sealed class Host {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }
        public ChatTabView View { get; }
        public Window Window { get; }
        public ScrollViewer Scroll => View.GetVisualDescendants().OfType<ScrollViewer>().First();

        public Host() {
            Terminal = new TerminalTabViewModel("a1", Daemon, new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel("a1", Daemon, Terminal, TranscriptProjection.For("claude"), new RecordingOpener(), Time);
            View = new ChatTabView { DataContext = Chat };
            Window = new Window { Content = View, Width = 800, Height = 600 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public async Task LoadAsync(string path) {
            Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path });
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
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
}
