using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

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

            var (exitCode, answer) = await RunGeminiAsync(worktree.FullName, MemoryIndexLiveCertHarness.NegativePrompt);

            // Turn completion is asserted FIRST, and it is what stops this control being vacuous: a
            // turn that never ran trivially contains no nonce. Observed for real — before
            // `--skip-trust` was passed, gemini exited 55 on the trust gate and this test passed
            // while proving nothing at all. The claim is "the opt-out suppressed the injection",
            // which is only meaningful if the model was actually asked.
            await Assert.That(exitCode).IsEqualTo(0);
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

    /// <summary>
    /// The third case §3.3 owes: a SessionStart whose lifecycle POST genuinely fails, so the hook exits
    /// non-zero while still emitting <c>additionalContext</c> — does the model still receive it?
    ///
    /// <para>§3.3 chose "no commit gate" on a reading of Gemini's hook runner (<c>stdout.trim() ||
    /// stderr.trim()</c>, parsed with no exit-code gate). That is a source reading, not a behavioural
    /// one, and the spec's definition of done requires settling it live. This test is that settlement:
    /// if Gemini ever did discard a failing hook's stdout, the lease would be spent on an injection the
    /// model never saw, and the decision would have to flip to a commit gate.</para>
    ///
    /// <para>The failure is REAL, not simulated: a WireMock instance proxies the whole API to the
    /// configured server and fails exactly <c>POST /hooks/session-start/gemini</c>. The index fetch on
    /// the very same base URL is proxied through untouched, which is the only arrangement that produces
    /// the shape under test — a hook that fetched a real index and then failed its POST.</para>
    ///
    /// <para><b>Two links, asserted separately, because the live turn alone cannot prove either.</b>
    /// Gemini's exit code says nothing about its hook's exit code, so (1) the proxy is asserted to have
    /// actually failed a session-start POST during the run, and (2) the same binary is driven directly
    /// against the same proxy to prove that a failed POST does exit non-zero AND still writes parseable
    /// <c>additionalContext</c>. Without (2) a green run here would be equally consistent with the POST
    /// having quietly succeeded.</para>
    /// </summary>
    [Test, NotInParallel]
    public async Task Failed_session_start_post_still_delivers_the_index_to_a_real_gemini_session() {
        Gate();

        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();
        await Assert.That(original is true).IsFalse();

        var upstream = await MemoryIndexLiveCertHarness.InitializeAndResolveServerUrlAsync();

        // The proxy is stood up BEFORE the nonce memory is saved, deliberately: everything that can throw
        // during setup must throw while there is still nothing to clean up. A leaked nonce memory
        // corrupts every later cert's injected index, so the window between the save and the archiving
        // `finally` is kept as narrow as it can be.
        using var proxy = WireMockServer.Start();

        // Ordering matters: the specific mapping is registered first and at a higher priority, so the
        // catch-all proxy cannot swallow the one route this test exists to fail.
        proxy.Given(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost())
             .AtPriority(1)
             .RespondWith(Response.Create().WithStatusCode(400)
                                           .WithBody("""{"error":"ai1463_cert_forced_session_start_failure"}"""));
        proxy.Given(Request.Create().WithPath("/*").UsingAnyMethod())
             .AtPriority(100)
             .RespondWith(Response.Create().WithProxy(upstream));

        var viaProxy = new Dictionary<string, string> { ["KCAP_URL"] = proxy.Url! };

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "gemini", ["--version"]);

            // (2) The hook link, proven directly: same binary, same proxy, a real failed POST.
            var (hookExit, hookStdout) = await RunSessionStartHookAsync(worktree.FullName, viaProxy);

            await Assert.That(hookExit).IsNotEqualTo(0);
            await Assert.That(AdditionalContextOf(hookStdout)).IsNotNull();

            var failedPostsBefore = FailedSessionStartPosts(proxy);

            var (exitCode, answer) = await RunGeminiAsync(
                worktree.FullName, MemoryIndexLiveCertHarness.PositivePrompt, viaProxy);

            // (1) The turn's own hook really did hit the failing route — otherwise this degenerates
            // into the plain positive case with extra machinery.
            await Assert.That(FailedSessionStartPosts(proxy)).IsGreaterThan(failedPostsBefore);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(answer).Contains(nonce);
        } finally {
            TryDelete(worktree);
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    /// <summary>Drives `kcap hook --gemini` with a minimal SessionStart payload, returning its exit code
    /// and stdout. The session id is a fresh GUID so the run cannot collide with a real session — and,
    /// because the POST is failed by the proxy, nothing is created on the server either.
    ///
    /// <para>It must be a BARE, parseable GUID. The dispatcher validates <c>session_id</c> with
    /// <c>Guid.TryParse</c> and a failure takes the same emit-and-return-0 path as a suppressed session,
    /// so a decorated id (<c>cert-{guid}</c>) silently produces a passing-looking exit 0 with the allow
    /// object and NO HTTP traffic at all — which is exactly what this test would otherwise be measuring
    /// instead of the failed POST.</para></summary>
    static async Task<(int ExitCode, string Stdout)> RunSessionStartHookAsync(
            string cwd, IReadOnlyDictionary<string, string> environment) {
        var payload = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = Guid.NewGuid().ToString(),
            ["cwd"]             = cwd,
            ["source"]          = "startup"
        }.ToJsonString();

        var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            "kcap", ["hook", "--gemini"], cwd, stdin: payload, environment: environment);

        await Console.Out.WriteLineAsync(
            $"[{VendorLabel}-memory-live] hook exit={exitCode} stdout={stdout} stderr={stderr}");

        return (exitCode, stdout);
    }

    /// <summary>The injected text, or null when the payload carries none — this is what Gemini's runner
    /// parses, so parsing it the same way is the assertion that the bytes were usable.</summary>
    static string? AdditionalContextOf(string stdout) {
        try {
            return JsonNode.Parse(stdout.Trim())?["hookSpecificOutput"]?["additionalContext"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    static int FailedSessionStartPosts(WireMockServer proxy) =>
        proxy.LogEntries.Count(e =>
            e.RequestMessage.Path == "/hooks/session-start/gemini" && e.ResponseMessage.StatusCode is 400);

    /// <summary>Runs one non-interactive Gemini turn. <c>--approval-mode plan</c> keeps it read-only so
    /// an unexpected tool request cannot stall the cert.
    ///
    /// <para><c>--skip-trust</c> is REQUIRED, not hygiene. Every cert runs in a freshly created
    /// throwaway worktree, which is by definition not in the user's trusted-folders list, and 0.53.0
    /// refuses a headless turn outright in an untrusted directory — <c>exit 55</c>, before any model
    /// call. Without it the positive case fails on the trust gate and, worse, the negative control
    /// passes vacuously (see its exit-code assertion).</para></summary>
    static async Task<(int ExitCode, string Answer)> RunGeminiAsync(
            string cwd, string prompt, IReadOnlyDictionary<string, string>? environment = null) {
        var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            "gemini", ["--skip-trust", "--approval-mode", "plan", "--prompt", prompt], cwd,
            environment: environment);

        await Console.Out.WriteLineAsync($"[{VendorLabel}-memory-live] gemini exit={exitCode} stderr={stderr}");

        return (exitCode, MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout));
    }

    static void TryDelete(DirectoryInfo dir) {
        try { dir.Delete(recursive: true); } catch { /* best-effort */ }
    }
}
