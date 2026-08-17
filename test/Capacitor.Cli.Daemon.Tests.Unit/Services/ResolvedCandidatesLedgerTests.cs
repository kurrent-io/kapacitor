using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

// One test deletes and recreates its dir mid-test (below) — Dispose is best-effort and tolerates
// that.
public class ResolvedCandidatesLedgerTests : IDisposable {
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    ResolvedCandidatesLedger New(out string dir) {
        dir = _tmp.Path;
        return new ResolvedCandidatesLedger(dir, NullLogger.Instance);
    }

    [Test] public async Task Upsert_is_idempotent_on_agent_and_epoch_and_monotonic_generation() {
        var l = New(out _);
        var a = l.Upsert("a1", "e0", null, null);
        var b = l.Upsert("a2", "e0", null, null);
        var aAgain = l.Upsert("a1", "e0", null, null); // same key -> same generation, no new entry
        await Assert.That(a.Generation).IsEqualTo(1L);
        await Assert.That(b.Generation).IsEqualTo(2L);
        await Assert.That(aAgain.Generation).IsEqualTo(1L);
        await Assert.That(l.Snapshot().Count).IsEqualTo(2);
    }

    [Test] public async Task Snapshot_and_generation_survive_restart() {
        var l = New(out var dir);
        l.Upsert("a1", "e0", "flow", "reviewer");
        var reopened = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        await Assert.That(reopened.Snapshot().Single().AgentId).IsEqualTo("a1");
        // A new key after restart must not reuse generation 1.
        await Assert.That(reopened.Upsert("a2", "e0", null, null).Generation).IsEqualTo(2L);
    }

    // Phase B2-b (sequenced-settlement design §5.5): Upsert must be transactional with its durable write.
    // If the atomic write fails, the in-memory entry AND the generation counter must roll back so memory
    // never leads disk — otherwise the next sweep's idempotent short-circuit returns the unpersisted entry
    // without persisting, and the caller then deletes the durable source (violating append-before-delete).
    [Test] public async Task Upsert_rolls_back_entry_and_generation_when_the_durable_write_fails() {
        var l = New(out var dir);
        // Force the durable write to throw: remove the state dir so PersistState's temp write fails.
        Directory.Delete(dir, recursive: true);

        await Assert.That(() => { l.Upsert("a1", "e0", null, null); }).Throws<Exception>();
        await Assert.That(l.Snapshot()).IsEmpty(); // no phantom in-memory entry

        // The failed mint did NOT consume a generation: after the dir is restored, the SAME key mints
        // generation 1 (not 2).
        Directory.CreateDirectory(dir);
        await Assert.That(l.Upsert("a1", "e0", null, null).Generation).IsEqualTo(1L);

        // No partial/leftover file leads a fresh reload to a phantom entry.
        var reopened = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        await Assert.That(reopened.Snapshot().Single().AgentId).IsEqualTo("a1");
    }

    // Phase B2-b (sequenced-settlement design §5.5): the daemon-lifetime monotonic high-water — 0 before
    // any mint, N after N distinct Upserts — and unchanged by an Ack that prunes entries (so once sparse
    // acks prune the ledger the server still learns the generation frontier). Survives restart because
    // _nextGeneration is never decremented and Load restores it.
    [Test] public async Task HighestResolutionGeneration_reports_the_monotonic_high_water() {
        var l = New(out var dir);
        await Assert.That(l.HighestResolutionGeneration).IsEqualTo(0L); // nothing minted yet
        var g1 = l.Upsert("a1", "e0", null, null);
        l.Upsert("a2", "e0", null, null);
        l.Upsert("a3", "e0", null, null);
        await Assert.That(l.HighestResolutionGeneration).IsEqualTo(3L);

        l.Ack([new ResolvedCandidateAck(g1.Generation, "a1", "e0")]); // prune a1
        await Assert.That(l.HighestResolutionGeneration).IsEqualTo(3L); // unchanged by a prune

        var reopened = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        await Assert.That(reopened.HighestResolutionGeneration).IsEqualTo(3L); // survives restart
    }

    [Test] public async Task Ack_prunes_sparsely_without_head_of_line_blocking() {
        var l = New(out _);
        var g1 = l.Upsert("a1", "e0", null, null);
        var g2 = l.Upsert("a2", "e0", null, null);
        var g3 = l.Upsert("a3", "e0", null, null);
        l.Ack([new ResolvedCandidateAck(g1.Generation, "a1", "e0"),
               new ResolvedCandidateAck(g3.Generation, "a3", "e0")]);
        await Assert.That(l.Snapshot().Select(x => x.AgentId)).IsEquivalentTo(new[] { "a2" });
        l.Ack([new ResolvedCandidateAck(g2.Generation, "a2", "e0")]); // idempotent for already-pruned too
        l.Ack([new ResolvedCandidateAck(99, "nope", "e0")]);          // unknown -> no-op
        await Assert.That(l.Snapshot()).IsEmpty();
    }
}
