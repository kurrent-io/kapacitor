using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The sidebar VM: facts from the dto, the session-id lease that owns each read, the poll, and
/// teardown. Every read settles through Dispatcher.UIThread, so every test runs under RunOnUiAsync
/// and carries [NotInParallel("AvaloniaSession")].
public class WorkContextViewModelTests {
    const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    sealed class Harness {
        public BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        public FakeWorkContextSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public Subject<ReactiveUnit> SignIn { get; } = new();
        public int SignInRequests;
        public WorkContextViewModel Vm { get; }

        public Harness() =>
            Vm = new WorkContextViewModel(Presence, Source, Time, Opener, () => SignInRequests++, SignIn);

        /// For a read that will answer from the queue: pushes and awaits the read it starts.
        public async Task PushAsync(AgentStatusDto dto) {
            Presence.OnNext(dto);
            await (Vm.PendingReadForTesting ?? Task.CompletedTask);
        }

        /// For a read that will park on a gate: pushes and returns, since the read cannot settle
        /// until the test releases the gate.
        public void Push(AgentStatusDto dto) => Presence.OnNext(dto);

        public async Task TickAsync() {
            Time.Advance(WorkContextViewModel.PollInterval);
            await (Vm.PendingReadForTesting ?? Task.CompletedTask);
        }
    }

    static AgentStatusDto Dto(string? sessionId = SessionA, string? repoPath = "/repo/myproj", string? branch = "feature/x") =>
        Agent("a1", "claude", hasTerminal: true, repoPath: repoPath, model: "claude-opus-5",
            worktreePath: "/repo/myproj/.capacitor/worktrees/agent-1", workLocation: "owned",
            sessionId: sessionId, branch: branch);

