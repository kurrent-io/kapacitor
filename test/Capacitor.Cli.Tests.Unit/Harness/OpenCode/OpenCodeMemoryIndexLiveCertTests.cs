using System.Text.Json.Nodes;
using Capacitor.Cli.Tests.Unit.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

/// <summary>
/// Env-gated certification that the SessionStart memory index actually reaches the model on SST
/// OpenCode. The raw-stdout contract and the plugin's static shape are covered against fakes by
/// <c>OpenCodeSessionStartMemoryTests</c>; this is the only place asserting the end-to-end claim.
///
/// <para><b>This cert matters more here than for any other harness in the epic.</b> Injection rides
/// <c>experimental.chat.system.transform</c> — an API OpenCode names EXPERIMENTAL. An upstream rename,
/// a changed payload shape, or a dropped trigger would stop delivering the fragment to the model while
/// every unit test in the suite still passed, because those tests assert the bytes kcap writes and the
/// text of the plugin we generate, not what OpenCode does with either. Cursor is the standing
/// counterexample: byte-perfect output, model receipt not guaranteed. Nothing except this test would
/// notice.</para>
///
/// <para><b>Verified experimental-API baseline: opencode 1.18.9.</b> That is the build against which
/// the system-transform contract was measured
/// (<c>docs/probes/2026-08-07-opencode-acp/</c> established the ACP surface; the transform contract
/// itself was read out of the 1.18.9 binary and is recorded here): the hook is triggered from
/// <c>LLMRequestPrep.prepare</c> once per LLM request with
/// <c>({sessionID, model}, {system: string[]})</c>, the array arrives holding ONE pre-joined element,
/// mutations to it are honoured, and the same hook name is separately triggered by the agent-config
/// generator with NO <c>sessionID</c>. A future build that breaks any of those is what this cert is
/// for — if it fails, re-read that contract before assuming a kcap defect.</para>
///
/// <para><b>Known flakiness, and which kind it is.</b> The positive case asks the model to echo 32
/// RANDOM hex characters, and a small free model mistranscribes them often enough to fail the run —
/// observed twice while certifying, both times with the fragment demonstrably delivered (the readiness
/// probe confirmed the index carried the nonce, and an isolated turn on a patterned nonce reproduced it
/// exactly). That is a model-capability flake, not a delivery defect, which is why the assertion puts
/// the model's own answer in the failure message: <c>NONE</c> means go read the transform, a near-miss
/// of the nonce means point <see cref="ModelEnvVar"/> at a more capable model. Do NOT weaken the
/// assertion to a fuzzy match — an exact echo is the whole proof.</para>
///
/// <para>Both tests are <c>[NotInParallel]</c>: the negative control mutates the REAL process-global
/// <c>disable_memory_index</c> config. <c>[NotInParallel]</c> only prevents concurrency — it restores
/// nothing — so every test snapshots before creating anything and restores in a <c>finally</c>,
/// including the unset state.</para>
/// </summary>
public class OpenCodeMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_OPENCODE_MEMORY_LIVE";
    const string VendorLabel    = "opencode";

    /// <summary>
    /// The build the transform contract above was verified against. Recorded in-code, not just in the
    /// probe notes, so a future failure is diagnosable as "the vendor moved" rather than investigated as
    /// a kcap regression.
    /// </summary>
    internal const string VerifiedAgainstVersion = "1.18.9";

    /// <summary>
    /// Optional model override, as <c>provider/model</c>.
    ///
    /// <para><b>Why this exists rather than just using the account default.</b> On the first real run of
    /// this cert the account's configured default (<c>opencode/big-pickle</c>) answered
    /// <c>[401] Provider returned error</c> — <c>opencode run</c> exited 1 before any model call
    /// completed, so the cert failed on the EXIT CODE and not on the nonce. That is a credential/credit
    /// condition on one model, entirely unrelated to what this certifies, and without an override the
    /// cert is simply unrunnable on such an account. Deliberately NOT a hard-coded model: pinning one
    /// here would rot the moment OpenCode Zen retires it, and the account default is the right thing to
    /// exercise when it works.</para>
    /// </summary>
    const string ModelEnvVar = "KCAP_OPENCODE_MEMORY_LIVE_MODEL";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "one real `opencode run` turn per test",
        // The plugin-installed precondition is stated because WITHOUT IT THE NEGATIVE CONTROL PASSES
        // VACUOUSLY: with no plugin there is no injection at all, so "the nonce did not appear" is
        // guaranteed and proves nothing about the opt-out. The positive case is what would fail, but a
        // reader seeing one pass and one fail should know which precondition to check first.
        $"`opencode` (verified against {VerifiedAgainstVersion}) on PATH, authenticated "
      + "(`opencode auth login`), AND the kcap plugin installed at "
      + "~/.config/opencode/plugins/kcap.ts (`kcap plugin install --opencode`) — without the plugin the "
      + "negative control passes vacuously. The plugin shells out to `kcap` FROM PATH, so that `kcap` "
      + "must be the same build as the plugin: a released `kcap` emits no fragment at all and the "
      + $"positive case fails. Set {ModelEnvVar}=provider/model if the account's default model is "
      + "unavailable");

    /// <summary>
    /// Runs one non-interactive <c>opencode run</c> turn in a fresh directory.
    ///
    /// <para>No <c>--continue</c>/<c>--session</c>: every cert run must start a brand-new session, or a
    /// prior turn's context could carry the nonce and the assertion would prove nothing.</para>
    ///
    /// <para><b><c>--pure</c> must NEVER be passed here.</b> It disables external plugins, which is
    /// exactly the kcap plugin this cert exists to exercise — the run would succeed and the nonce would
    /// never appear, presenting as a code defect. (It is also the flag the DAEMON passes deliberately,
    /// for the opposite reason: to keep a hosted agent from being captured twice.)</para>
    ///
    /// <para><see cref="MemoryIndexLiveCertHarness.ResolveOnPath(string)"/> is called explicitly rather than left
    /// to <c>RunProcessAsync</c>'s own resolution, because <c>Process.Start</c> tries a bare filename
    /// against the working directory FIRST — and the working directory here is a throwaway cert
    /// worktree. Resolving up front pins the exact binary this cert reports on.</para>
    /// </summary>
    static Task<(int ExitCode, string Stdout, string Stderr)> RunOpenCodeAsync(string cwd, string prompt) {
        string[] args = ["run", "--format", "json"];

        if (Environment.GetEnvironmentVariable(ModelEnvVar) is { Length: > 0 } model)
            args = [.. args, "-m", model];

        return MemoryIndexLiveCertHarness.RunProcessAsync(
            MemoryIndexLiveCertHarness.ResolveOnPath("opencode"), [.. args, prompt], cwd);
    }

    /// <summary>
    /// Waits until the memory index actually CARRIES the nonce, by running the same
    /// <c>kcap hook --opencode … --memory-contract 1</c> the plugin runs, against throwaway session ids.
    ///
    /// <para><b>Why this is a precondition and not a weakening.</b> A memory saved a moment ago is not
    /// instantly in <c>GET /api/memories/index</c>, and the cert's save→run sequence is tight enough to
    /// lose that window: observed as an intermittent positive-case failure where the model answered
    /// <c>NONE</c> while injection was demonstrably working. Left unguarded, the cert reports a
    /// propagation race as a delivery defect — the most expensive kind of false failure, because the
    /// honest response to it looks like debugging code that is correct.</para>
    ///
    /// <para>The assertion itself is untouched: the pass condition is still that the MODEL reproduced
    /// the nonce. This only establishes that there was something to reproduce. It deliberately probes
    /// through the CLI path the plugin uses rather than a bespoke HTTP call, so a "ready" verdict means
    /// ready <i>for the mechanism under test</i> — and it costs no model tokens.</para>
    ///
    /// <para>Each probe burns a throwaway session's injection lease, which is why the ids are random and
    /// never the cert's own.</para>
    /// </summary>
    static async Task<bool> WaitForNonceInIndexAsync(string nonce, string cwd) {
        var kcap = MemoryIndexLiveCertHarness.ResolveOnPath("kcap");
        var transcript = Path.Combine(cwd, "kcap-cert-readiness.jsonl");

        for (var attempt = 1; attempt <= 10; attempt++) {
            var sessionId = "sesready" + Guid.NewGuid().ToString("N")[..12];

            var (_, stdout, _) = await MemoryIndexLiveCertHarness.RunProcessAsync(
                kcap,
                ["hook", "--opencode", "--event", "session-start",
                 "--session", sessionId, "--file", transcript, "--cwd", cwd,
                 "--memory-contract", "1"],
                cwd);

            if (stdout.Contains(nonce, StringComparison.Ordinal)) {
                Console.WriteLine($"[cert] index carried the nonce after {attempt} readiness probe(s)");
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }

    /// <summary>
    /// The model's own words, and ONLY those: the concatenated <c>text</c> of every <c>type: "text"</c>
    /// event in OpenCode's NDJSON stream.
    ///
    /// <para><b>Not <see cref="MemoryIndexLiveCertHarness.ExtractAssistantAnswer"/>.</b> That helper
    /// returns the first <c>text</c>-ish field it finds anywhere in a frame, which on a stream that ever
    /// echoed the request — the injected system prompt included — would return text CONTAINING the nonce
    /// without the model having produced it. The whole cert rests on the nonce being something only the
    /// model could have said, so the extraction has to be specific rather than convenient. Measured
    /// shape (opencode 1.18.9): <c>{"type":"text","sessionID":"…","part":{"type":"text","text":"…"}}</c>,
    /// with the user's message absent from the stream.</para>
    /// </summary>
    internal static string ExtractModelText(string stdout) {
        var said = new System.Text.StringBuilder();

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (line.Length == 0 || line[0] != '{') continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch { continue; }

            if (node is not JsonObject frame) continue;
            if (frame["type"] is not JsonValue t || !t.TryGetValue<string>(out var type) || type != "text") continue;
            if (frame["part"] is not JsonObject part) continue;
            if (part["text"] is JsonValue v && v.TryGetValue<string>(out var text)) said.Append(text);
        }

        return said.ToString();
    }

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_opencode_turn() {
        Gate();

        // Read BEFORE anything is created: a leaked `true` from an earlier failed run would make this
        // positive case pass vacuously, so the opt-out state is asserted rather than assumed.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();
        await Assert.That(original is true).IsFalse();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            // Records the exact binary version. A cert passing against an unknown build is how earlier
            // memory-cert failures were misdiagnosed as code defects when the real cause was a stale
            // installed binary.
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "opencode", ["--version"]);

            // Establish that there IS something for the model to reproduce before spending a turn.
            // Without this the cert intermittently reports an index-propagation race as a delivery
            // defect — observed, and the most expensive kind of false failure.
            await Assert.That(await WaitForNonceInIndexAsync(nonce, worktree.Path))
                .IsTrue()
                .Because("the nonce memory never became visible in GET /api/memories/index, so a model "
                       + "turn could not prove anything either way — this is a save/propagation "
                       + "problem, NOT evidence about the system transform");

            var (exitCode, stdout, _) =
                await RunOpenCodeAsync(worktree.Path, MemoryIndexLiveCertHarness.PositivePrompt);

            LogInjectionEvidence(nonce, stdout);

            await Assert.That(exitCode).IsEqualTo(0);

            var said = ExtractModelText(stdout);

            // The model's own words go in the failure message, because the two ways this can fail look
            // identical from the assertion alone and lead to opposite responses. "NONE" means the
            // fragment did not reach the model — a real delivery defect, go read the transform. A
            // near-miss of the nonce means the fragment DID reach it and a weak model mistranscribed 32
            // random hex characters — nothing to fix in kcap; point the model override at something more
            // capable. Diagnosing the second as the first is an expensive mistake.
            await Assert.That(said).Contains(nonce)
                .Because($"the model was asked to echo the injected nonce and answered: '{said}'. "
                       + "'NONE' (or no mention) means the fragment never reached the model; a near-miss "
                       + "of the nonce means it did, and the model mistranscribed it — see "
                       + $"{ModelEnvVar}");
        } finally {
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_opencode_turn() {
        Gate();

        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "opencode", ["--version"]);

        // Snapshot before anything is created — this throws on an unreadable config, and doing it after
        // the save would strand a real memory outside the archive-protecting try below.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var (exitCode, stdout, _) =
                await RunOpenCodeAsync(worktree.Path, MemoryIndexLiveCertHarness.NegativePrompt);

            LogInjectionEvidence(nonce, stdout);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(ExtractModelText(stdout)).DoesNotContain(nonce);
        } finally {
            // Nested: the restore THROWS on a failed or unconfirmed write, and that must not skip the
            // archive — a leaked nonce corrupts every later cert's injected index.
            try {
                await MemoryIndexLiveCertHarness.RestoreDisableMemoryIndexAsync(original);
            } finally {
                await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
            }
        }
    }

    /// <summary>
    /// Diagnostic ONLY — logged unconditionally, on pass and fail alike, and never asserted on. It
    /// separates the two ways this cert can fail, which otherwise look identical: the nonce never
    /// reached the process (kcap, auth, scope, or the plugin not reading stdout) versus it reached the
    /// process and the model did not use it (the transform not delivering). Asserting on it would weaken
    /// the cert to "the harness ingested it", which is exactly the standard that let three earlier
    /// adapters merge without proof the MODEL received anything.
    /// </summary>
    static void LogInjectionEvidence(string nonce, string stdout) =>
        Console.WriteLine(
            $"[cert] nonce present anywhere in the raw stream (NOT the pass condition): {stdout.Contains(nonce)}");

    /// <summary>Saves the nonce memory, deleting the worktree if the save throws — the memory is the
    /// expensive thing to leak, but a save failure must not also strand a temp directory.</summary>
    static async Task<string> SaveNonceOrCleanUpAsync(string nonce, TempDir worktree) {
        try {
            return await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);
        } catch {
            worktree.Dispose();
            throw;
        }
    }
}
