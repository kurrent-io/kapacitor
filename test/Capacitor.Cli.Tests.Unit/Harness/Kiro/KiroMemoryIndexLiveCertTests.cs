using Capacitor.Cli.Tests.Unit.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

/// <summary>
/// Env-gated certification that the SessionStart memory index actually reaches the model on Kiro CLI.
/// The raw-stdout contract and the lease dedupe are covered against fakes by
/// <c>KiroSessionStartMemoryTests</c> and the Kiro_* cases in <c>SessionStartMemoryFoundationTests</c>;
/// this is the only place asserting the end-to-end claims.
///
/// <para><b>The once-per-session dedupe is NOT certifiable through this CLI, and there is
/// deliberately no cert for it here.</b> The adapter's load-bearing property is that
/// <c>agentSpawn</c> fires per PROMPT within one session and the lease suppresses re-injection. The
/// obvious probe — <c>kiro-cli chat --resume</c> for a second turn — does NOT test that. Measured
/// 2026-07-30: a resumed invocation carries a DIFFERENT hook <c>session_id</c> than the turn it
/// resumes, even though Kiro's own chat SessionId is unchanged and the conversation genuinely
/// continues (2 msgs to 4). Two lease records were written five seconds apart, one per invocation.</para>
///
/// <para>So a resumed turn is a NEW session to kcap and re-injecting there is correct — a cert
/// asserting absence would have been asserting a bug, and an earlier draft of this file did exactly
/// that and "passed" only because its probe shared a prefix with the first turn's nonce. Two separate
/// processes are not two prompts in one session. Certifying the real property needs several prompts
/// inside ONE interactive process (PTY automation), which this suite does not do; the lease stays
/// covered by the <c>Kiro_*</c> cases in <c>SessionStartMemoryFoundationTests</c>.</para>
///
/// <para>Both tests are <c>[NotInParallel]</c>: the negative control mutates the REAL
/// process-global <c>disable_memory_index</c> config.</para>
/// </summary>
public class KiroMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_KIRO_MEMORY_LIVE";
    const string VendorLabel    = "kiro";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "one real `kiro-cli chat` turn per test",
        "`kiro-cli` on PATH with its agentSpawn hook wired to `kcap` in ~/.kiro/agents/kcap.json");

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_kiro_agent_spawn() {
        Gate();
        var nonce = MemoryIndexLiveCertHarness.NewNonce();

        // Worktree first: it is local and can throw (permissions, disk, temp state), and creating it
        // after the remote save would strand a real memory outside the archive-protecting try.
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "kiro-cli", ["--version"]);

            var answer = await RunKiroAsync(worktree.Path, MemoryIndexLiveCertHarness.PositivePrompt);

            await Assert.That(answer).Contains(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_kiro_agent_spawn() {
        Gate();

        // Recorded here too: a stale PATH kcap makes a negative control pass vacuously.
        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "kiro-cli", ["--version"]);

        // Read BEFORE anything is created. This throws on an unreadable config, and doing it after the
        // save would strand a real memory outside the archive-protecting try below.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        var nonce = MemoryIndexLiveCertHarness.NewNonce();

        // Worktree before the remote save, for the same reason as the positive cert above.
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var answer = await RunKiroAsync(worktree.Path, MemoryIndexLiveCertHarness.NegativePrompt);

            await Assert.That(answer).DoesNotContain(nonce);
        } finally {
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
    /// </summary>
    static async Task<string> RunKiroAsync(string cwd, string prompt) {
        var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            "kiro-cli", ["chat", "--no-interactive", "--trust-tools=", prompt], cwd);

        await Console.Out.WriteLineAsync(
            $"[{VendorLabel}-memory-live] kiro-cli exit={exitCode} stderr={stderr}");
        await Assert.That(exitCode).IsEqualTo(0);

        return MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout);
    }
}
