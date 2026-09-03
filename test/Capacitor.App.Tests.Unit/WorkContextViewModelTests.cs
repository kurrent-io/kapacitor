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
            await Assert.That(h.Source.InFlight).IsEqualTo(1); // the switch already cancelled A; only B is parked

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

    static SessionWorkItemAssignmentDto Row(string id, string label, bool primary = true) =>
        new() { WorkItemId = id, Label = label, Source = "mcp", Confidence = 1, IsPrimary = primary };

    static WorkItemTopologyPartDto Part(string id, string title, int ordinal) =>
        new() { WorkItemId = id, Title = title, Ordinal = ordinal };

    static SessionPullRequestDto Pr(string owner, string repo, int number, string? url = null, string? title = null) =>
        new() { RepoHash = "h", Owner = owner, RepoName = repo, Number = number, Url = url, Title = title };

    static WorkContextRead ReadyWith(
            SessionWorkItemAssignmentDto? primary, WorkItemTopologyDto? topology = null, SessionSummaryDto? summary = null,
            bool topologyFailed = false, bool summaryFailed = false, IReadOnlyList<SessionWorkItemAssignmentDto>? assignments = null) =>
        new(WorkContextReadKind.Ready, assignments ?? (primary is null ? [] : [primary]), primary, topology,
            summary ?? new SessionSummaryDto { SessionId = SessionA }, topologyFailed, summaryFailed, null);

    static WorkItemTopologyDto Topology(params WorkItemTopologyPartDto[] parts) => new() {
        Parts = [.. parts],
        Item = new WorkItemRefDto { WorkItemId = "w1", Title = "Desktop shell: work-context sidebar" },
    };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_throwing_apply_still_stops_reading_and_lets_teardown_finish() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(ReadyWith(Row("w1", "AI-1 — t"), Topology(Part("p1", "First", 0)), assignments: [null!]));

            await h.PushAsync(Dto());

            await Assert.That(h.Vm.IsReading).IsFalse();
            await Assert.That(await h.Vm.RefreshCommand.CanExecute.FirstAsync()).IsTrue();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_card_shows_key_title_parts_marks_part_of_blockers_and_the_cycle_note() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var topology = Topology(Part("p2", "Second", 1), Part("p1", "First", 0)) with {
                PartOf = new WorkItemRefDto { WorkItemId = "w0", Title = "Parent epic" },
                BlockedBy = [new WorkItemRefDto { WorkItemId = "b1", Title = "Pin the helper" }],
                Cycle = "indeterminate",
            };
            h.Source.Enqueue(ReadyWith(Row("w1", "AI-2198 — old label"), topology,
                assignments: [Row("w1", "AI-2198 — old label"), Row("p1", "part", primary: false)]));

            await h.PushAsync(Dto());

            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Ready);
            await Assert.That(h.Vm.Key).IsEqualTo("AI-2198");
            await Assert.That(h.Vm.Title).IsEqualTo("Desktop shell: work-context sidebar");
            await Assert.That(h.Vm.PartOfTitle).IsEqualTo("Parent epic");
            await Assert.That(h.Vm.Parts.Select(p => p.Title)).IsEquivalentTo(new[] { "First", "Second" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(h.Vm.Parts[0].Mark).IsEqualTo(WorkContextPartMark.ThisSession);
            await Assert.That(h.Vm.Parts[1].Mark).IsEqualTo(WorkContextPartMark.Unknown);
            await Assert.That(h.Vm.PartsHeader).IsEqualTo("2 parts");
            await Assert.That(h.Vm.BlockedBy[0]).IsEqualTo("Pin the helper");
            await Assert.That(h.Vm.HasBlockers).IsTrue();
            await Assert.That(h.Vm.CycleNote).IsEqualTo("Dependencies could not be fully resolved");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_label_display_is_the_title_when_the_topology_has_no_item_and_a_cycle_note_needs_no_blockers() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(ReadyWith(Row("w1", "Daemon tests flake"), new WorkItemTopologyDto { Cycle = "cyclic" }));

            await h.PushAsync(Dto());

            await Assert.That(h.Vm.Key).IsNull();
            await Assert.That(h.Vm.Title).IsEqualTo("Daemon tests flake");
            await Assert.That(h.Vm.PartsHeader).IsEqualTo("0 parts");
            await Assert.That(h.Vm.HasParts).IsFalse();
            await Assert.That(h.Vm.HasBlockers).IsFalse();
            await Assert.That(h.Vm.CycleNote).IsEqualTo("Dependencies form a cycle");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_topology_blip_keeps_parts_for_the_same_primary_and_clears_them_for_a_new_one() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(
                ReadyWith(Row("w1", "AI-1 — t"), Topology(Part("p1", "First", 0))),
                ReadyWith(Row("w1", "AI-1 — t"), topology: null, topologyFailed: true),
                ReadyWith(Row("w9", "AI-9 — other"), topology: null, topologyFailed: true),
                ReadyWith(Row("w9", "AI-9 — other"), new WorkItemTopologyDto()));
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.Parts.Count).IsEqualTo(1);
            await Assert.That(h.Vm.IsStale).IsTrue();

            await h.TickAsync();
            await Assert.That(h.Vm.Key).IsEqualTo("AI-9");
            await Assert.That(h.Vm.Parts).IsEmpty();
            await Assert.That(h.Vm.IsStale).IsTrue();

            await h.TickAsync();
            await Assert.That(h.Vm.IsStale).IsFalse();
            await Assert.That(h.Vm.Parts).IsEmpty();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Links_come_from_the_list_with_the_top_level_triple_as_a_repository_aware_fallback() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var dup = new SessionSummaryDto {
                SessionId = SessionA, RepoOwner = "kurrent-io", RepoName = "kcap-cli", PrNumber = 42, PrUrl = "https://github.com/kurrent-io/kcap-cli/pull/42", PrTitle = "Top",
                PullRequests = [Pr("kurrent-io", "kcap-cli", 42, "https://github.com/kurrent-io/kcap-cli/pull/42", "Listed")],
            };
            var otherRepo = dup with { PullRequests = [Pr("kurrent-io", "kcap-server", 42, "https://github.com/kurrent-io/kcap-server/pull/42", "Server")] };
            var noIdentity = dup with { RepoOwner = null, RepoName = null, PullRequests = [Pr("x", "y", 42, null, "Elsewhere")] };
            h.Source.Enqueue(ReadyWith(null, summary: dup), ReadyWith(null, summary: otherRepo), ReadyWith(null, summary: noIdentity));

            await h.PushAsync(Dto());
            await Assert.That(h.Vm.Links.Select(l => l.Title)).IsEquivalentTo(new[] { "Listed" });
            await Assert.That(h.Vm.Links[0].Eyebrow).IsEqualTo("PULL REQUEST");
            await Assert.That(h.Vm.Links[0].Key).IsEqualTo("#42");

            await h.TickAsync();
            await Assert.That(h.Vm.Links.Select(l => l.Title)).IsEquivalentTo(new[] { "Server", "Top" });

            await h.TickAsync();
            await Assert.That(h.Vm.Links.Select(l => l.Title)).IsEquivalentTo(new[] { "Elsewhere" });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Links_open_through_the_policy_and_a_throwing_opener_is_caught() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var summary = new SessionSummaryDto {
                SessionId = SessionA,
                PullRequests = [Pr("o", "r", 1, "https://github.com/o/r/pull/1", "Https"), Pr("o", "r", 2, "file:///etc/passwd", "File"), Pr("o", "r", 3, "pull/3", "Relative"), Pr("o", "r", 4, null, "None")],
            };
            h.Source.Enqueue(ReadyWith(null, summary: summary));
            await h.PushAsync(Dto());

            await Assert.That(h.Vm.Links.Select(l => l.CanOpen)).IsEquivalentTo(new[] { true, false, false, false }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await h.Vm.Links[0].OpenCommand.Execute();
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://github.com/o/r/pull/1" });

            h.Opener.ThrowOnOpen = new InvalidOperationException("no browser");
            await h.Vm.Links[0].OpenCommand.Execute();
            await Assert.That(h.Opener.Opened.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_summary_blip_keeps_the_link_cards_and_an_empty_summary_clears_them() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var withPr = new SessionSummaryDto { SessionId = SessionA, PullRequests = [Pr("o", "r", 1, null, "One")] };
            h.Source.Enqueue(ReadyWith(null, summary: withPr), ReadyWith(null, summary: null, summaryFailed: true), ReadyWith(null));
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.Links.Count).IsEqualTo(1);
            await Assert.That(h.Vm.IsStale).IsTrue();
            await h.TickAsync();
            await Assert.That(h.Vm.Links).IsEmpty();
            await Assert.That(h.Vm.IsStale).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Terminal_phases_clear_every_server_projection_but_keep_the_facts_and_requester() {
        await RunOnUiAsync(async () => {
            foreach (var kind in new[] { WorkContextReadKind.SignedOut, WorkContextReadKind.NotInPlan, WorkContextReadKind.SessionUnknown }) {
                var h = new Harness();
                var summary = new SessionSummaryDto { SessionId = SessionA, PullRequests = [Pr("o", "r", 1, null, "One")] };
                h.Source.Enqueue(ReadyWith(Row("w1", "AI-1 — t"), Topology(Part("p1", "First", 0)), summary), WorkContextRead.Of(kind));
                await h.PushAsync(Dto());
                await h.TickAsync();

                await Assert.That(h.Vm.Key).IsNull();
                await Assert.That(h.Vm.Title).IsEqualTo("");
                await Assert.That(h.Vm.Parts).IsEmpty();
                await Assert.That(h.Vm.BlockedBy).IsEmpty();
                await Assert.That(h.Vm.CycleNote).IsNull();
                await Assert.That(h.Vm.Links).IsEmpty();
                await Assert.That(h.Vm.Repository).IsEqualTo("myproj");
                await Assert.That(h.Vm.Requester).IsEqualTo("You");
                await h.Vm.TeardownAsync();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_null_primary_clears_the_card_to_no_work_item_but_keeps_the_links() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var summary = new SessionSummaryDto { SessionId = SessionA, PullRequests = [Pr("o", "r", 1, null, "One")] };
            h.Source.Enqueue(ReadyWith(Row("w1", "AI-1 — t"), Topology(Part("p1", "First", 0)), summary), ReadyWith(null, summary: summary));
            await h.PushAsync(Dto());
            await h.TickAsync();

            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Vm.Parts).IsEmpty();
            await Assert.That(h.Vm.Key).IsNull();
            await Assert.That(h.Vm.Links.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_requester_row_prefers_the_display_name_then_the_id_then_you_and_skips_blanks() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.PushAsync(Dto(sessionId: null) with { RequesterDisplay = "  Ada Lovelace ", Requester = "github:1" });
            await Assert.That(h.Vm.Requester).IsEqualTo("Ada Lovelace");
            await Assert.That(h.Vm.RequesterInitial).IsEqualTo("A");
            await Assert.That(h.Vm.RequesterRole).IsEqualTo("This session · Claude Code");

            await h.PushAsync(Dto(sessionId: null) with { RequesterDisplay = "   ", Requester = "github:1" });
            await Assert.That(h.Vm.Requester).IsEqualTo("github:1");
            await Assert.That(h.Vm.RequesterInitial).IsEqualTo("G");

            await h.PushAsync(Dto(sessionId: null) with { RequesterDisplay = null, Requester = "" });
            await Assert.That(h.Vm.Requester).IsEqualTo("You");
            await Assert.That(h.Vm.RequesterInitial).IsEqualTo("Y");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_requester_initial_keeps_a_surrogate_pair_whole() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.PushAsync(Dto(sessionId: null) with { RequesterDisplay = "👩 Ada", Requester = "github:1" });
            await Assert.That(h.Vm.RequesterInitial).IsEqualTo("👩");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sections_default_open_parts_and_collapsed_people_and_session_and_toggle() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.PartsExpanded).IsTrue();
            await Assert.That(h.Vm.PeopleExpanded).IsFalse();
            await Assert.That(h.Vm.SessionExpanded).IsFalse();

            await h.Vm.TogglePartsCommand.Execute();
            await h.Vm.TogglePeopleCommand.Execute();
            await h.Vm.ToggleSessionCommand.Execute();

            await Assert.That(h.Vm.PartsExpanded).IsFalse();
            await Assert.That(h.Vm.PeopleExpanded).IsTrue();
            await Assert.That(h.Vm.SessionExpanded).IsTrue();
            await h.Vm.TeardownAsync();
        });
    }
}
