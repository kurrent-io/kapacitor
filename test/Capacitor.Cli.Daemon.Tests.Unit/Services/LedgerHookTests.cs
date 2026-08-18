using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LedgerHookTests : IDisposable {
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    (AgentPidRecordStore store, ResolvedCandidatesLedger ledger, string dir) NewPair() {
        return (
            new AgentPidRecordStore(_tmp.Path, NullLogger.Instance),
            new ResolvedCandidatesLedger(_tmp.Path, NullLogger.Instance),
            dir: _tmp.Path
        );
    }

    [Test]
    public async Task Record_pass_appends_resolved_evidence_before_deleting_the_source() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // process gone -> confirmed absent

        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();

        var entry = ledger.Snapshot().Single();
        await Assert.That(entry.AgentId).IsEqualTo("gone");
        await Assert.That(entry.OldEpoch).IsEqualTo("old-epoch");
        await Assert.That(entry.FlowRole).IsEqualTo("reviewer"); // from the trusted record
        await Assert.That(store.ReadAll()).IsEmpty();            // source deleted after the append
    }

    [Test]
    public async Task Crash_between_append_and_delete_reconciles_idempotently_on_restart() {
        var (store, ledger, dir) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        // Simulate crash-after-append-before-delete: append, but skip the delete this pass.
        var crashing = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => { ledger.Upsert(a, e, fr, role); throw new IOException("crash before delete"); });
        try { await crashing.ReapOnceAsync(); } catch { /* the reaper swallows per-record faults */ }
        await Assert.That(store.ReadAll()).IsNotEmpty();          // leftover source
        var gen = ledger.Snapshot().Single().Generation;

        // Restart: re-derive from the leftover source; Upsert collapses onto the committed entry.
        var restarted = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await restarted.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();             // leftover source now deleted
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(gen); // single emit, no new generation
    }

    [Test]
    public async Task Crash_before_append_re_derives_the_record_pass_evidence_next_boot() {
        // Parent §8: crash BEFORE the durable append leaves the source only → re-derived next boot
        // (at-least-once, deduped). The record pass confirms death but the daemon dies before Upsert.
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        // Crash before the append: the confirmed-gone branch throws before touching the ledger.
        var crashing = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (_, _, _, _) => throw new IOException("crash before append"));
        try { await crashing.ReapOnceAsync(); } catch { /* per-record faults swallowed */ }
        await Assert.That(ledger.Snapshot()).IsEmpty();          // nothing committed
        await Assert.That(store.ReadAll()).IsNotEmpty();         // source persists

        // Next boot re-derives from the on-disk pre-append source shape (keyed (AgentId, OldEpoch)).
        var restarted = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await restarted.ReapOnceAsync();
        var entry = ledger.Snapshot().Single();
        await Assert.That(entry.AgentId).IsEqualTo("gone");
        await Assert.That(entry.OldEpoch).IsEqualTo("old-epoch");
        await Assert.That(store.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Quarantine_drain_returns_entries_and_confirmed_ones_are_emittable() {
        var q = new AgentKillQuarantine(NullLogger.Instance);
        using var dummy = DummyProcess.StartSleep(30);
        var id = ProcessIdentity.Capture(dummy.Pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // confirmed dead -> will drain

        q.Add(new AgentKillQuarantine.Entry("q1", dummy.Pid, id, "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer"));
        var drained = await q.RetryAllAsync(CancellationToken.None);

        await Assert.That(drained.Select(e => e.AgentId)).IsEquivalentTo(new[] { "q1" });
        await Assert.That(drained.Single().FlowRole).IsEqualTo("reviewer");
    }

    // ── Quarantine-drain hook — crash both sides (parent §8). The drain's durable source is the RETAINED
    // AgentPidRecord (teardown quarantines AND keeps the record); the drain emits (AgentId, epoch-at-drain,
    // flow…) then deletes the record. After a crash the record's DaemonEpoch == that same epoch, so the
    // next boot's OrphanReaper record pass reconciles on the source-stable (AgentId, OldEpoch) key (NOT
    // Generation, which the pre-append source lacks).
    [Test]
    public async Task Quarantine_drain_crash_between_append_and_delete_reconciles_single_emit() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("qr", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "drain-epoch", DateTimeOffset.UtcNow));
        var committed = ledger.Upsert("qr", "drain-epoch", "flow-1", "reviewer"); // drain appended
        // crash before DeletePidRecord → committed entry + leftover record

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(committed.Generation); // single emit
    }

    [Test]
    public async Task Quarantine_drain_crash_before_append_re_derives_next_boot() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("qr", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "drain-epoch", DateTimeOffset.UtcNow));
        await Assert.That(ledger.Snapshot()).IsEmpty(); // crash before append → record only

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(ledger.Snapshot().Single().AgentId).IsEqualTo("qr");
        await Assert.That(store.ReadAll()).IsEmpty();
    }

    // ── StopAgent-fallback hook — crash both sides (parent §8). TryStopByPidRecordAsync emits
    // (agentId, record.DaemonEpoch, record.flow…) before deleting the record; same durable source +
    // (AgentId, OldEpoch) reconciliation as the drain hook.
    [Test]
    public async Task Stop_fallback_crash_between_append_and_delete_reconciles_single_emit() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("sf", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-2", "reviewer", "did", "stop-epoch", DateTimeOffset.UtcNow));
        var committed = ledger.Upsert("sf", "stop-epoch", "flow-2", "reviewer"); // stop-fallback appended
        // crash before _pidRecords.Delete → committed entry + leftover record

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(committed.Generation);
    }

    [Test]
    public async Task Stop_fallback_crash_before_append_re_derives_next_boot() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("sf", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-2", "reviewer", "did", "stop-epoch", DateTimeOffset.UtcNow));
        await Assert.That(ledger.Snapshot()).IsEmpty(); // crash before append

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();
        await Assert.That(ledger.Snapshot().Single().AgentId).IsEqualTo("sf");
        await Assert.That(store.ReadAll()).IsEmpty();
    }
}
