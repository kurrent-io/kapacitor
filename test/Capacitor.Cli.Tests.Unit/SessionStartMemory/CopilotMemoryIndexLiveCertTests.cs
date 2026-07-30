using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

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

        var baseUrl = await MemoryIndexLiveCertHarness.InitializeAndResolveServerUrlAsync();
        var nonce   = MemoryIndexLiveCertHarness.NewNonce();

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(client, baseUrl, VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.RecordVersionAsync(VendorLabel, "copilot", ["--version"]);

            var answer = await RunCopilotAsync(MemoryIndexLiveCertHarness.PositivePrompt);

            await Assert.That(answer).Contains(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(client, baseUrl, VendorLabel, memoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_copilot_session_start() {
        Gate();

        var baseUrl = await MemoryIndexLiveCertHarness.InitializeAndResolveServerUrlAsync();
        var nonce   = MemoryIndexLiveCertHarness.NewNonce();

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(client, baseUrl, VendorLabel, nonce);

        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var answer = await RunCopilotAsync(MemoryIndexLiveCertHarness.NegativePrompt);

            await Assert.That(answer).DoesNotContain(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.RestoreDisableMemoryIndexAsync(original);
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(client, baseUrl, VendorLabel, memoryId);
        }
    }

    /// <summary>
    /// Runs one non-interactive Copilot turn in a throwaway directory. <c>-p</c> is Copilot's
    /// documented non-interactive scripting mode. No tool grants are passed: the prompt is
    /// answerable from injected context alone, so granting tools would widen what a cert run can do
    /// on this machine for no benefit.
    /// </summary>
    static async Task<string> RunCopilotAsync(string prompt) {
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);

        try {
            var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
                "copilot", ["-p", prompt], worktree.FullName);

            await Console.Out.WriteLineAsync($"[{VendorLabel}-memory-live] copilot exit={exitCode} stderr={stderr}");
            await Assert.That(exitCode).IsEqualTo(0);

            return MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout);
        } finally {
            try { worktree.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }
}
