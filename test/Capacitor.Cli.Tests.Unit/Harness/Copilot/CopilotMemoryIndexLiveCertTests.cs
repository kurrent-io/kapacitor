using Capacitor.Cli.Tests.Unit.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>
/// Env-gated certification that the SessionStart memory index actually reaches the model on GitHub
/// Copilot CLI. The envelope, budget, lease and silence-on-no-fragment behaviour are covered against
/// fakes by <c>CopilotSessionStartMemoryTests</c>; this is the only place asserting the end-to-end
/// claim.
///
/// <para>Copilot's adapter is the one that writes NOTHING when there is no fragment, so the negative
/// control here doubles as proof that silence really is silence on the wire and not an empty envelope
/// the model might still narrate.</para>
///
/// <para>Both tests are <c>[NotInParallel]</c>: the negative control mutates the REAL process-global
/// <c>disable_memory_index</c> profile config.</para>
/// </summary>
public class CopilotMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_COPILOT_MEMORY_LIVE";
    const string VendorLabel    = "copilot";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "a real `copilot -p` turn",
        "`copilot` on PATH with its SessionStart hook wired to `kcap` in ~/.copilot/hooks/");

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_copilot_session_start() {
        Gate();
        var nonce   = MemoryIndexLiveCertHarness.NewNonce();
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "copilot", ["--version"]);

            var answer = await RunCopilotAsync(MemoryIndexLiveCertHarness.PositivePrompt);

            await Assert.That(answer).Contains(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_copilot_session_start() {
        Gate();

        // Recorded here too: a stale PATH kcap makes a negative control pass vacuously.
        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "copilot", ["--version"]);

        // Read BEFORE anything is created. This throws on an unreadable config, and doing it after the
        // save would strand a real memory outside the archive-protecting try below.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var answer = await RunCopilotAsync(MemoryIndexLiveCertHarness.NegativePrompt);

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
    /// Runs one non-interactive Copilot turn in a throwaway directory. <c>-p</c> is Copilot's
    /// documented non-interactive scripting mode. No tool grants are passed: the prompt is
    /// answerable from injected context alone, so granting tools would widen what a cert run can do
    /// on this machine for no benefit.
    /// </summary>
    static async Task<string> RunCopilotAsync(string prompt) {
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);

        var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            "copilot", ["-p", prompt], worktree.Path);

        await Console.Out.WriteLineAsync($"[{VendorLabel}-memory-live] copilot exit={exitCode} stderr={stderr}");
        await Assert.That(exitCode).IsEqualTo(0);

        return MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout);
    }
}
