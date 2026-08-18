using Capacitor.Cli.Tests.Unit.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.Harness.Pi;

/// <summary>
/// Env-gated certification that the SessionStart memory index actually reaches the model on Pi
/// (badlogic/pi-mono). The raw-stdout contract (<c>PiHookCommand.RenderMemoryOutput</c>, the
/// <c>&lt;!-- kcap-memory-index:v1 --&gt;</c> marker) and the extension's static shape are covered
/// against fakes elsewhere in this suite; this is the only place asserting the end-to-end claim: that
/// a real <c>pi</c> process, running the installed <c>kcap.ts</c> extension, actually puts the fragment
/// in front of the model.
///
/// <para><b>Why this matters for Pi specifically.</b> Delivery is entirely owned by Pi's own extension
/// runtime, not by kcap: <c>kcap.ts</c> caches the fragment on <c>session_start</c> and splices it into
/// <c>_event.systemPrompt</c> from <c>pi.on("before_agent_start", …)</c>, keyed on
/// <c>ctx.sessionManager.getSessionFile()</c> matching the cached session file. A renamed lifecycle
/// event, a <c>before_agent_start</c> payload that stops carrying <c>systemPrompt</c>, or a
/// <c>getSessionFile()</c> that returns something else on a future Pi build would silently stop
/// delivery while every unit test in this suite still passed — those tests assert the bytes kcap
/// writes and the text of the extension we generate, never what Pi's runtime does with either. Cursor
/// is the standing counterexample in this epic: byte-perfect output, model receipt not guaranteed.
/// Nothing except this test would notice.</para>
///
/// <para><b>Session identity is the session FILE PATH</b> (<c>PiHookCommand.ExtractSessionId</c>): the
/// header's uuid when the session's first line has been flushed, else the uuid suffix of the
/// <c>&lt;timestamp&gt;_&lt;uuid&gt;.jsonl</c> filename. The readiness probe below constructs throwaway
/// filenames of that exact shape for the same reason the OpenCode cert mints throwaway session ids —
/// each probe must resolve to an identity kcap has never seen, so it spends its own once-per-session
/// lease rather than the cert's.</para>
///
/// <para><b>No <c>VerifiedAgainstVersion</c> baseline yet.</b> Unlike the OpenCode cert, this file has
/// never been run against a live <c>pi</c> + installed extension — the substitution work that produced
/// it was done by reading <c>PiExtensionInstaller.ExtensionContent</c> and <c>PiHookCommand</c>, not by
/// observing a real turn. Recording an unverified version string here would be worse than recording
/// none: a future failure would be misread as "the vendor moved from a known-good baseline" when no
/// baseline was ever established. The first live run must fill in <see cref="VerifiedAgainstVersion"/>
/// with the observed <c>pi --version</c> output (see <c>RecordCertEnvironmentAsync</c>'s console line)
/// before this comment can make the same claim OpenCode's does.</para>
///
/// <para><b>The <c>-p</c> stdout-shape assumption is GUARDED, not just disclosed.</b> The positive case
/// asserts on <c>stdout.Trim()</c> directly on the assumption that print mode writes ONLY the
/// assistant's final text — unlike OpenCode, which structurally defends that claim by parsing just the
/// <c>type: "text"</c> NDJSON frames. Because that structural defense has no Pi equivalent, the positive
/// case instead asserts, BEFORE the nonce assertion, that raw stdout does NOT contain
/// <see cref="MemoryIndexEmitter.FragmentMarker"/>: if a future <c>pi</c> build ever echoed the injected
/// system prompt, the marker would ride along with the nonce and turn what would otherwise be a silent
/// vacuous pass (the negative control cannot catch this — it has no fragment to echo) into a loud,
/// diagnosable failure. That guard is necessarily partial — a reformatted echo that drops the literal
/// marker text would not trip it — so the deferred first live run must still eyeball raw stdout by eye,
/// not just trust the guard.</para>
///
/// <para><b>Known flakiness, and which kind it is.</b> Per the shared harness's <c>PositivePrompt</c>,
/// the positive case asks the model to echo 32 random hex characters; a small/free model can
/// mistranscribe them. That is a model-capability flake, not a delivery defect — see
/// <see cref="ModelEnvVar"/> below. Do NOT weaken the assertion to a fuzzy match: an exact echo is the
/// whole proof that the fragment, and not some other source, produced the answer.</para>
///
/// <para>Both tests are <c>[NotInParallel]</c>: the negative control mutates the REAL process-global
/// <c>disable_memory_index</c> config. <c>[NotInParallel]</c> only prevents concurrency — it restores
/// nothing — so every test snapshots before creating anything and restores in a <c>finally</c>,
/// including the unset state.</para>
/// </summary>
public class PiMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_PI_MEMORY_LIVE";
    const string VendorLabel    = "pi";

    /// <summary>
    /// The <c>pi --version</c> output this cert was last verified against, or "pending" if it never
    /// has been. See the class doc's "No VerifiedAgainstVersion baseline yet" section — fill this in
    /// on the first live pass, from the <c>[pi-memory-live] pi --version</c> line
    /// <c>RecordCertEnvironmentAsync</c> writes to stdout.
    /// </summary>
    internal const string VerifiedAgainstVersion = "pending";

    /// <summary>
    /// Optional model override, passed as <c>--model &lt;value&gt;</c>. Deliberately not hard-coded for
    /// the same reason as the OpenCode cert's equivalent: an account's configured default model can be
    /// unavailable (expired credit, retired model) in a way that fails the run on the exit code rather
    /// than on the nonce, which is a credential condition unrelated to what this certifies. Point this
    /// at a more capable model if the positive case fails on a near-miss of the nonce rather than
    /// weakening the assertion.
    /// </summary>
    const string ModelEnvVar = "KCAP_PI_MEMORY_LIVE_MODEL";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "one real `pi -p` turn per test",
        // Stated explicitly because WITHOUT THE EXTENSION THE NEGATIVE CONTROL PASSES VACUOUSLY: with
        // no extension there is no injection at all, so "the nonce did not appear" is guaranteed and
        // proves nothing about the opt-out. The positive case is what would fail, but a reader seeing
        // one pass and one fail should know which precondition to check first.
        "`pi` on PATH, authenticated for a provider, AND the kcap extension installed at "
      + "~/.pi/agent/extensions/kcap.ts (`kcap plugin install --pi`) — without the extension the "
      + "negative control passes vacuously. The extension shells out to `kcap` FROM PATH, so that "
      + "`kcap` must be the same build as the extension: a released `kcap` emits no fragment at all "
      + $"and the positive case fails. Set {ModelEnvVar}=<value> if the account's default model is "
      + "unavailable");

    /// <summary>
    /// Runs one non-interactive <c>pi -p &lt;prompt&gt;</c> turn in a fresh directory.
    ///
    /// <para>No <c>--continue</c>/<c>--resume</c>/<c>--session</c>: every cert run must start a
    /// brand-new session, or a prior turn's context could carry the nonce and the assertion would
    /// prove nothing.</para>
    ///
    /// <para><b><c>--no-extensions</c> must NEVER be passed here.</b> It would disable the kcap
    /// extension under test — the exact inverse of OpenCode's <c>--pure</c> warning — and the run would
    /// succeed with the nonce never appearing, presenting as a code defect. <b><c>--no-session</c> must
    /// NEVER be passed either</b>: no session file means <c>PiHookCommand.Handle</c> returns before
    /// recording anything (<c>if (string.IsNullOrWhiteSpace(file)) return 0;</c>), so there is no hook
    /// and the negative control would again pass vacuously.</para>
    ///
    /// <para><see cref="MemoryIndexLiveCertHarness.ResolveOnPath(string)"/> is called explicitly for the same
    /// reason as the OpenCode cert: <c>Process.Start</c> tries a bare filename against the working
    /// directory before consulting PATH, and the working directory here is a throwaway cert
    /// worktree.</para>
    /// </summary>
    static Task<(int ExitCode, string Stdout, string Stderr)> RunPiAsync(string cwd, string prompt) {
        string[] args = ["-p"];
        if (Environment.GetEnvironmentVariable(ModelEnvVar) is { Length: > 0 } model)
            args = [.. args, "--model", model];

        return MemoryIndexLiveCertHarness.RunProcessAsync(
            MemoryIndexLiveCertHarness.ResolveOnPath("pi"), [.. args, prompt], cwd);
    }

    /// <summary>
    /// Waits until the memory index actually CARRIES the nonce, by running the same
    /// <c>kcap hook --pi … --memory-contract 1</c> the extension runs, against throwaway session
    /// identities.
    ///
    /// <para><b>Why this is a precondition and not a weakening.</b> Same race as every other harness in
    /// this epic: a memory saved a moment ago is not instantly in <c>GET /api/memories/index</c>, and
    /// the cert's save→run sequence is tight enough to lose that window. Left unguarded, the cert
    /// reports a propagation race as a delivery defect. The assertion itself is untouched — the pass
    /// condition is still that the MODEL reproduced the nonce; this only establishes that there was
    /// something to reproduce, and costs no model tokens.</para>
    ///
    /// <para>Each probe target's filename follows Pi's own <c>&lt;timestamp&gt;_&lt;uuid&gt;.jsonl</c>
    /// shape so <c>PiHookCommand.ExtractSessionId</c> derives a fresh, never-before-seen session id from
    /// it — the file itself need not exist on disk: with no header to read, the command falls back to
    /// the filename's uuid suffix, which is exactly the "session-start fires before the header line is
    /// flushed" path the fallback exists for. Each probe therefore burns a throwaway identity's
    /// injection lease, never the cert's own.</para>
    /// </summary>
    static async Task<bool> WaitForNonceInIndexAsync(string nonce, string cwd) {
        var kcap = MemoryIndexLiveCertHarness.ResolveOnPath("kcap");

        for (var attempt = 1; attempt <= 10; attempt++) {
            var probeFile = Path.Combine(cwd, $"20260101T000000_{Guid.NewGuid()}.jsonl");

            var (_, stdout, _) = await MemoryIndexLiveCertHarness.RunProcessAsync(
                kcap,
                ["hook", "--pi", "--event", "session-start",
                 "--file", probeFile, "--cwd", cwd, "--memory-contract", "1"],
                cwd);

            if (stdout.Contains(nonce, StringComparison.Ordinal)) {
                Console.WriteLine($"[cert] index carried the nonce after {attempt} readiness probe(s)");
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_pi_turn() {
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
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "pi", ["--version"]);

            // Establish that there IS something for the model to reproduce before spending a turn.
            await Assert.That(await WaitForNonceInIndexAsync(nonce, worktree.Path))
                .IsTrue()
                .Because("the nonce memory never became visible in GET /api/memories/index, so a model "
                       + "turn could not prove anything either way — this is a save/propagation "
                       + "problem, NOT evidence about the extension's delivery");

            var (exitCode, stdout, _) =
                await RunPiAsync(worktree.Path, MemoryIndexLiveCertHarness.PositivePrompt);

            LogInjectionEvidence(nonce, stdout);

            await Assert.That(exitCode).IsEqualTo(0);

            // Structural echo guard, BEFORE the nonce assertion. -p mode is ASSUMED (not yet verified
            // against a live run — see the class doc) to write only the assistant's final text, with no
            // echo of the request or the chained system prompt. If a future pi build ever echoes any
            // part of the injected prompt, the nonce would ride along WITHOUT the model having produced
            // it — a vacuous pass invisible to the nonce assertion alone, because the negative control
            // has no fragment at all to echo and so cannot catch it either. The marker is never something
            // the model is asked to reproduce (PositivePrompt asks only for the nonce), and ordinary
            // extension/plugin startup noise does not contain it, so this cannot trip on a true pass —
            // only on the exact failure mode it exists to convert from a silent false-positive into a
            // loud one.
            await Assert.That(stdout).DoesNotContain(MemoryIndexEmitter.FragmentMarker)
                .Because("the injected-fragment marker appeared in raw pi stdout, which means pi echoed "
                       + "the injected system prompt back — the nonce's presence below proves nothing "
                       + "about model receipt. Fix the stdout extraction (this cert currently assumes -p "
                       + "mode emits ONLY the assistant's final text) before trusting this cert again");

            // -p (print) mode writes ONLY the assistant's final text to stdout — no echo of the request
            // or the chained system prompt — so the stream IS the model's answer; no NDJSON/frame
            // extraction is needed or safe to add (it would risk matching an echoed request instead of
            // what the model said). That assumption is now GUARDED by the assertion above rather than
            // merely disclosed — but the deferred first live run must still eyeball raw stdout, since a
            // partial or reformatted echo that happens not to carry the literal marker text is not
            // something the guard can catch.
            var said = stdout.Trim();

            // The model's own words go in the failure message, because the two ways this can fail look
            // identical from the assertion alone and lead to opposite responses. "NONE" means the
            // fragment did not reach the model — a real delivery defect, go read before_agent_start. A
            // near-miss of the nonce means the fragment DID reach it and a weak model mistranscribed 32
            // random hex characters — nothing to fix in kcap; point the model override at something
            // more capable. Diagnosing the second as the first is an expensive mistake.
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
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_pi_turn() {
        Gate();

        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "pi", ["--version"]);

        // Snapshot before anything is created — this throws on an unreadable config, and doing it after
        // the save would strand a real memory outside the archive-protecting try below.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var (exitCode, stdout, _) =
                await RunPiAsync(worktree.Path, MemoryIndexLiveCertHarness.NegativePrompt);

            LogInjectionEvidence(nonce, stdout);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(stdout.Trim()).DoesNotContain(nonce);
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
    /// reached the process (kcap, auth, scope, or the extension not shelling out) versus it reached the
    /// process and the model did not use it (before_agent_start not splicing it into the system
    /// prompt). Asserting on it would weaken the cert to "the harness ingested it", which is exactly the
    /// standard that let earlier adapters merge without proof the MODEL received anything.
    ///
    /// <para>Also logs whether <see cref="MemoryIndexEmitter.FragmentMarker"/> itself shows up in raw
    /// stdout — purely informational here (the hard guard against an echo is the assertion in the
    /// positive case below); kept beside the nonce line so both diagnostics land together in the test
    /// log.</para>
    /// </summary>
    static void LogInjectionEvidence(string nonce, string stdout) {
        Console.WriteLine(
            $"[cert] nonce present anywhere in the raw stdout (NOT the pass condition): {stdout.Contains(nonce)}");
        Console.WriteLine(
            $"[cert] injected-fragment marker present in raw stdout (structural echo guard): "
          + $"{stdout.Contains(MemoryIndexEmitter.FragmentMarker)}");
    }

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
