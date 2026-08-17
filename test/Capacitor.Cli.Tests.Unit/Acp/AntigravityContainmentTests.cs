// test/Capacitor.Cli.Tests.Unit/Acp/AntigravityContainmentTests.cs
using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// GATED enforcement of the Antigravity reviewer's containment, run against a REAL <c>agy</c>
/// process.
///
/// <para><b>Why a real process.</b> A model-layer refusal is not containment evidence: an agent that
/// declines to touch the operator's home looks identical to one that is structurally unable to. Every
/// assertion below is a filesystem or process-table observation made after a real turn, never a claim
/// read out of the agent's own words. This is the same rule <c>BorrowedReviewSandbox</c>'s
/// enforcement tests follow for Copilot, applied to the containment shape this vendor actually
/// uses — an isolated per-launch <c>HOME</c>, not an OS sandbox.</para>
///
/// <para><b>What it certifies.</b> The three properties the design's containment section rests on,
/// plus the probe finding that makes the whole shape sound:</para>
/// <para>1. Capture stays single-lane — no kcap hook fired for this conversation, so no second
/// watcher recorded it alongside the runtime's own NDJSON parse.</para>
/// <para>2. No watcher process was spawned for it.</para>
/// <para>3. <c>agy</c> derives <c>~/Library</c> from <c>$HOME</c>, so its Library writes land INSIDE
/// the per-launch home. This is the observation that makes "the reviewer's state is contained" a
/// measured fact rather than an assumption — if it were false, reviewer conversation state would
/// pollute the operator's own agy history and containment would be a fiction.</para>
/// <para>4. The operator's real agy conversation store gained no entry for this conversation.</para>
///
/// <para><b>Read-only against the operator's tree.</b> This test never creates, modifies or deletes
/// anything under the real <c>~/.config/kcap</c> or <c>~/.gemini</c>. It only reads them, and it keys
/// every observation on THIS run's conversation id so a concurrently-live daemon writing its own
/// files cannot make it pass or fail.</para>
///
/// <para><b>The positive control is deliberately not run.</b> The same turn under the operator's real
/// <c>HOME</c> DOES create a kcap log and a watcher — that was measured during the probe (agy's own
/// hook contract fires in print mode) and is written down in
/// <c>docs/probes/2026-08-06-agy-reviewer/findings.md</c>. Running it here would record a real session
/// against the operator's server and spawn a watcher over the reviewer's conversation, which is the
/// exact damage the containment exists to prevent — so the control stays a recorded measurement, not
/// a test case.</para>
///
/// <para><b>Gated</b> behind <c>KCAP_ANTIGRAVITY_REVIEWER_LIVE=1</c> AND a non-empty
/// <c>GOOGLE_CLOUD_PROJECT</c>: CI has neither an <c>agy</c> binary nor Google credentials, and the
/// case spends a real model turn. Both gates are evaluated before anything else, so a CI run costs a
/// skip.</para>
/// </summary>
public class AntigravityContainmentTests {
    const string GateEnvVar = "KCAP_ANTIGRAVITY_REVIEWER_LIVE";

