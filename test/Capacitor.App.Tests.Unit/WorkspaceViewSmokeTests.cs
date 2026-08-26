using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
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
            string agentId = AgentId) {
        var daemon = new FakeDaemonClientService();
        var attach = new FakeTerminalAttachClientFactory();
        var vm = new WorkspaceViewModel(
            agentId, daemon, NewActions(), attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener());
        return (new WorkspaceView { DataContext = vm }, vm, daemon, attach);
    }

    static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// Run-and-observe: hosts the view with a plainly-Resolving VM (no dto ever pushed) and looks
    /// every named control up by x:Name. Every control is statically declared in the XAML (no
    /// DataTemplate/ItemsControl realization involved, unlike HomeView's session cards), so this
    /// is not red-verifiable the way a missing-feature test would be -- the view already exists and
    /// already carries all eight names; there is no "before" state in which the assertion could
    /// fail short of a typo. TerminalTabButton is a plain Button in the XAML with no Command bound
    /// (non-interactive today), so it is resolved as a bare Control like every other name here
    /// rather than assumed clickable.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task WorkspaceView_resolves_all_eight_named_controls() {
        await RunOnUiAsync(async () => {
            var (view, vm, _, _) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var names = new[] {
                "WorkspaceTitle", "WorkspaceRepo", "WorkspaceVendorChip", "TerminalTabButton",
                "NoTerminalNote", "TerminalHost", "DetachButton", "ReattachButton",
            };
            foreach (var name in names)
                await Assert.That(Find<Control>(window, name)).IsNotNull().Because($"{name} should resolve");

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Run-and-observe: drives ONE workspace through both has_terminal values for the same agent id
    /// and asserts the tab/note pair actually flips, not just that one arrangement renders
    /// correctly. TerminalTabButton and NoTerminalNote both bind IsVisible directly to
    /// WorkspaceViewModel.ShowsTerminalTab (never negated the same way twice, see the XAML's `!`
    /// prefix on the note), so a bare `.IsVisible` read is enough -- neither sits behind an
    /// invisible ancestor.
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
            await Assert.That(tabButton.IsVisible).IsFalse();
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
            await Assert.That(tabButton.IsVisible).IsTrue();
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

    /// Owner decision after manual QA: a NORMAL read-write attach shows NO banner — it overlaid
    /// the terminal content. Explicit detach returns when a use case earns it; read-only keeps
    /// the banner because it is the only explanation for dead keystrokes.
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
}
