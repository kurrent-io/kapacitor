namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Env-gated certification that the SessionStart memory index actually reaches the model on Kiro CLI.
/// The raw-stdout contract and the lease dedupe are covered against fakes by
/// <c>KiroSessionStartMemoryTests</c> and the Kiro_* cases in <c>SessionStartMemoryFoundationTests</c>;
/// this is the only place asserting the end-to-end claims.
///
/// <para><b>Kiro carries a third cert the other harnesses do not.</b> Its <c>agentSpawn</c> hook fires
/// on EVERY prompt with the same session id, so "injects once per session" is the load-bearing
/// property of the whole adapter — and the unit tests can only prove the LEASE dedupes, not that the
/// model stops seeing the index on turn two. <c>kiro-cli chat --resume</c> continues the most recent
/// conversation in the same directory, which is what makes that certifiable at all.</para>
///
/// <para>All three tests are <c>[NotInParallel]</c>: the negative control mutates the REAL
/// process-global <c>disable_memory_index</c> config, and the dedupe cert depends on which
/// conversation is "most recent" in its own directory.</para>
/// </summary>
public class KiroMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_KIRO_MEMORY_LIVE";
    const string VendorLabel    = "kiro";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "one real `kiro-cli chat` turn per test (two for the dedupe cert)",
        "`kiro-cli` on PATH with its agentSpawn hook wired to `kcap` in ~/.kiro/agents/kcap.json");

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_kiro_agent_spawn() {
        Gate();
        var nonce   = MemoryIndexLiveCertHarness.NewNonce();
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);

        try {
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "kiro-cli", ["--version"]);

            var answer = await RunKiroAsync(worktree.FullName, MemoryIndexLiveCertHarness.PositivePrompt);

            await Assert.That(answer).Contains(nonce);
        } finally {
            TryDelete(worktree);
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    /// <summary>
    /// THE Kiro-specific claim: agentSpawn fires again on the second prompt, and the index must NOT be
    /// injected again. Turn two is resumed in the same directory, so it is the same Kiro session id and
    /// therefore the same lease key.
    ///
    /// <para><b>Why a SECOND nonce, saved only after turn one.</b> The obvious shape — ask turn two
    /// whether a "new" block was injected and assert the first nonce is absent — cannot work. Turn
    /// one's own answer put that nonce into the conversation history, so turn two can see it whether or
    /// not the index was re-injected, and "was this block NEW?" is a semantic judgment the model may
    /// get right or wrong for its own reasons. It could false-pass on a broken lease and false-fail on
    /// a working one.</para>
    ///
    /// <para>Saving a second memory AFTER turn one makes the property observable instead. Nonce two was
    /// never in the history, so it can only reach the model via a FRESH index fetch. Absent means no
    /// second fetch happened — the lease held. Present means it did. No model meta-reasoning
    /// involved.</para>
    ///
    /// <para>A failure here means the lease is not holding in production even though the unit tests
    /// pass — the exact gap between "our bytes dedupe" and "the model stops seeing it".</para>
    /// </summary>
    [Test, NotInParallel]
    public async Task A_resumed_kiro_session_does_not_get_the_index_injected_a_second_time() {
        Gate();
        var firstNonce    = MemoryIndexLiveCertHarness.NewNonce();
        var firstMemoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, firstNonce);

        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "kiro-cli", ["--version"]);

        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);

        try {
            var first = await RunKiroAsync(worktree.FullName, MemoryIndexLiveCertHarness.PositivePrompt);

            // Guard: if turn one never saw the index, turn two seeing nothing proves nothing.
            await Assert.That(first).Contains(firstNonce);

            // Saved AFTER turn one, so it cannot be in the resumed conversation history.
            var secondNonce    = MemoryIndexLiveCertHarness.NewNonce();
            var secondMemoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, secondNonce);

            try {
                // Same question as turn one. Echoing the FIRST nonce back from history is expected and
                // harmless; only the second nonce is diagnostic.
                var second = await RunKiroAsync(
                    worktree.FullName, MemoryIndexLiveCertHarness.PositivePrompt, resume: true);

                await Assert.That(second).DoesNotContain(secondNonce);
            } finally {
                await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, secondMemoryId);
            }
        } finally {
            TryDelete(worktree);
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, firstMemoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_kiro_agent_spawn() {
        Gate();
        var nonce   = MemoryIndexLiveCertHarness.NewNonce();
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        // Recorded here too: a stale PATH kcap makes a negative control pass vacuously.
        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "kiro-cli", ["--version"]);

        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var answer = await RunKiroAsync(worktree.FullName, MemoryIndexLiveCertHarness.NegativePrompt);

            await Assert.That(answer).DoesNotContain(nonce);
        } finally {
            TryDelete(worktree);
            // Nested: the restore THROWS on a failed or unconfirmed write, and that must not
            // be allowed to skip the archive — a leaked nonce corrupts every later cert's index.
            try {
                await MemoryIndexLiveCertHarness.RestoreDisableMemoryIndexAsync(original);
            } finally {
                await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
            }
        }
    }

    /// <summary>
    /// Runs one non-interactive Kiro turn. <c>--trust-tools=</c> trusts NO tools: the prompt is
    /// answerable from injected context alone, and an untrusted tool request in
    /// <c>--no-interactive</c> mode would otherwise be the likeliest way for a cert to stall.
    /// <paramref name="resume"/> continues the most recent conversation in
    /// <paramref name="cwd"/> — the same Kiro session id, which is what the dedupe cert needs.
    /// </summary>
    static async Task<string> RunKiroAsync(string cwd, string prompt, bool resume = false) {
        string[] args = resume
            ? ["chat", "--no-interactive", "--trust-tools=", "--resume", prompt]
            : ["chat", "--no-interactive", "--trust-tools=", prompt];

        var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            "kiro-cli", args, cwd);

        await Console.Out.WriteLineAsync(
            $"[{VendorLabel}-memory-live] kiro-cli resume={resume} exit={exitCode} stderr={stderr}");
        await Assert.That(exitCode).IsEqualTo(0);

        return MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout);
    }

    static void TryDelete(DirectoryInfo dir) {
        try { dir.Delete(recursive: true); } catch { /* best-effort */ }
    }
}