    /// <summary>Bounded on purpose, and every wait in this test is bounded by it. A denial ENDS an agy
    /// turn — no closing <c>agent_response</c>, an empty <c>result.response</c>, and any second
    /// instruction in the prompt never runs — so a test that waits for post-denial output hangs rather
    /// than fails.</summary>
    static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// One real <c>agy</c> turn under the production per-launch home, then four observations of the
    /// operator's tree and this host's process table.
    /// </summary>
    [Test]
    public async Task A_real_turn_under_the_per_launch_home_leaves_the_operators_tree_untouched() {
        var project = Gate();

        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Antigravity reviewer is POSIX-only — its per-launch home cannot be created owner-only on Windows.");

        // Captured BEFORE anything overrides HOME: the operator's real tree is what the observations
        // are made against, and GOOGLE_APPLICATION_CREDENTIALS points into it (auth is an env path,
        // not HOME-anchored file state — see the probe findings).
        var realHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        await Assert.That(realHome).IsNotEmpty().Because("the observations are made against the operator's real HOME");

        var kcapLogs     = Path.Combine(realHome, ".config", "kcap", "logs");
        var kcapWatchers = Path.Combine(realHome, ".config", "kcap", "watchers");

        var logsBefore     = SnapshotNames(kcapLogs);
        var watchersBefore = SnapshotNames(kcapWatchers);

        var stateDir  = CreateTemp("kcap-agy-containment-state-");
        var workspace = CreateTemp("kcap-agy-containment-ws-");
        string? home  = null;

        try {
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "containment probe workspace\n");

            // Production home and production argv/env — an assertion over a re-derived spawn shape
            // would certify the test's idea of the launch, not the launch.
            home = AntigravityReviewerHome.Create(
                stateDir, "containment-epoch", "containment-agent", [], grantInjectedMcpTools: true);

            var psi = AntigravityHostedAgentRuntimeFactory.BuildTurnPsi(
                config: new DaemonConfig {
                    AntigravityPath                      = "agy",
                    AntigravityUnattendedReviewerEnabled = true,
                    Name                                 = "containment-daemon",
                    DaemonEpoch                          = "containment-epoch",
                    StateDir                             = stateDir,
                    AntigravityReviewerTurnTimeoutSeconds = (int)TurnTimeout.TotalSeconds
                },
                ctx: Ctx(workspace),
                prompt: "Reply with the single word OK. Do not use any tools.",
                conversationId: null,
                home: home);

            // The daemon's own Google auth block, which BuildTurnPsi deliberately does not re-stamp
            // (it is inherited, so a rotated ADC path is read at spawn). Supplied here because a test
            // process is not a supervised daemon; defaulted rather than required so the gate stays
            // two variables.
            psi.Environment["AGY_ADC_AUTH"]        = Environment.GetEnvironmentVariable("AGY_ADC_AUTH") ?? "1";
            psi.Environment["GOOGLE_CLOUD_PROJECT"] = project;
            psi.Environment["GOOGLE_APPLICATION_CREDENTIALS"] =
                Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")
                ?? Path.Combine(realHome, ".config", "gcloud", "application_default_credentials.json");

            var turn = await RunTurnAsync(psi);

            Console.WriteLine($"[agy-containment] conversation={turn.ConversationId} status={turn.Status} "
                            + $"exit={turn.ExitCode} detail={turn.Detail}");

            // ── the run must have HAPPENED before any absence is evidence ──────────────────────────
            // Without this, every observation below is satisfied by an agy that never started: no
            // conversation means no hook, no watcher and no store entry, trivially.
            await Assert.That(turn.ConversationId).IsNotNull()
                .Because($"no conversation id, no evidence either way: {turn.Detail}");
            await Assert.That(turn.SawResult).IsTrue()
                .Because($"the turn must have reached its terminal result: {turn.Detail}");

            var id       = turn.ConversationId!;
            var dashless = Guid.TryParse(id, out var g) ? g.ToString("N") : id.Replace("-", "");

            // Positive control for observations 3 and 4 together: the conversation state EXISTS, and
            // the rest of the test is about where. Without it, "the operator's store gained nothing"
            // could mean agy wrote its state nowhere at all.
            await Assert.That(TreeMentions(home, id, dashless)).IsTrue()
                .Because("agy must have written this conversation's state somewhere under the per-launch home");

            // ── 1. no kcap hook fired ─────────────────────────────────────────────────────────────
            // Keyed on the conversation id, not on a bare count: a live daemon on this host writes its
            // own logs throughout, and a count comparison would be a coin flip.
            await Assert.That(NewNamesMentioning(kcapLogs, logsBefore, id, dashless)).IsEmpty()
                .Because("an empty HOME has no ~/.gemini/config/plugins/kcap/hooks.json, so agy's capture "
                       + "hooks cannot fire — a log keyed on this conversation means the reviewer was "
                       + "double-captured");
            await Assert.That(NewNamesMentioning(kcapWatchers, watchersBefore, id, dashless)).IsEmpty()
                .Because("a spawnlock or watcher marker keyed on this conversation is a watcher that ran");

            // ── 2. no watcher process ─────────────────────────────────────────────────────────────
            await Assert.That(turn.ProcessesMentioningConversation).IsEmpty()
                .Because("no process may carry this conversation id — the NDJSON stream is the only capture lane");

            // ── 3. agy's Library writes landed inside the per-launch home ─────────────────────────
            // The sharpest containment question available: agy's own credential lives outside
            // ~/.gemini, under ~/Library, so if agy resolved ~/Library from the real user rather than
            // from $HOME the relocated home would contain nothing that matters. It does not — and
            // this is the assertion that keeps that a measured fact.
            await Assert.That(Directory.Exists(Path.Combine(home, "Library"))).IsTrue()
                .Because("agy must derive ~/Library from $HOME; if it stopped, reviewer state would land "
                       + "in the operator's own tree and the containment claim would be false");

            // ── 4. the operator's real agy conversation store gained no entry ─────────────────────
            // Both roots: the `agy` CLI writes ~/.gemini/antigravity-cli/, the GUI IDE writes
            // ~/.gemini/antigravity/ — one vendor identity, two products, two stores.
            foreach (var root in (string[])[Path.Combine(realHome, ".gemini", "antigravity-cli"),
                                            Path.Combine(realHome, ".gemini", "antigravity")]) {
                await Assert.That(Directory.Exists(Path.Combine(root, "brain", id))).IsFalse()
                    .Because($"the reviewer's brain dir must not appear in the operator's store ({root})");
                await Assert.That(File.Exists(Path.Combine(root, "conversations", $"{id}.db"))).IsFalse()
                    .Because($"the reviewer's conversation db must not appear in the operator's store ({root})");
            }
        } finally {
            if (home is not null) AntigravityReviewerHome.Delete(home, stateDir);
            TryDelete(stateDir);
            TryDelete(workspace);
        }
    }

    // ── gate ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Both gates, evaluated before any work, so a CI run costs a skip and nothing else. The
    /// two reasons are reported separately: "not opted in" and "opted in but misconfigured" are
    /// different operator problems, and collapsing them hides the second.</summary>
    static string Gate() {
        Skip.Unless(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"Gated live enforcement of the Antigravity reviewer's containment — set {GateEnvVar}=1 to run "
          + "(spends one real agy turn; needs `agy` on PATH and application-default credentials). Observes "
          + "the filesystem after a real process, because a model-layer refusal is not containment evidence.");

        var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? "";

        Skip.Unless(project.Length > 0,
            $"{GateEnvVar}=1 but GOOGLE_CLOUD_PROJECT is unset — agy authenticates from an empty HOME via "
          + "AGY_ADC_AUTH + GOOGLE_CLOUD_PROJECT + GOOGLE_APPLICATION_CREDENTIALS, and without the project "
          + "it fails in a way that names a tier problem rather than the missing setting.");

        return project;
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    readonly record struct TurnOutcome(
        string? ConversationId, bool SawResult, string? Status, int? ExitCode,
        IReadOnlyList<string> ProcessesMentioningConversation, string? Detail);

    /// <summary>
    /// Drives one real turn, parsing stdout with the PRODUCTION NDJSON parser. Takes the process-table
    /// snapshot as soon as the conversation id is known — a watcher spawned by a hook would be alive
    /// then, and might have exited by the time the turn ends.
    /// </summary>
    static async Task<TurnOutcome> RunTurnAsync(ProcessStartInfo psi) {
        using var cts  = new CancellationTokenSource(TurnTimeout);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("agy did not start — is it on PATH?");

        // Closed immediately, exactly as the production turn process does: nothing can consume a
        // pasted OAuth code, so an unauthenticated agy fails against the deadline rather than waiting
        // on a human who is not there.
        proc.StandardInput.Close();

        var stderrDrain = Task.Run(async () => {
            try { return await proc.StandardError.ReadToEndAsync(cts.Token); } catch { return ""; }
        }, cts.Token);

        string?      conversationId = null;
        string?      status         = null;
        var          sawResult      = false;
        List<string> processes      = [];

        try {
            while (await proc.StandardOutput.ReadLineAsync(cts.Token) is { } line) {
                if (AntigravityNdjson.TryParseLine(line) is not { } evt) continue;

                if (evt.Kind == AntigravityEventKind.Init && evt.ConversationId is { Length: > 0 } cid) {
                    conversationId = cid;
                    var dashless   = Guid.TryParse(cid, out var g) ? g.ToString("N") : cid.Replace("-", "");
                    processes      = await ProcessesMentioningAsync(cid, dashless);
                }

                if (evt.Kind == AntigravityEventKind.Result) {
                    sawResult = true;
                    status    = evt.Status;
                }
            }

            await proc.WaitForExitAsync(cts.Token);

            // A watcher spawned late, or one that outlives the turn, is caught by this second pass.
            if (conversationId is { Length: > 0 } done) {
                var dashless = Guid.TryParse(done, out var g2) ? g2.ToString("N") : done.Replace("-", "");
                processes    = [.. processes.Concat(await ProcessesMentioningAsync(done, dashless)).Distinct()];
            }

            return new(conversationId, sawResult, status, proc.HasExited ? proc.ExitCode : null, processes, null);
        } catch (OperationCanceledException) {
            // stderr is where agy reports an auth/project misconfiguration, which is by far the
            // likeliest reason this times out on a fresh machine.
            var err = stderrDrain.IsCompletedSuccessfully ? stderrDrain.Result : "(stderr not drained)";

            return new(conversationId, sawResult, status, null, processes,
                       $"timed out after {TurnTimeout.TotalSeconds:N0}s; stderr tail: "
                     + err[Math.Max(0, err.Length - 400)..]);
        } finally {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>Every command line on this host mentioning the conversation, in either id spelling —
    /// the watcher takes the dashless session id, agy uses the dashed one.</summary>
    static async Task<List<string>> ProcessesMentioningAsync(string id, string dashless) {
        try {
            using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-Ao", "args="]) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });

            if (ps is null) return [];

            using var kill = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var text = await ps.StandardOutput.ReadToEndAsync(kill.Token);
            await ps.WaitForExitAsync(kill.Token);

            return [.. text.Split('\n')
                           .Where(l => l.Contains(id, StringComparison.Ordinal)
                                    || l.Contains(dashless, StringComparison.Ordinal))
                           // Our own agy child carries the id only once it is resumed; exclude nothing
                           // else, so a `kcap watch` on this conversation cannot be filtered away.
                           .Where(l => !l.Contains("/bin/ps", StringComparison.Ordinal))];
        } catch {
            return [];
        }
    }

    static RuntimeStartContext Ctx(string workspace) => new(
        AgentId: "containment-agent", Vendor: "antigravity", SourceRepoPath: workspace,
        Worktree: new WorktreeInfo(Path: workspace, Branch: "containment", SourceRepo: workspace),
        Prompt: null, Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: true, Review: null,
        Cols: 80, Rows: 24,
        // No server URL: a hook that fired would still write its log, so leaving KCAP_URL unset keeps
        // the measurement from touching a real server without weakening observation 1.
        ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        DaemonId: "containment-daemon", DaemonEpoch: "containment-epoch");

    // ── observation helpers (read-only) ───────────────────────────────────────────────────────────

    static HashSet<string> SnapshotNames(string dir) {
        try {
            return Directory.Exists(dir)
                ? [.. Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Select(x => x!)]
                : [];
        } catch {
            return [];
        }
    }

    /// <summary>Entries that are BOTH new since the snapshot and keyed on this conversation. New-only
    /// would be noise from a live daemon; keyed-only would count a pre-existing coincidence.</summary>
    static IReadOnlyList<string> NewNamesMentioning(string dir, HashSet<string> before, string id, string dashless) =>
        [.. SnapshotNames(dir)
             .Where(n => !before.Contains(n))
             .Where(n => n.Contains(id, StringComparison.OrdinalIgnoreCase)
                      || n.Contains(dashless, StringComparison.OrdinalIgnoreCase))];

    /// <summary><c>AttributesToSkip</c> is set explicitly to NONE, and that is load-bearing: its default
    /// skips <c>Hidden</c>, and .NET reports every Unix dot-entry as hidden — so the default would make
    /// the whole <c>.gemini</c> subtree, which is where agy writes ALL of its conversation state,
    /// invisible to this search. The first live run of this test failed on exactly that, with the
    /// containment itself working perfectly.</summary>
    static bool TreeMentions(string root, string id, string dashless) {
        try {
            return Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None
            }).Any(p => p.Contains(id, StringComparison.Ordinal) || p.Contains(dashless, StringComparison.Ordinal));
        } catch {
            return false;
        }
    }

    static string CreateTemp(string prefix) {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void TryDelete(string dir) {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
    }
}
