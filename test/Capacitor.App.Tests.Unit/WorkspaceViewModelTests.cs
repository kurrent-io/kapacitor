using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// WorkspaceViewModel's header projections (Title/RepoLabelText/VendorChip/FamilyDot/
/// ShowsTerminalTab/NoTerminalNote), SessionEnded, and Stop routing. WorkspaceViewModel always
/// builds a real TerminalTabViewModel internally, so pushing a matching AgentStatusDto into the
/// shared daemon.Agents cache also drives Terminal's OWN resolve gate -- which reaches
/// Dispatcher.UIThread.InvokeAsync regardless of hasTerminal (both the NoTerminal and the attach
/// branches dispatch). Every test therefore runs through the same RunOnUiAsync nesting
/// TerminalTabViewModelTests uses (DispatchAsync for a live pumped dispatcher, WithImmediateRxScheduler
/// so ObserveOn(RxSchedulers.MainThreadScheduler) applies synchronously) and carries
/// [NotInParallel("AvaloniaSession")] -- see that class's identical header comment.
public class WorkspaceViewModelTests {
    sealed class FakeTerminalSurface : ITerminalSurface {
        public void Feed(string text) { }
        public event Action<byte[]>? InputProduced;
        public event Action<int, int>? Resized;
        public void RaiseInput(byte[] bytes) => InputProduced?.Invoke(bytes);
        public void RaiseResize(int cols, int rows) => Resized?.Invoke(cols, rows);
        public (int Cols, int Rows) CurrentSize { get; set; } = (80, 24);
        public int CaretShown;
        public void EnsureCaretVisible() => CaretShown++;
    }

    static AgentStatusDto Agent(
            string id, string vendor, bool? hasTerminal, string? repoPath = null,
            string kind = "agent", string? model = null) => new(
        id, kind, vendor, repoPath, "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: model,
        RequesterDisplay: null, HasTerminal: hasTerminal);

    static WorkspaceViewModel Build(
            FakeDaemonClientService daemon, AgentActionService actions, FakeTerminalAttachClientFactory factory,
            FakeTimeProvider time, string agentId = "a1") =>
        new(agentId, daemon, actions, factory.Factory, () => new FakeTerminalSurface(), time);

    static AgentActionService NewActions(
            ScriptedLocalControlOps ops, RecordingNotifier notifier, RecordingOpener opener,
            Func<string, Task<bool>>? confirmForceStop = null) =>
        new(ops, notifier, opener, new ReplaySubject<DaemonStatusDto>(1), CancellationToken.None,
            confirmForceStop ?? NeverConfirm.Confirm);

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    // See the class doc comment: WithImmediateRxScheduler alone never pumps Dispatcher.UIThread,
    // which the internal TerminalTabViewModel's resolve gate reaches on every dto push.
    static Task RunOnUiAsync(Func<Task> body) =>
        AvaloniaSession.DispatchAsync(async () => {
            await AvaloniaSession.WithImmediateRxScheduler(body);
            return true;
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Title_repo_and_vendor_chip_project_from_the_pushed_dto() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var actions = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, actions, factory, new FakeTimeProvider(), agentId: "a1");

            // Before any dto: placeholders, never a crash on the null-dto default path.
            await Assert.That(vm.RepoLabelText).IsEqualTo("—");
            await Assert.That(vm.ShowsTerminalTab).IsFalse();

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", model: "sonnet"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(vm.Title).IsEqualTo("myproj · claude");
            await Assert.That(vm.RepoLabelText).IsEqualTo("myproj");
            await Assert.That(vm.VendorChip).IsEqualTo("claude (sonnet)");
            await Assert.That(vm.FamilyDot).IsEqualTo("pty");
            await Assert.That(vm.ShowsTerminalTab).IsTrue();
            await Assert.That(vm.NoTerminalNote).IsEqualTo("");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Has_terminal_false_hides_the_tab_and_sets_the_family_aware_note() {
        await RunOnUiAsync(async () => {
            // Case 1: an ACP vendor (gemini) -- note says "runs over ACP", never the vendor name.
            var daemon1 = new FakeDaemonClientService();
            var actions1 = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var factory1 = new FakeTerminalAttachClientFactory();
            var vm1 = Build(daemon1, actions1, factory1, new FakeTimeProvider(), agentId: "a1");

            daemon1.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: false));
            await (vm1.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(vm1.ShowsTerminalTab).IsFalse();
            await Assert.That(vm1.NoTerminalNote).Contains("runs over ACP");
            await Assert.That(vm1.NoTerminalNote).DoesNotContain("Gemini");

            // Case 2: a non-ACP vendor (pi maps to rpc) -- bare note, no family token leaked.
            var daemon2 = new FakeDaemonClientService();
            var actions2 = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var factory2 = new FakeTerminalAttachClientFactory();
            var vm2 = Build(daemon2, actions2, factory2, new FakeTimeProvider(), agentId: "a2");

            daemon2.Agents.AddOrUpdate(Agent("a2", "pi", hasTerminal: false));
            await (vm2.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);

            await Assert.That(vm2.ShowsTerminalTab).IsFalse();
            await Assert.That(vm2.NoTerminalNote).IsEqualTo("This session has no terminal.");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SessionEnded_flips_on_cache_removal_and_the_header_stays_frozen() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var actions = NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener());
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, actions, factory, new FakeTimeProvider(), agentId: "a1");

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await Assert.That(vm.SessionEnded).IsFalse();

            daemon.Agents.Remove("a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            // The header keeps identifying the session that just ended rather than reverting to
            // "— · —" -- a frozen last-known snapshot, not a blanked one.
            await Assert.That(vm.Title).IsEqualTo("myproj · claude");
            await Assert.That(vm.ShowsTerminalTab).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Stop_routes_through_agent_action_service_with_the_dtos_kind() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var ops = new ScriptedLocalControlOps();
            var notifier = new RecordingNotifier();
            var confirmer = new RecordingConfirmer();
            var actions = NewActions(ops, notifier, new RecordingOpener(), confirmForceStop: confirmer.Confirm);
            var factory = new FakeTerminalAttachClientFactory();
            var vm = Build(daemon, actions, factory, new FakeTimeProvider(), agentId: "a1");

            // "review" is a PROTECTED kind (AgentActionService.IsProtectedKind: anything but
            // exactly "agent"). Only reaching the confirm-then-force seam proves StopCommand read
            // the DTO's OWN kind rather than some fixed default -- StopPayloads alone can't show
            // this, since the wire call never carries kind, only the force bool the confirm
            // decision produces.
            daemon.Agents.AddOrUpdate(Agent("a1", "codex", hasTerminal: true, kind: "review"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);

            confirmer.Queue(true);
            ops.QueueStop(new StopAgentResult(true, "stopped", null));
            await vm.StopCommand.Execute();

            await WaitUntilAsync(() => ops.StopCalls >= 1, what: "stop issued after confirm");
            await Assert.That(confirmer.Prompted.Count).IsEqualTo(1);
            await Assert.That(ops.StopPayloads).IsEquivalentTo([("a1", true)], CollectionOrdering.Matching);
        });
    }
}
