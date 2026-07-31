namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Env-gated certification that the SessionStart memory index actually reaches the model on Gemini CLI.
/// The envelope shape and the always-emit invariant are covered against fakes by
/// <c>GeminiSessionStartMemoryTests</c>; this is the only place asserting the end-to-end claims.
///
/// <para><b>This cert is load-bearing, not a nice-to-have.</b> Gemini's hook stdout is a JSON decision
/// channel, and a green unit suite only proves the bytes we emit — not that Gemini accepts them. The
/// positive case therefore asserts the turn <i>completes</i>, not merely that the nonce appears: a
/// harness that masked a failed invocation would otherwise look like a pass.</para>
///
/// <para>Both tests are <c>[NotInParallel]</c>: the negative control mutates the REAL process-global
/// <c>disable_memory_index</c> config. <c>[NotInParallel]</c> only prevents concurrency — it restores
/// nothing — so every test snapshots before creating anything and restores in a <c>finally</c>,
/// including the unset state.</para>
/// </summary>
public class GeminiMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_GEMINI_MEMORY_LIVE";
    const string VendorLabel    = "gemini";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "one real `gemini` turn per test",
        "`gemini` (>= 0.53.0) on PATH with its SessionStart hook wired to `kcap` in ~/.gemini/settings.json");

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_gemini_session_start() {
        Gate();

        // Read BEFORE anything is created: a leaked `true` from an earlier failed run would make this
        // positive case pass vacuously, so the opt-out state is asserted rather than assumed.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();
        await Assert.That(original is true).IsFalse();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            // Records the exact binary version. A cert passing against an unknown build is how the
            // earlier memory-cert failures were misdiagnosed as a code defect when the real cause was a
            // stale installed binary.
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "gemini", ["--version"]);

            var (exitCode, answer) = await RunGeminiAsync(worktree.FullName, MemoryIndexLiveCertHarness.PositivePrompt);

            // Turn completion is part of the contract, not incidental: it is what verifies that a
            // hookSpecificOutput-only payload does not disturb Gemini's decision channel.
            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(answer).Contains(nonce);
        } finally {
            TryDelete(worktree);
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_gemini_session_start() {
        Gate();

        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "gemini", ["--version"]);

        // Snapshot before anything is created — this throws on an unreadable config, and doing it after
        // the save would strand a real memory outside the archive-protecting try below.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var (_, answer) = await RunGeminiAsync(worktree.FullName, MemoryIndexLiveCertHarness.NegativePrompt);

            await Assert.That(answer).DoesNotContain(nonce);
        } finally {
            TryDelete(worktree);
            // Nested: the restore THROWS on a failed or unconfirmed write, and that must not skip the
            // archive — a leaked nonce corrupts every later cert's index.
            try {
                await MemoryIndexLiveCertHarness.RestoreDisableMemoryIndexAsync(original);
            } finally {
                await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
            }
        }
    }

    /// <summary>Runs one non-interactive Gemini turn. <c>--approval-mode plan</c> keeps it read-only so
    /// an unexpected tool request cannot stall the cert.</summary>
    static async Task<(int ExitCode, string Answer)> RunGeminiAsync(string cwd, string prompt) {
        var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            "gemini", ["--approval-mode", "plan", "--prompt", prompt], cwd);

        await Console.Out.WriteLineAsync($"[{VendorLabel}-memory-live] gemini exit={exitCode} stderr={stderr}");

        return (exitCode, MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout));
    }

    static void TryDelete(DirectoryInfo dir) {
        try { dir.Delete(recursive: true); } catch { /* best-effort */ }
    }
}
