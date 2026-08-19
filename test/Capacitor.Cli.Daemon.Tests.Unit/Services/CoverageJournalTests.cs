using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class CoverageJournalTests {
    // Real DaemonLock lifecycle so the InstanceId chain is genuine, never a hand-edited marker.
    sealed class Harness : IDisposable {
        const string DaemonName = "alpha";

        readonly TempDaemonStore _daemons = new();

        // The lock and the journal share one daemons directory, as they do in production.
        public readonly string StateDirectory;

        public Harness() {
            StateDirectory = _daemons.Store.StateDirectory(DaemonName);
            // Pre-created, as DaemonRunner does at boot: one test's premise is a used-looking state dir.
            Directory.CreateDirectory(StateDirectory);
        }

        // One real boot: acquire the lock, record coverage (aware), dispose.
        public bool AwareBoot(bool contained = true) {
            using var l = DaemonLock.TryAcquire(_daemons.Store, DaemonName)!;
            return new CoverageJournal(StateDirectory, NullLogger.Instance)
                .RecordBoot(l.InstanceId, l.PriorInstanceId, priorLockReadFailed: l.PriorLockIndeterminate, contained);
        }
        // An unaware/old boot: acquires the real lock (mints a fresh InstanceId) but writes NO journal.
        public void UnawareBoot() { using var l = DaemonLock.TryAcquire(_daemons.Store, DaemonName)!; }
        public void Dispose() => _daemons.Dispose();
    }

    [Test] public async Task Genesis_first_ever_contained_boot_seeds_true() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot(contained: true)).IsTrue();
    }

    [Test] public async Task Genesis_uncontained_epoch_is_false() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot(contained: false)).IsFalse();
    }

    [Test] public async Task Clean_and_crashed_W1_to_W1_keep_the_chain() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot()).IsTrue();
        await Assert.That(h.AwareBoot()).IsTrue(); // prior tail id == prior lock id ⇒ chain intact
    }

    [Test] public async Task Downgrade_sandwich_breaks_the_chain_permanently() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot()).IsTrue();
        h.UnawareBoot();                                   // old boot mints a fresh lock InstanceId, no journal
        await Assert.That(h.AwareBoot()).IsFalse();  // prior lock id != journal tail id ⇒ broken
        await Assert.That(h.AwareBoot()).IsFalse();  // sticky: the detecting boot persisted false
    }

    [Test] public async Task Aware_but_uncontained_epoch_poisons_all_later_boots() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot(contained: true)).IsTrue();
        await Assert.That(h.AwareBoot(contained: false)).IsFalse(); // this_epoch_contained=false
        await Assert.That(h.AwareBoot(contained: true)).IsFalse();  // folds from false ⇒ still false
    }

    [Test] public async Task Empty_looking_used_dir_with_prior_lock_is_false() {
        using var h = new Harness();
        // A pre-existing (previously-used) state dir with NO journal file + a prior lock InstanceId.
        // Genesis-eligibility is "journal absent", but a prior lock InstanceId ⇒ un-journaled history ⇒ false.
        h.UnawareBoot(); // prior lock id exists; state dir has no journal
        await Assert.That(h.AwareBoot()).IsFalse();
    }

    [Test] public async Task Indeterminate_prior_lock_is_not_genesis_even_with_absent_journal() {
        using var h = new Harness();
        // A read-failed / blank prior lock (priorLockReadFailed = true) with an absent journal must NOT be
        // treated as genesis — an unreadable prior lock could hide an intervening boot, so it fails closed
        // (regression: previously null priorLockInstanceId alone drove genesis, conflating empty-vs-unreadable).
        var covered = new CoverageJournal(h.StateDirectory, NullLogger.Instance)
            .RecordBoot("me", priorLockInstanceId: null, priorLockReadFailed: true, thisEpochContained: true);
        await Assert.That(covered).IsFalse();
        // A genuinely empty prior lock (readFailed = false, id = null) with an absent journal is still genesis.
        using var h2 = new Harness();
        var genesis = new CoverageJournal(h2.StateDirectory, NullLogger.Instance)
            .RecordBoot("me", priorLockInstanceId: null, priorLockReadFailed: false, thisEpochContained: true);
        await Assert.That(genesis).IsTrue();
    }

    [Test] public async Task Corrupt_coverage_state_is_false() {
        using var h = new Harness();
        await Assert.That(h.AwareBoot()).IsTrue();
        File.WriteAllText(Path.Combine(h.StateDirectory, "coverage.json"), "{ not json");
        await Assert.That(h.AwareBoot()).IsFalse();
    }

    [Test] public async Task Genesis_crash_before_the_single_rename_leaves_no_journal_and_reseeds() {
        using var h = new Harness();
        // The genesis write is ONE atomic temp+rename of a single {initialized,instance_id,cumulative_covered}
        // document. A crash BEFORE the rename leaves at most a stray .tmp — never a partial journal and never
        // a separate marker — so File.Exists(coverage.json) stays false and the next boot is still
        // genesis-eligible (re-seeds correctly). A COMPLETED rename is a valid, fully-initialized journal.
        Directory.CreateDirectory(h.StateDirectory);
        var journal = Path.Combine(h.StateDirectory, "coverage.json");
        File.WriteAllText(journal + ".tmp-deadbeef", "{ partial"); // crash-before-rename residue
        await Assert.That(File.Exists(journal)).IsFalse();

        await Assert.That(h.AwareBoot(contained: true)).IsTrue();  // journal absent + no prior lock id ⇒ genesis re-seeds
        await Assert.That(File.Exists(journal)).IsTrue();          // the completed rename is durable
        await Assert.That(h.AwareBoot(contained: true)).IsTrue();  // a valid initialized journal ⇒ chain intact
    }
}
