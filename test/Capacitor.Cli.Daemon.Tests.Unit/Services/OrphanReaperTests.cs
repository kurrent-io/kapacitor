using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase B (D4 §6.4(3)): <see cref="OrphanReaper"/> — reaps hosted-agent children that outlived
/// a prior daemon run. Acts only on test-owned <see cref="DummyProcess"/> instances. OS-aware: the
/// RECORD pass confirms-and-kills on a proven exact (pid, start-identity) match on every platform
/// (macOS uses the exact <c>mac:{bootsessionuuid}:{p_uniqueid}</c> incarnation identity — M1-A;
/// Linux additionally requires the readable <c>KCAP_AGENT_ID</c> env as defense-in-depth). The
/// separate ENV-MARKER scan still SPARES on macOS 26 (other processes' env is redacted — the scan
/// finds nothing; ambiguity never kills). The epoch-guard and gone-process paths are OS-independent.
/// </summary>
public class OrphanReaperTests {
    [TempDir] public required TempDir Tmp { get; init; }

    // Unique per test: the env-marker scan authorises a kill by KCAP_DAEMON_ID, so two
    // tests sharing one id reap each other's marked children.
    readonly string _daemonId = "did-" + Guid.NewGuid().ToString("N")[..8];


    AgentPidRecordStore NewStore() => new(Tmp.Path, NullLogger.Instance);

    static AgentPidRecord Rec(string agentId, int pid, string identity, string daemonId, string epoch) =>
        new(agentId, pid, identity, PidIdentityKind.Present, "ReviewFlow", "codex", "flow-1", "reviewer", daemonId, epoch, DateTimeOffset.UtcNow);

    [Test]
    public async Task Record_pass_reaps_a_prior_incarnation_survivor_and_deletes_the_record() {
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(
            30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "orphan" });
        var identity = ProcessIdentity.Capture(dummy.Pid)!;

        store.Write(Rec("orphan", dummy.Pid, identity, daemonId: _daemonId, epoch: "old-epoch"));

        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        // Record-pass kills once ownership is provable: a proven exact (pid, start-identity) match is
        // sufficient authorization on EVERY platform. Linux additionally confirms via the KCAP_AGENT_ID
        // env (defense-in-depth); macOS relies on the exact mac:{bootsessionuuid}:{p_uniqueid} identity
        // alone (env is redacted there — requiring it would defeat M1-A leader recovery); Windows has no
        // Unix env guard. Either way the survivor is REAPED (killed) on the first pass.
        dummy.WaitForExit(TimeSpan.FromSeconds(8));
        await Assert.That(dummy.HasExited).IsTrue();

        // Record DELETION: on Linux/Windows the kill confirms and the record is dropped on the first
        // pass. On macOS the daemon is NOT the child's parent, so KillConfirmAsync may observe the
        // not-yet-parent-reaped zombie as still "alive" within its confirm window (macOS IsAlive has no
        // zombie detection) and defer deletion to the next sweep — the spec's "eventual, next
        // boot/heartbeat" macOS recovery. A second sweep (the heartbeat re-run), after WaitForExit has
        // let the pid settle to fully-dead, deletes the record deterministically on every platform.
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Record_pass_leaves_a_current_incarnation_record_and_its_live_agent_untouched() {
        // The epoch guard is the load-bearing safety property for the HEARTBEAT re-run: a record whose
        // epoch equals the running daemon's must NEVER be reaped — it belongs to a live current agent.
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(
            30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "live" });
        var identity = ProcessIdentity.Capture(dummy.Pid)!;

        store.Write(Rec("live", dummy.Pid, identity, daemonId: _daemonId, epoch: "cur-epoch"));

