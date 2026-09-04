using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using SvcSystems.UI.Terminal;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the session workspace: WorkspaceView is a UserControl (like
/// HomeView), so each test hosts it inside a plain Window purely to give headless something to
/// Show() -- see HomeViewSmokeTests' identical header comment. Unlike HomeView, this VIEW is
/// normally handed its DataContext through MainWindow's ContentControl/DataTemplate swap
/// (WorkspaceNavigationTests exercises that path); a smoke test instead sets DataContext directly,
/// bypassing the template so the view under test is exactly WorkspaceView, not MainWindow's swap
/// machinery.
///
/// WorkspaceViewModel always builds a real TerminalTabViewModel internally, which reaches
/// Dispatcher.UIThread.InvokeAsync on every daemon-cache dto push regardless of has_terminal (both
/// the NoTerminal and the attach branches dispatch) -- so every test here runs through the same
/// RunOnUiAsync nesting WorkspaceViewModelTests/WorkspaceNavigationTests use (DispatchAsync for a
/// live pumped dispatcher, WithImmediateRxScheduler so ObserveOn(RxSchedulers.MainThreadScheduler)
/// applies synchronously) and carries [NotInParallel("AvaloniaSession")].
public class WorkspaceViewSmokeTests {
    const string AgentId = "0123456789abcdef0123456789abcdef";

    static AgentStatusDto Agent(string id, bool? hasTerminal, string vendor = "claude") =>
        WorkspaceFixtures.Agent(id, vendor, hasTerminal, "/repo/myproj");

    static (WorkspaceView View, WorkspaceViewModel Vm, FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Attach) Build(
            string agentId = AgentId, Func<ITerminalSurface>? surface = null) {
        var daemon = new FakeDaemonClientService();
        var attach = new FakeTerminalAttachClientFactory();
        var vm = new WorkspaceViewModel(
            agentId, daemon, NewActions(), attach.Factory, surface ?? (() => new FakeTerminalSurface()),
            new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource());
        return (new WorkspaceView { DataContext = vm }, vm, daemon, attach);
    }

    static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// A shown workspace on a PTY session, laid out at a real pane size — the shape every
    /// tab/focus test below starts from.
    static async Task<(Window Window, WorkspaceViewModel Vm, FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Attach)> ShowPtyAsync(
            Func<ITerminalSurface>? surface = null) {
        var (view, vm, daemon, attach) = Build(surface: surface);
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
        await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, vm, daemon, attach);
    }

    /// Effectively visible under this window. A name that never gets realized into the visual
    /// tree — the collapsed chat surface's own controls — reads as not visible, which is exactly
    /// what the gate promises.
    static bool Visible(Window window, string name) => Find<Control>(window, name) is { IsEffectivelyVisible: true };

    static bool IsOffscreen(Control control) =>
        Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control).IsOffscreen();

    /// Everything a real Tab from `start` reaches, in order, until it comes back round. Avalonia's
    /// own navigation handler is internal, so the ring is walked by pressing the key.
    static List<IInputElement> TabRing(Window window, Control start) {
        start.Focus();
        Dispatcher.UIThread.RunJobs();
        var ring = new List<IInputElement>();
        var seen = new HashSet<IInputElement>();
        while (window.FocusManager?.GetFocusedElement() is { } current && seen.Add(current)) {
            ring.Add(current);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }
        return ring;
    }

    /// Pins that every x:Name the view's code-behind and the suite reach for resolves before any
    /// dto arrives — the tab strip included, which is collapsed in this state and must still be
    /// in the tree for the code-behind to find. The chat surface is collapsed too, and a
    /// collapsed UserControl is never measured, so its own names resolve through its name scope
    /// rather than the window's visual tree.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task WorkspaceView_resolves_all_named_controls() {
        await RunOnUiAsync(async () => {
            var (view, vm, _, _) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var names = new[] {
                "WorkspaceTitle", "WorkspaceRepo", "WorkspaceVendorChip", "ChatTabButton",
                "TerminalTabButton", "NoTerminalNote", "TerminalHost", "TerminalBanners",
                "DetachButton", "ReattachButton", "SessionEndedNote", "ChatHost", "WorkContextHost",
            };
            foreach (var name in names)
                await Assert.That(Find<Control>(window, name)).IsNotNull().Because($"{name} should resolve");

            var chatHost = Find<ChatTabView>(window, "ChatHost")!;
            foreach (var name in new[] { "ChatItems", "ChatPhaseNote", "ComposerInput", "SendButton" })
                await Assert.That(chatHost.FindControl<Control>(name)).IsNotNull().Because($"{name} should resolve");

            var pane = Find<WorkContextView>(window, "WorkContextHost")!;
            foreach (var name in new[] {
                "RefreshButton", "StaleDot", "WorkContextKey", "WorkContextTitle", "PartOfLine", "PartsToggle", "PartsList",
                "BlockedByBlock", "CycleNoteText", "PhaseNoteText", "SignInButton", "RetryButton", "LinkCards", "IssueSoonCard",
                "WhoToggle", "SessionToggle", "SessionSummaryText", "SessionFacts",
            })
                await Assert.That(pane.FindControl<Control>(name)).IsNotNull().Because($"{name} should resolve");

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// The pane takes its fixed 400 and the terminal the rest, so the PTY size the terminal
    /// reports is the real center-pane width.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_is_400_wide_and_the_terminal_takes_the_remainder() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();

            var pane = Find<WorkContextView>(window, "WorkContextHost")!;
            var terminal = Find<TerminalControl>(window, "TerminalHost")!;
            await Assert.That(pane.Bounds.Width).IsEqualTo(400);
            await Assert.That(terminal.Bounds.Width).IsEqualTo(window.Bounds.Width - 400);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Run-and-observe: drives ONE workspace through both has_terminal values for the same agent id
    /// and asserts the tab/note pair actually flips, not just that one arrangement renders
    /// correctly. The tab buttons share one IsVisible-bound strip, so the button is read through
    /// IsEffectivelyVisible while the note, which binds the negation itself, is read directly.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tab_and_note_visibility_flip_with_ShowsTerminalTab() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, _) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tabButton = Find<Control>(window, "TerminalTabButton")!;
            var note = Find<Control>(window, "NoTerminalNote")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: false));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.ShowsTerminalTab).IsFalse();
            await Assert.That(tabButton.IsEffectivelyVisible).IsFalse();
            await Assert.That(note.IsVisible).IsTrue();
            await Assert.That(terminalHost.IsVisible).IsFalse();
            await Assert.That(vm.NoTerminalNote).IsNotEmpty();

            // Same agent id, has_terminal flips to true: WorkspaceViewModel's ShowsTerminalTab/
            // NoTerminalNote are plain Rx projections off the daemon cache (not gated by
            // TerminalTabViewModel's own one-shot resolve CAS), so a later update still moves them.
            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.ShowsTerminalTab).IsTrue();
            await Assert.That(tabButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(note.IsVisible).IsFalse();
            await Assert.That(terminalHost.IsVisible).IsTrue();
            await Assert.That(vm.NoTerminalNote).IsEmpty();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Run-and-observe: drives the fake attach client's Result straight to AttachOutcome.Detached
    /// (TerminalTabViewModelTests' own idiom) and checks the view actually renders the combined
    /// Detached/Failed banner -- ReattachButton sits inside a Border whose OWN IsVisible is bound
    /// to the phase, so IsEffectivelyVisible (not IsVisible) is required to see the ancestor's
    /// collapse, same as MainWindowSmokeTests' shell-vs-workspace check.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Detached_state_shows_the_reattach_banner() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, attach) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var reattachButton = Find<Control>(window, "ReattachButton")!;
            var detachButton = Find<Control>(window, "DetachButton")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            await Assert.That(reattachButton.IsEffectivelyVisible).IsFalse();

            var client = attach.Created[^1];
            client.Result.SetResult(new AttachOutcome.Detached());
            await vm.Terminal.CurrentRunForTesting!;
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.Detached);
            await Assert.That(reattachButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(detachButton.IsEffectivelyVisible).IsFalse();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins that a normal read-write attach shows NO banner — one would overlay the terminal
    /// content. Read-only keeps its banner because it is the only explanation for dead
    /// keystrokes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Read_write_attached_state_shows_no_banner() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, attach) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var detachButton = Find<Control>(window, "DetachButton")!;
            var bannerText = Find<TextBlock>(window, "AttachBannerText")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            var client = attach.Created[^1];
            await client.TriggerAttached([], reason: null);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.Terminal.State.ReadOnly).IsFalse();
            await Assert.That(detachButton.IsEffectivelyVisible).IsFalse();
            await Assert.That(bannerText.IsEffectivelyVisible).IsFalse();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Companion to the read-write test above: a read-only attach (TriggerAttached with a reason)
    /// is the ONE mode that shows the banner — warning copy with the daemon's reason, plus the
    /// Detach button (the only action a read-only session has).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Read_only_attached_state_shows_the_warning_banner_and_detach_button() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, attach) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var detachButton = Find<Control>(window, "DetachButton")!;
            var bannerText = Find<TextBlock>(window, "AttachBannerText")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            var client = attach.Created[^1];
            await client.TriggerAttached([], reason: "review");
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.Terminal.State.ReadOnly).IsTrue();
            await Assert.That(detachButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(bannerText.Text).IsEqualTo("Read-only: review");

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins the tab swap: Chat is the surface a PTY session opens on, and the terminal stays in
    /// the tree behind it — inert and reported offscreen, never unloaded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_opens_first_and_the_tabs_swap_the_surfaces_while_the_terminal_stays_in_the_tree() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            var chatHost = Find<Control>(window, "ChatHost")!;
            var banners = Find<Control>(window, "TerminalBanners")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            await Assert.That(vm.IsChatActive).IsTrue();
            await Assert.That(chatHost.IsEffectivelyVisible).IsTrue();
            await Assert.That(banners.IsEffectivelyVisible).IsFalse();
            await Assert.That(IsOffscreen(banners)).IsTrue();
            await Assert.That(terminalHost.IsEnabled).IsFalse();
            await Assert.That(terminalHost.IsHitTestVisible).IsFalse();
            await Assert.That(terminalHost.IsVisible).IsTrue();
            await Assert.That(terminalHost.Opacity).IsEqualTo(0.0);
            await Assert.That(IsOffscreen(terminalHost)).IsTrue();

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(chatHost.IsEffectivelyVisible).IsFalse();
            await Assert.That(IsOffscreen(chatHost)).IsTrue();
            await Assert.That(terminalHost.IsEnabled).IsTrue();
            await Assert.That(terminalHost.Opacity).IsEqualTo(1.0);
            await Assert.That(IsOffscreen(terminalHost)).IsFalse();
            await Assert.That(Find<Control>(window, "TerminalHost")).IsNotNull();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins the one gate the chat surface hangs off: a session with no PTY renders no chat at
    /// all — not the host, not the composer, not Send — and keeps the banner layer it has no
    /// Terminal tab to reach, so its end is still announced.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_without_a_terminal_shows_no_chat_surface_and_still_banners_its_end() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, _) = Build();
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: false));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var chatHost = Find<ChatTabView>(window, "ChatHost")!;
            await Assert.That(vm.IsChatActive).IsTrue();
            await Assert.That(chatHost.IsEffectivelyVisible).IsFalse();
            await Assert.That(Visible(window, "ComposerInput")).IsFalse();
            await Assert.That(Visible(window, "SendButton")).IsFalse();
            await Assert.That(chatHost.FindControl<TextBox>("ComposerInput")!.IsFocused).IsFalse();
            await Assert.That(Find<Control>(window, "NoTerminalNote")!.IsVisible).IsTrue();

            daemon.Agents.Remove(AgentId);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
            await Assert.That(Find<Control>(window, "SessionEndedNote")!.IsEffectivelyVisible).IsTrue();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins why the off-tab terminal is faded rather than collapsed: the PTY is sized from the
    /// laid-out pane, so opening on Chat must not hand the daemon the surface's ctor default.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_workspace_opened_on_chat_still_reports_the_laid_out_pane_size() {
        await RunOnUiAsync(async () => {
            XtermTerminalSurface? surface = null;
            var (window, vm, _, attach) = await ShowPtyAsync(surface: () => surface = new XtermTerminalSurface(80, 24));
            var client = attach.Created[^1];

            await Assert.That((client.Cols, client.Rows)).IsNotEqualTo((80, 24));
            await Assert.That((client.Cols, client.Rows)).IsEqualTo(surface!.CurrentSize);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins that focus follows the active tab, and that a terminal going live under the Chat tab
    /// does not steal the composer's focus. A Model assignment is that "went live" moment, so the
    /// test performs one rather than waiting for a reattach to produce it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Focus_follows_the_tab_and_survives_a_late_model_assignment() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            var composer = Find<TextBox>(window, "ComposerInput")!;
            var terminalHost = Find<TerminalControl>(window, "TerminalHost")!;
            await Assert.That(composer.IsFocused).IsTrue();

            terminalHost.Model = new XtermTerminalSurface(80, 24).Model;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(terminalHost.IsFocused).IsFalse();
            await Assert.That(composer.IsFocused).IsTrue();

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(terminalHost.IsFocused).IsTrue();

            await vm.ShowChatCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(composer.IsFocused).IsTrue();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins that the inactive surface is out of the keyboard's reach in both directions — a Tab
    /// from either tab's own controls never lands on the other's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tab_traversal_never_reaches_the_inactive_surface() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, attach) = await ShowPtyAsync();
            attach.Created[^1].Result.SetResult(new AttachOutcome.Detached());
            await vm.Terminal.CurrentRunForTesting!;
            Dispatcher.UIThread.RunJobs();

            var composer = Find<TextBox>(window, "ComposerInput")!;
            var detach = Find<Control>(window, "DetachButton")!;
            var reattach = Find<Control>(window, "ReattachButton")!;
            var send = Find<Control>(window, "SendButton")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            var ringFromComposer = TabRing(window, composer);
            await Assert.That(ringFromComposer).Contains(composer);
            await Assert.That(ringFromComposer).DoesNotContain(detach);
            await Assert.That(ringFromComposer).DoesNotContain(reattach);

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var ringFromTerminal = TabRing(window, terminalHost);
            await Assert.That(ringFromTerminal).Contains(terminalHost);
            await Assert.That(ringFromTerminal).DoesNotContain(composer);
            await Assert.That(ringFromTerminal).DoesNotContain(send);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }
}
