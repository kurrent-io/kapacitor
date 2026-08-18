using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Capacitor.Cli.Tests.Unit.SessionStartMemory;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Gemini;

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

    const string SessionStartPath     = "/hooks/session-start/gemini";
    const string ForcedFailureMarker  = "kcap_cert_forced_session_start_failure";

    /// <summary>What the poster's failed-POST branch writes to stderr — <c>[kcap] {agentTag}
    /// {endpoint}: HTTP {code}</c>. Matched on the route-and-status tail rather than the whole line, so
    /// the cert does not break on an agent-tag rename, but still cannot be satisfied by a failure on
    /// some other route.</summary>
    const string FailedPostDiagnostic = "session-start/gemini: HTTP 400";

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
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

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
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

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
    /// <para><b>The claim is about ONE invocation, so every link is measured on that one invocation.</b>
    /// A recording shim named <c>kcap</c> is prepended to the PATH Gemini inherits, capturing each hook
    /// invocation's exit code, stdout and stderr. The test picks the invocation whose
    /// <c>additionalContext</c> carried the nonce — the hook Gemini actually consumed — and asserts, of
    /// that same invocation, that it exited non-zero and that its own stderr carries the failed-POST
    /// diagnostic for the session-start route.</para>
    ///
    /// <para>Three weaker versions were tried and each was holed in review; they are worth naming
    /// because each looks sufficient. (1) Proving the non-zero exit from a SEPARATE direct
    /// <c>kcap hook --gemini</c> run is a substitution: Gemini's own hook could have returned 0 through
    /// a session- or source-dependent branch. (2) "Some forced 400 happened during the turn" is
    /// turn-global: one invocation could carry the nonce while a DIFFERENT one took the 400. (3)
    /// Correlating those two on session id narrows it but does not close it, because a session id is
    /// reused across invocations — <c>startup</c> and <c>resume</c> share one. Only per-invocation
    /// evidence closes it.</para>
    /// </summary>
    [Test, NotInParallel]
    [UnsupportedOSPlatform("windows")]
    public async Task Failed_session_start_post_still_delivers_the_index_to_a_real_gemini_session() {
        Gate();
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The recorder shim below is a POSIX executable shell script.");

        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();
        await Assert.That(original is true).IsFalse();

        var upstream = await MemoryIndexLiveCertHarness.InitializeAndResolveServerUrlAsync();

        // Proxy and shim are stood up BEFORE the nonce memory is saved, deliberately: everything that
        // can throw during setup should throw while there is still nothing to clean up. A leaked nonce
        // memory corrupts every later cert's injected index.
        using var proxy = WireMockServer.Start();
        using var hooks = new HookRecorder();

        // Ordering matters: the specific mapping is registered first and at a higher priority, so the
        // catch-all proxy cannot swallow the one route this test exists to fail.
        proxy.Given(Request.Create().WithPath(SessionStartPath).UsingPost())
             .AtPriority(1)
             .RespondWith(Response.Create().WithStatusCode(400).WithBody($$"""{"error":"{{ForcedFailureMarker}}"}"""));
        proxy.Given(Request.Create().WithPath("/*").UsingAnyMethod())
             .AtPriority(100)
             .RespondWith(Response.Create().WithProxy(upstream));

        var viaProxy = new Dictionary<string, string> {
            ["KCAP_URL"] = proxy.Url!,
            ["PATH"]     = hooks.PrependedTo(Environment.GetEnvironmentVariable("PATH"))
        };

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "gemini", ["--version"]);

            var (exitCode, answer) = await RunGeminiAsync(
                worktree.FullName, MemoryIndexLiveCertHarness.PositivePrompt, viaProxy);

            // The invocations that delivered the index, identified by the nonce in the context Gemini
            // itself was handed. There is no substitution here: these ARE hooks Gemini ran.
            var delivering = hooks.Invocations
                .Where(i => AdditionalContextOf(i.Stdout)?.Contains(nonce) == true)
                .ToList();

            await Assert.That(delivering).IsNotEmpty();

            // Non-zero exit, per delivering invocation. `ExitCode` is nullable ON PURPOSE: an
            // unreadable or half-written exit record must FAIL this, not satisfy it. An earlier
            // version mapped a parse failure to int.MinValue, which is != 0 and therefore passed the
            // very property under test — a sentinel that satisfies the assertion is a false-pass path.
            await Assert.That(delivering.All(i => i.ExitCode is { } code && code != 0)).IsTrue();

            // ...and each of those invocations rejected its OWN POST. This is per-invocation evidence,
            // read from that invocation's own stderr — `[kcap] {agentTag} {endpoint}: HTTP {code}` is
            // written by the failed-POST branch of the poster itself.
            //
            // Two weaker versions were tried and both were holed in review. "Some forced 400 happened
            // during the turn" is turn-global: one invocation could carry the nonce while a DIFFERENT
            // one took the 400. Correlating on session id narrows that but does not close it, because a
            // session id is reused across invocations — `startup` and `resume` share one, which is
            // exactly the shape the integration test exercises.
            await Assert.That(delivering.All(i => i.Stderr.Contains(FailedPostDiagnostic))).IsTrue();

            // The proxy assertion is now doing one job only, and it is a job the stderr cannot do:
            // proving the 400 came from THIS test's mapping rather than from upstream via the
            // catch-all proxy. Both halves are needed — neither implies the other.
            await Assert.That(ForcedSessionStartFailures(proxy)).IsGreaterThan(0);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(answer).Contains(nonce);
        } finally {
            TryDelete(worktree);
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    /// <summary>Saves the nonce memory, deleting the worktree if the save throws — the memory is the
    /// expensive thing to leak, but a save failure must not also strand a temp directory.</summary>
    static async Task<string> SaveNonceOrCleanUpAsync(string nonce, DirectoryInfo worktree) {
        try {
            return await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);
        } catch {
            TryDelete(worktree);
            throw;
        }
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

    /// <summary>Counts only responses this test's own mapping produced, identified by its body marker.
    /// Counting "400 at that path" instead would also count an upstream 400 relayed by the catch-all
    /// proxy, and the point of this assertion is that the FORCED failure is what fired.</summary>
    static int ForcedSessionStartFailures(WireMockServer proxy) =>
        proxy.LogEntries.Count(e =>
            e.RequestMessage.Path == SessionStartPath
         && e.ResponseMessage.StatusCode is 400
         && e.ResponseMessage.BodyData?.BodyAsString?.Contains(ForcedFailureMarker) == true);

    /// <summary>
    /// A throwaway PATH entry holding a shim named <c>kcap</c> that runs the real one and records each
    /// invocation's exit code and stdout.
    ///
    /// <para><b>Only <c>kcap hook</c> is recorded; everything else is <c>exec</c>'d straight through.</b>
    /// That is not tidiness — recording buffers the child's stdout to a file and replays it on exit,
    /// and the same PATH entry is used by the four long-lived <c>kcap mcp</c> stdio servers Gemini
    /// launches from <c>~/.gemini/settings.json</c>. Buffering a stdio JSON-RPC server traps its
    /// handshake response until it exits, which it never does, so the turn stalls until the harness
    /// timeout. Measured: a recorder without this guard hung every run at exactly 120s, with four
    /// wrapped <c>kcap mcp …</c> processes alive and zero HTTP traffic.</para>
    ///
    /// <para>Buffering IS safe for the hook, and only because Gemini's runner collects stdout and parses
    /// on <c>close</c> rather than reading incrementally. Do not lift this shim to a harness that
    /// streams.</para>
    ///
    /// <para>The real binary is resolved from the UNMODIFIED PATH at construction time, so the shim
    /// cannot re-enter itself.</para>
    /// </summary>
    sealed class HookRecorder : IDisposable {
        readonly TempDir _root = new();
        readonly string _log;

        [UnsupportedOSPlatform("windows")]
        public HookRecorder() {
            _log = _root.CreateDir("log");

            var bin = _root.CreateDir("bin");
            BinDir  = bin;

            var real = MemoryIndexLiveCertHarness.ResolveOnPath("kcap");
            var shim = bin.CreateFile("kcap",
                $"""
                 #!/bin/sh
                 # Anything that is not a hook — notably the long-lived `kcap mcp <server>` stdio
                 # servers — must be completely transparent, so hand the process over rather than
                 # wrapping its streams.
                 if [ "$1" != "hook" ]; then exec "{real}" "$@"; fi

                 out="{_log}/$$.out"
                 err="{_log}/$$.err"
                 "{real}" "$@" > "$out" 2> "$err"
                 status=$?
                 cat "$out"
                 cat "$err" >&2
                 printf '%s' "$status" > "{_log}/$$.exit"
                 exit "$status"
                 """);
            File.SetUnixFileMode(shim,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string BinDir { get; }

        public string PrependedTo(string? path) =>
            string.IsNullOrEmpty(path) ? BinDir : $"{BinDir}{Path.PathSeparator}{path}";

        /// <summary>
        /// One entry per completed shim invocation. An invocation still in flight has no <c>.exit</c>
        /// file yet and is skipped, so a partially written set is never reported.
        ///
        /// <para><c>ExitCode</c> is nullable rather than defaulted: an unreadable or half-written record
        /// must fail an assertion, never satisfy one. A sentinel here (the first version used
        /// <c>int.MinValue</c>) is <c>!= 0</c> and would have passed the exact property the cert
        /// exists to check.</para>
        /// </summary>
        public IReadOnlyList<(int? ExitCode, string Stdout, string Stderr)> Invocations =>
            [.. Directory.EnumerateFiles(_log, "*.exit")
                .Select(exitFile => (
                    ExitCode: int.TryParse(File.ReadAllText(exitFile).Trim(), out var code) ? code : (int?)null,
                    Stdout:   ReadOrEmpty(Path.ChangeExtension(exitFile, ".out")),
                    Stderr:   ReadOrEmpty(Path.ChangeExtension(exitFile, ".err"))))];

        static string ReadOrEmpty(string path) {
            try { return File.ReadAllText(path); } catch { return ""; }
        }

        public void Dispose() => _root.Dispose();
    }

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