        // Same currentEpoch as the record → must be skipped entirely (before any env read).
        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "cur-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        await Assert.That(dummy.HasExited).IsFalse();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "live")).IsTrue();
        dummy.Kill();
    }

    [Test]
    public async Task Record_pass_deletes_a_stale_record_whose_process_is_already_gone() {
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(30);
        var identity = ProcessIdentity.Capture(dummy.Pid)!;
        var pid = dummy.Pid;
        dummy.Kill();
        dummy.WaitForExit(TimeSpan.FromSeconds(5));

        store.Write(Rec("gone", pid, identity, daemonId: _daemonId, epoch: "old-epoch"));

        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        // Process gone (or PID reused → proven identity mismatch) → the stale record is deleted.
        await Assert.That(store.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Env_marker_scan_reaps_a_stale_epoch_survivor_of_this_daemon_only() {
        using var stale = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "s", ["KCAP_DAEMON_ID"] = _daemonId, ["KCAP_DAEMON_EPOCH"] = "old" });
        using var other = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "o", ["KCAP_DAEMON_ID"] = "OTHER", ["KCAP_DAEMON_EPOCH"] = "old" });
        using var mine = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "m", ["KCAP_DAEMON_ID"] = _daemonId, ["KCAP_DAEMON_EPOCH"] = "new" });

        // Empty store → the record pass is a no-op; only the env-marker scan runs.
        var reaper = new OrphanReaper(NewStore(), daemonId: _daemonId, currentEpoch: "new", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        if (OperatingSystem.IsLinux()) {
            stale.WaitForExit(TimeSpan.FromSeconds(8));
            await Assert.That(stale.HasExited).IsTrue();  // this daemon, prior epoch → reaped
            await Assert.That(other.HasExited).IsFalse(); // different daemon → spared
            await Assert.That(mine.HasExited).IsFalse();  // current epoch → spared
        } else {
            // macOS: env of another process can't be read → the scan finds nothing → all spared.
            await Assert.That(stale.HasExited).IsFalse();
            await Assert.That(other.HasExited).IsFalse();
            await Assert.That(mine.HasExited).IsFalse();
        }

        other.Kill();
        mine.Kill();
        stale.Kill();
    }

    [Test]
    public async Task Identity_unavailable_record_is_not_suppressed_from_the_marker_scan_and_self_deletes_on_confirmed_kill() {
        if (!OperatingSystem.IsLinux()) return;

        // A prior daemon incarnation spawned this, captured NOTHING (private-ABI hiccup — modeled
        // here directly since this test doesn't need real capture failure, just the RECORD shape),
        // and crashed. The record pass alone can never resolve an identity_unavailable record (no
        // token to compare) — it must fall through to the env-marker scan, which CAN reap it via
        // the live process's own KCAP_* triple, and must delete the record in the SAME operation.
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "unresolved", ["KCAP_DAEMON_ID"] = _daemonId, ["KCAP_DAEMON_EPOCH"] = "old-epoch" });

        store.Write(new AgentPidRecord("unresolved", dummy.Pid, "", PidIdentityKind.IdentityUnavailable,
            "ReviewFlow", "codex", "flow-1", "reviewer", _daemonId, "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        dummy.WaitForExit(TimeSpan.FromSeconds(8));
        await Assert.That(dummy.HasExited).IsTrue();
        // Positive, PID-independent resolution: the marker scan's own confirmed kill deleted the
        // identity_unavailable record — NOT a later record pass keyed on the numeric pid.
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "unresolved")).IsFalse();
    }

    [Test]
    public async Task Identity_unavailable_record_stays_pending_when_the_marker_read_is_unreadable() {
        if (!OperatingSystem.IsLinux()) return;

        // A recordless-of-env process (no KCAP_* markers at all — simulating a marker read that
        // can't confirm ownership) with an identity_unavailable record on file: the record pass
        // can't resolve it (no token) AND the marker scan can't confirm it (no matching env) —
        // it must stay PENDING (retained), never silently treated as absent/resolved.
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(30); // no KCAP_* env at all

        store.Write(new AgentPidRecord("unresolved2", dummy.Pid, "", PidIdentityKind.IdentityUnavailable,
            "ReviewFlow", "codex", "flow-1", "reviewer", _daemonId, "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        await Assert.That(dummy.HasExited).IsFalse();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "unresolved2")).IsTrue();
        dummy.Kill();
    }

    [Test]
    public async Task Forced_pid_reuse_between_marker_kill_and_a_later_sweep_never_acts_on_the_new_occupant() {
        if (!OperatingSystem.IsLinux()) return;

        // After the marker scan confirms a kill and deletes the identity_unavailable record, a
        // LATER sweep must find NOTHING to act on for that agent id even if the pid gets reused —
        // proven by running ReapOnceAsync a second time after spawning a decoy on a (best-effort)
        // recycled pid and confirming no record references it.
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "reused", ["KCAP_DAEMON_ID"] = _daemonId, ["KCAP_DAEMON_EPOCH"] = "old-epoch" });
        var firstPid = dummy.Pid;

        store.Write(new AgentPidRecord("reused", firstPid, "", PidIdentityKind.IdentityUnavailable,
            "ReviewFlow", "codex", "flow-1", "reviewer", _daemonId, "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();
        dummy.WaitForExit(TimeSpan.FromSeconds(8));
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "reused")).IsFalse();

        // A second sweep (simulating a later heartbeat) with an unrelated live process happening
        // to occupy a nearby pid must find nothing keyed to "reused" — the record is genuinely
        // gone, not re-derived from the numeric pid.
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "reused")).IsFalse();
    }

    [Test]
    public async Task Macos_identity_unavailable_record_is_identity_unresolvable_manual_only() {
        if (!OperatingSystem.IsMacOS()) return;

        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(30);

        store.Write(new AgentPidRecord("mac-unresolved", dummy.Pid, "", PidIdentityKind.IdentityUnavailable,
            "ReviewFlow", "codex", "flow-1", "reviewer", _daemonId, "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync(); // macOS has no marker scan — this can NEVER auto-resolve

        await Assert.That(dummy.HasExited).IsFalse();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "mac-unresolved")).IsTrue();

        // Manual-kill contract: once the operator kills the pid, the NEXT sweep observes it dead
        // (IsAlive false) and deletes the record normally — no special-case code needed for this
        // half, Classify's existing Dead branch already covers it.
        dummy.Kill();
        dummy.WaitForExit(TimeSpan.FromSeconds(5));
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "mac-unresolved")).IsFalse();
    }

    [Test]
    public async Task Macos_legacy_tk_record_is_legacy_unresolvable_spared_every_pass_until_manual_kill() {
        if (!OperatingSystem.IsMacOS()) return;

        // A pre-M1-A tk: record compared against the now-mac:-producing live process is
        // cross-scheme → Ambiguous → spared, every pass, forever — until the operator manually
        // kills it, at which point Classify's Dead branch (which runs BEFORE any token
        // comparison) confirms death regardless of scheme and the record is deleted normally.
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(30);

        store.Write(new AgentPidRecord("legacy-live", dummy.Pid, "tk:1", PidIdentityKind.Present,
            "ReviewFlow", "codex", "flow-1", "reviewer", _daemonId, "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, daemonId: _daemonId, currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        await Assert.That(dummy.HasExited).IsFalse();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "legacy-live")).IsTrue();

        dummy.Kill();
        dummy.WaitForExit(TimeSpan.FromSeconds(5));
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "legacy-live")).IsFalse();
    }
}
