using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class MarkerCandidateResolutionTests {
    static (AgentPidRecordStore store, MarkerCandidateStore markers, string dir) New(TempDir tmp) =>
        (new AgentPidRecordStore(tmp.Path, NullLogger.Instance), new MarkerCandidateStore(tmp.Path, NullLogger.Instance), tmp.Path);

    [Test]
    public async Task Recordless_marker_kill_emits_resolved_and_deletes_the_source() {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var (store, markers, _) = New(tmp);
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "rec-less", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "old" });

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        dummy.WaitForExit(TimeSpan.FromSeconds(8));
        await Assert.That(dummy.HasExited).IsTrue();
        await Assert.That(resolved).Contains(("rec-less", "old"));
        await Assert.That(markers.ReadAll()).IsEmpty();          // source deleted after the emit
    }

    [Test]
    public async Task Alive_mismatch_spares_and_leaves_the_marker_candidate_pending() {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var (store, markers, _) = New(tmp);
        // A persisted marker-candidate whose pid is now occupied by an UNRELATED process (no triple).
        using var occupant = DummyProcess.StartSleep(30); // no KCAP_* env
        markers.Write(new MarkerCandidate("stale", "did", "old", occupant.Pid));

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync(); // boot reconciliation re-reads the source, re-runs (a)/(b)/(c)

        await Assert.That(occupant.HasExited).IsFalse();          // reused pid spared
        await Assert.That(resolved).IsEmpty();                    // (c) never emits
        await Assert.That(markers.ReadAll().Single().AgentId).IsEqualTo("stale"); // stays pending
        occupant.Kill();
    }

    [Test]
    public async Task Confirmed_dead_persisted_candidate_resolves_incl_zombie_path() {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var (store, markers, _) = New(tmp);
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive
        markers.Write(new MarkerCandidate("dead1", "did", "old", pid));

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        await Assert.That(resolved).Contains(("dead1", "old")); // (a) dead -> resolved+emit
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    // ── Marker-scan kill hook — crash both sides (parent §8; recordless→marker-candidate case). ──
    [Test]
    public async Task Marker_kill_crash_before_append_re_derives_from_the_persisted_source() {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var (store, markers, _) = New(tmp);
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive (branch a)
        markers.Write(new MarkerCandidate("mk-gone", "did", "old", pid));

        // Crash BEFORE the emit: onMarkerResolved throws before the ledger append -> no entry, source persists.
        var crashing = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (_, _) => throw new IOException("crash before append"));
        try { await crashing.ReapOnceAsync(); } catch { /* per-source faults swallowed */ }
        await Assert.That(markers.ReadAll()).IsNotEmpty(); // source persists (never a source-less window)

        // Next boot reconciles: re-read the on-disk source, (a) dead -> single emit + delete.
        var resolved = new List<(string, string)>();
        var restarted = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await restarted.ReapOnceAsync();
        await Assert.That(resolved).IsEquivalentTo(new[] { ("mk-gone", "old") });
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Marker_kill_crash_between_append_and_source_delete_reconciles_idempotently() {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var (store, markers, dir) = New(tmp);
        var ledger = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        markers.Write(new MarkerCandidate("mk-gone", "did", "old", pid));
        var committed = ledger.Upsert("mk-gone", "old", null, null); // append happened
        // crash before markerStore.Delete -> committed entry + leftover marker source

        // Next boot: reconciliation re-reads the source, (a) dead -> idempotent Upsert (key (AgentId,OldEpoch)) + delete.
        var restarted = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => ledger.Upsert(a, e, null, null));
        await restarted.ReapOnceAsync();
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(committed.Generation); // single emit
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    // Phase B2-b (sequenced-settlement design): the env agentId on a marker candidate is UNTRUSTED for
    // role authority. A prior-epoch DESCENDANT that inherited a still-live leader X's KCAP_AGENT_ID env
    // triple (the descendant-outlives-leader residual) and is reaped under a DIFFERENT pid must NOT be
    // corroborated against X's still-on-disk leader record: matching on agentId ALONE would emit a FALSE
    // trusted-flow death proof for the LIVE leader AND delete its durable record (stranding it).
    // EmitAndClear must also corroborate the reaped pid + the prior epoch. Cross-platform: the marker
    // resolves via branch (a) (!IsAlive of the reaped pid), which needs no env-marker scan.
    [Test]
    public async Task Marker_resolution_does_not_delete_or_falsely_resolve_a_non_corroborating_live_leader_record() {
        using var tmp = new TempDir();
        var (store, markers, _) = New(tmp);

        // The reaped descendant: a real, test-owned process, killed so branch (a) (!IsAlive) resolves it.
        using var descendant = DummyProcess.StartSleep(30);
        var reapedPid = descendant.Pid;
        descendant.Kill();
        descendant.WaitForExit(TimeSpan.FromSeconds(5));

        // The LIVE leader X's durable record: X's OWN pid (alive, != the reaped pid) and the CURRENT
        // epoch (a live current-incarnation leader). Neither the reaped pid nor the marker's prior epoch
        // corroborates — the record pass skips a current-epoch record, so the live process is untouched.
        var leaderPid = Environment.ProcessId; // alive, distinct from the reaped pid, carries no KCAP_* env
        store.Write(new AgentPidRecord("leader-x", leaderPid, "tok", PidIdentityKind.Present,
            "ReviewFlow", "codex", "flow-x", "leader", "did", "cur", DateTimeOffset.UtcNow));

        // The marker candidate carries the descendant's INHERITED agentId (leader-x), the PRIOR epoch,
        // and the reaped (dead) pid.
        markers.Write(new MarkerCandidate("leader-x", "did", "old", reapedPid));

        var recordResolved = new List<(string a, string e, string? fr, string? role)>();
        var markerResolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "cur", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => recordResolved.Add((a, e, fr, role)),
            markerStore: markers, onMarkerResolved: (a, e) => markerResolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        // The live leader's record is UNTOUCHED — not deleted, no trusted-flow death proof emitted for it.
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "leader-x")).IsTrue();
        await Assert.That(recordResolved).IsEmpty();
        // The resolution falls back to the recordless (null-flow) path and still clears the marker source.
        await Assert.That(markerResolved).IsEquivalentTo(new[] { ("leader-x", "old") });
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    // Phase B2-b (sequenced-settlement design §5.5): if a discovered recordless survivor's durable
    // marker-candidate source WRITE fails, the survivor is neither recorded (invisible to
    // BlockedCandidates) nor killed — so the scan must NOT report Complete this pass, or
    // StartupReapComplete could go true beside a live prior-epoch survivor and the server would launch a
    // duplicate. Discovery must stay Failed (retried next heartbeat).
    [Test]
    public async Task Marker_scan_stays_incomplete_when_a_candidate_source_write_fails() {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var (store, _, dir) = New(tmp);
        // A MarkerCandidateStore whose Write ALWAYS throws: its state dir is actually a FILE, so
        // Directory.CreateDirectory(_dir) fails for every Write.
        var badBase = Path.Combine(dir, "not-a-dir");
        File.WriteAllText(badBase, "x");
        var failingMarkers = new MarkerCandidateStore(badBase, NullLogger.Instance);

        // A live prior-epoch recordless survivor the scan discovers and tries (and fails) to capture.
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "surv", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "old" });

        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance, markerStore: failingMarkers);
        await reaper.ReapOnceAsync();

        // The source write failed, so the pass is not a completeness proof.
        await Assert.That(reaper.CurrentDiscovery.MarkerScanState).IsEqualTo(MarkerScanState.Failed);
        // The survivor was never killed (we never resolve without a durable source first).
        await Assert.That(dummy.HasExited).IsFalse();
        dummy.Kill();
    }

    [Test]
    public async Task Identity_unavailable_record_resolved_via_marker_scan_emits_trusted_flow() {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var (store, markers, _) = New(tmp);
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive (branch a)
        // A Linux identity_unavailable RECORD (capture failed) co-existing with a marker-candidate source.
        store.Write(new AgentPidRecord("iu", pid, "", PidIdentityKind.IdentityUnavailable, "ReviewFlow",
            "codex", "flow-9", "reviewer", "did", "old", DateTimeOffset.UtcNow));
        markers.Write(new MarkerCandidate("iu", "did", "old", pid));

        var recordResolved = new List<(string a, string e, string? fr, string? role)>();
        var markerResolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => recordResolved.Add((a, e, fr, role)),
            markerStore: markers, onMarkerResolved: (a, e) => markerResolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        // The trusted record's flow is emitted (via onRecordResolved), NOT null (via onMarkerResolved).
        await Assert.That(recordResolved.Single().role).IsEqualTo("reviewer");
        await Assert.That(recordResolved.Single().fr).IsEqualTo("flow-9");
        await Assert.That(markerResolved).IsEmpty();
        await Assert.That(store.ReadAll()).IsEmpty();       // identity_unavailable record cleared
        await Assert.That(markers.ReadAll()).IsEmpty();     // marker source cleared
    }
}
