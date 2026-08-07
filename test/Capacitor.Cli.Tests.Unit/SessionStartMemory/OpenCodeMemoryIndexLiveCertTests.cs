using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

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
    /// <para><see cref="MemoryIndexLiveCertHarness.ResolveOnPath"/> is called explicitly rather than left
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
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            // Records the exact binary version. A cert passing against an unknown build is how earlier
            // memory-cert failures were misdiagnosed as code defects when the real cause was a stale
            // installed binary.
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "opencode", ["--version"]);

            var (exitCode, stdout, _) =
                await RunOpenCodeAsync(worktree.FullName, MemoryIndexLiveCertHarness.PositivePrompt);

            LogInjectionEvidence(nonce, stdout);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(ExtractModelText(stdout)).Contains(nonce);
        } finally {
            TryDelete(worktree);
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
        var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var (exitCode, stdout, _) =
                await RunOpenCodeAsync(worktree.FullName, MemoryIndexLiveCertHarness.NegativePrompt);

            LogInjectionEvidence(nonce, stdout);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(ExtractModelText(stdout)).DoesNotContain(nonce);
        } finally {
            TryDelete(worktree);
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
    static async Task<string> SaveNonceOrCleanUpAsync(string nonce, DirectoryInfo worktree) {
        try {
            return await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);
        } catch {
            TryDelete(worktree);
            throw;
        }
    }

    static void TryDelete(DirectoryInfo dir) {
        try { dir.Delete(recursive: true); } catch { /* best-effort */ }
    }
}
