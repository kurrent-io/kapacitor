using Capacitor.Cli.Core;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The once-per-session nudge claim for harnesses whose start callback repeats per prompt without a
/// vendor counter to key on. What matters is the direction of failure: a repeat, an unusable id, or
/// an unavailable store must refuse — the nudge lands in a context channel the harness persists, so
/// one suppressed emission beats one per turn.
/// </summary>
public class NudgeLeaseTests {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    static SessionStartMemoryLeaseStore Store(TempDir root) => new(root.Path, TimeProvider.System);

    [Test]
    public async Task the_first_claim_wins_and_repeats_are_refused() {
        using var root = new TempDir();
        await Assert.That(await NudgeLease.TryClaimAsync(Store(root), HarnessId.Kiro, "kiro-session", Budget)).IsTrue();
        await Assert.That(await NudgeLease.TryClaimAsync(Store(root), HarnessId.Kiro, "kiro-session", Budget)).IsFalse();
    }

    [Test]
    public async Task distinct_sessions_claim_independently() {
        using var root = new TempDir();
        await Assert.That(await NudgeLease.TryClaimAsync(Store(root), HarnessId.Kiro, "session-a", Budget)).IsTrue();
        await Assert.That(await NudgeLease.TryClaimAsync(Store(root), HarnessId.Kiro, "session-b", Budget)).IsTrue();
    }

    // Kiro session ids are GUIDs the identity normaliser canonicalises; two spellings of one session
    // arriving across firings must not double-nudge.
    [Test]
    public async Task guid_spellings_collapse_to_one_claim() {
        using var root = new TempDir();
        await Assert.That(await NudgeLease.TryClaimAsync(
            Store(root), HarnessId.Kiro, "6f9619ff-8b86-d011-b42d-00cf4fc964ff", Budget)).IsTrue();
        await Assert.That(await NudgeLease.TryClaimAsync(
            Store(root), HarnessId.Kiro, "6F9619FF8B86D011B42D00CF4FC964FF", Budget)).IsFalse();
    }

    // The claim lives in the shared store under its own key domain: claiming the nudge must never
    // spend the memory lane's lease for the same session, or the index would stop injecting.
    [Test]
    public async Task the_nudge_claim_does_not_collide_with_the_memory_lease() {
        using var root = new TempDir();
        var store = Store(root);
        await Assert.That(await NudgeLease.TryClaimAsync(store, HarnessId.Kiro, "kiro-session", Budget)).IsTrue();

        var memoryKey = SessionStartMemoryIdentity.Create(HarnessId.Kiro, "kiro-session", null);
        await Assert.That(await store.TryBeginAsync(memoryKey, Budget)).IsNotNull();
    }

    [Test]
    public async Task an_unusable_session_id_never_claims() {
        using var root = new TempDir();
        await Assert.That(await NudgeLease.TryClaimAsync(Store(root), HarnessId.Kiro, "", Budget)).IsFalse();
    }

    // Store construction sits inside the claim's failure boundary: a config dir whose store root
    // cannot be created must read as unclaimed, never throw out of a hook.
    [Test]
    public async Task an_uncreatable_store_root_reads_as_unclaimed() {
        using var root = new TempDir();
        var file = root.CreateFile("occupied");
        var config = new ConfigRoot(Path.Combine(file, "under-a-file"));

        await Assert.That(await NudgeLease.TryClaimAsync(config, TimeProvider.System, HarnessId.Kiro, "kiro-session", Budget)).IsFalse();
    }
}