    static WorkContextRead Ready() => WorkContextRead.Of(WorkContextReadKind.Ready) with {
        Summary = new SessionSummaryDto { SessionId = SessionA },
    };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Facts_derive_from_the_dto_and_the_id_reads_resolving_until_reported() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.WaitingForSession);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.WaitingNote);
            await Assert.That(h.Vm.SessionIdText).IsEqualTo("resolving…");

            await h.PushAsync(Dto(sessionId: null));

            await Assert.That(h.Vm.Repository).IsEqualTo("myproj");
            await Assert.That(h.Vm.RepositoryPath).IsEqualTo("/repo/myproj");
            await Assert.That(h.Vm.Worktree).IsEqualTo("agent-1");
            await Assert.That(h.Vm.WorktreePath).IsEqualTo("/repo/myproj/.capacitor/worktrees/agent-1");
            await Assert.That(h.Vm.Branch).IsEqualTo("feature/x");
            await Assert.That(h.Vm.Harness).IsEqualTo("Claude Code · Claude Opus 5");
            await Assert.That(h.Vm.Transport).IsEqualTo("PTY");
            await Assert.That(h.Vm.SessionSummaryLine).IsEqualTo("Claude Code · Claude Opus 5 · PTY");
            await Assert.That(h.Vm.SessionIdText).IsEqualTo("resolving…");
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.WaitingForSession);
            await Assert.That(h.Source.Requested).IsEmpty();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_borrowed_launch_without_a_branch_shows_a_dash_and_the_borrowed_marker() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var dto = Agent("r1", "codex", hasTerminal: true, repoPath: "/repo/myproj", kind: "review",
                worktreePath: "/repo/myproj", workLocation: "borrowed", borrowedFrom: "/repo/myproj", branch: null, sessionId: null);

            await h.PushAsync(dto);

            await Assert.That(h.Vm.Branch).IsEqualTo("—");
            await Assert.That(h.Vm.Worktree).IsEqualTo("main checkout · borrowed");
            await Assert.That(h.Vm.Transport).IsEqualTo("PTY");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Transport_follows_the_effective_family() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.PushAsync(Agent("c1", "cursor", hasTerminal: false, repoPath: "/repo/x", sessionId: null));
            await Assert.That(h.Vm.Transport).IsEqualTo("ACP");
            await h.PushAsync(Agent("c1", "claude", hasTerminal: false, repoPath: "/repo/x", sessionId: null));
            await Assert.That(h.Vm.Transport).IsEqualTo("chat");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_first_session_id_reads_at_once_with_the_id_as_reported() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());

            await h.PushAsync(Dto());

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA });
            await Assert.That(h.Vm.HasSession).IsTrue();
            await Assert.That(h.Vm.SessionIdText).IsEqualTo(SessionA);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Each_read_kind_maps_to_its_phase() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.Gate();
            h.Push(Dto());
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.LoadingNote);
            gate.SetResult(WorkContextRead.Of(WorkContextReadKind.SessionUnknown));
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.SessionUnknown);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.WaitingNote);

            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.SignedOut);
            await Assert.That(h.Vm.ShowsSignIn).IsTrue();

            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.NotInPlan, "Upgrade."));
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NotInPlan);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.NotInPlanNote);

            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.Unreachable, "no response"));
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Unreachable);
            await Assert.That(h.Vm.ShowsRetry).IsTrue();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_work_item_on_a_repo_less_session_shows_the_no_repository_copy() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto(repoPath: null));
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.NoRepositoryNote);

            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.NoWorkItemNote);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_timer_re_reads_and_skips_a_tick_or_a_refresh_while_a_read_is_in_flight() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto());
            var gate = h.Source.Gate();
            h.Time.Advance(WorkContextViewModel.PollInterval); // the read parks on the gate, so TickAsync's await would never return
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await Assert.That(h.Vm.IsReading).IsTrue();
            await Assert.That(await h.Vm.RefreshCommand.CanExecute.FirstAsync()).IsFalse();

            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);

            gate.SetResult(Ready());
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.IsReading).IsFalse();
            await Assert.That(await h.Vm.RefreshCommand.CanExecute.FirstAsync()).IsTrue();
            h.Source.Enqueue(Ready());
            await h.Vm.RefreshCommand.Execute();
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreachable_refresh_after_ready_keeps_the_phase_and_marks_it_stale() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(), WorkContextRead.Of(WorkContextReadKind.Unreachable), Ready());
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Vm.IsStale).IsTrue();
            await h.TickAsync();
            await Assert.That(h.Vm.IsStale).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_id_switch_drops_the_old_read_and_reads_the_new_id_at_once() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionB));

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA, SessionB });
            await Assert.That(h.Vm.SessionIdText).IsEqualTo(SessionB);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
            await Assert.That(h.Vm.IsReading).IsTrue();

            gateA.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await Task.Yield();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
            await Assert.That(h.Vm.IsReading).IsTrue();
            await Assert.That(await h.Vm.RefreshCommand.CanExecute.FirstAsync()).IsFalse();
            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);

            gateB.SetResult(Ready());
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rapid_switch_applies_only_the_last_id() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            h.Push(Dto(sessionId: SessionB));
            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.NotInPlan));
            await h.PushAsync(Dto(sessionId: "cccccccccccccccccccccccccccccccc"));
            gateA.SetResult(Ready());
            gateB.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.Vm.TeardownAsync();

            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NotInPlan);
            await Assert.That(h.Vm.SessionIdText).IsEqualTo("cccccccccccccccccccccccccccccccc");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_id_going_back_to_null_changes_nothing() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto());
            await h.PushAsync(Dto(sessionId: null));

            await Assert.That(h.Vm.SessionIdText).IsEqualTo(SessionA);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sign_in_reads_at_once_when_idle_and_is_coalesced_into_the_next_read_otherwise() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.PushAsync(Dto());
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.SignedOut);
            await h.Vm.SignInCommand.Execute();
            await Assert.That(h.SignInRequests).IsEqualTo(1);

            h.Source.Enqueue(Ready());
            h.SignIn.OnNext(ReactiveUnit.Default);
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);

            var gate = h.Source.Gate();
            h.Time.Advance(WorkContextViewModel.PollInterval); // parked on the gate; do not await it
            h.SignIn.OnNext(ReactiveUnit.Default);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            h.Source.Enqueue(Ready());
            gate.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.Vm.PendingReadForTesting!;
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Source.Requested.Count).IsEqualTo(4);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_sign_in_refresh_is_discarded_when_its_lease_was_superseded() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            h.SignIn.OnNext(ReactiveUnit.Default);
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionB));
            gateA.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await Task.Yield();

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA, SessionB });
            gateB.SetResult(Ready());
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_cancels_and_awaits_every_outstanding_read_and_ignores_later_signals() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionB));
            await Assert.That(h.Source.InFlight).IsEqualTo(2);

            var teardown = h.Vm.TeardownAsync();
            await teardown;

            await Assert.That(h.Source.InFlight).IsEqualTo(0);
            gateA.TrySetResult(Ready());
            gateB.TrySetResult(Ready());
            h.SignIn.OnNext(ReactiveUnit.Default);
            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
        });
    }
}
