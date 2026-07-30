using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Env-gated certification that the SessionStart memory index actually reaches the model on Codex
/// CLI. The envelope, budget, lease and fail-open behaviour are covered against fakes by
/// <c>CodexSessionStartMemoryTests</c>; this is the only place asserting the end-to-end claim.
///
/// <para>Both tests are <c>[NotInParallel]</c>: the negative control mutates the REAL process-global
/// <c>disable_memory_index</c> profile config via a `kcap config set` subprocess, so an interleaved
/// positive run could observe the disabled flag and fail for the wrong reason.</para>
/// </summary>
public class CodexMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_CODEX_MEMORY_LIVE";
    const string VendorLabel    = "codex";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "a real `codex exec` turn",
        "`codex` on PATH with its SessionStart hook wired to `kcap` in ~/.codex/hooks.json");

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_codex_session_start() {
        Gate();

        var baseUrl = await MemoryIndexLiveCertHarness.InitializeAndResolveServerUrlAsync();
        var nonce   = MemoryIndexLiveCertHarness.NewNonce();

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(client, baseUrl, VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.RecordVersionAsync(VendorLabel, "codex", ["--version"]);

            var answer = await RunCodexAsync(MemoryIndexLiveCertHarness.PositivePrompt);

            await Assert.That(answer).Contains(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(client, baseUrl, VendorLabel, memoryId);
        }
    }

    /// <summary>
    /// Negative control. Without it the positive test is not evidence: an identically-worded ask with
    /// injection DISABLED must not surface the nonce, which is what rules out the nonce arriving by
    /// some other channel or the assertion being trivially satisfiable.
    /// </summary>
    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_codex_session_start() {
        Gate();

        var baseUrl = await MemoryIndexLiveCertHarness.InitializeAndResolveServerUrlAsync();
        var nonce   = MemoryIndexLiveCertHarness.NewNonce();

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(client, baseUrl, VendorLabel, nonce);

        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var answer = await RunCodexAsync(MemoryIndexLiveCertHarness.NegativePrompt);

            await Assert.That(answer).DoesNotContain(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.RestoreDisableMemoryIndexAsync(original);
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(client, baseUrl, VendorLabel, memoryId);
        }
    }

    /// <summary>
    /// Runs one non-interactive Codex turn in a throwaway directory.
    ///
    /// <para>The prompt goes over STDIN with <c>-</c> rather than as an argument: `codex exec` reads
    /// from stdin when the prompt is `-` or absent, and passing a long prompt as an argument has been
    /// observed to hang. <c>--skip-git-repo-check</c> is required because the cert worktree is a bare
    /// temp directory, not a repo.</para>
    /// </summary>
    static async Task<string> RunCodexAsync(string prompt) {
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);

        try {
            var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
                "codex", ["exec", "--skip-git-repo-check", "-"], worktree.FullName, stdin: prompt);

            await Console.Out.WriteLineAsync($"[{VendorLabel}-memory-live] codex exit={exitCode} stderr={stderr}");
            await Assert.That(exitCode).IsEqualTo(0);

            return MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout);
        } finally {
            try { worktree.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }
}
