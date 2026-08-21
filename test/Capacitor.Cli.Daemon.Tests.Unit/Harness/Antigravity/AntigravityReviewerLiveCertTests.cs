using System.Diagnostics;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

/// <summary>
/// GATED live certification of the unattended Antigravity reviewer against a REAL <c>agy</c>.
///
/// <para><b>What this certifies.</b> The daemon half of a
/// <c>start_review_flow(kind: "code-review", vendor: "antigravity")</c> — the reviewer launch the
/// server drives — end to end: a real turn child spawns under the per-launch isolated
/// <c>HOME</c>, authenticates from the daemon's own ADC environment, reports a conversation id, and
/// completes its round with no human answering anything. The server-side half (the MCP call itself,
/// and the result arriving over <c>kcap mcp flow-result</c>) needs a running tenant and a daemon
/// that ADVERTISES antigravity, so it is deliberately not simulated here: a fake would certify
/// nothing and would read as a green light nobody earned.</para>
///
/// <para><b>The second cert is the load-bearing one.</b> <c>agy</c> has no long-lived process —
/// every round is its own <c>agy -p …</c> invocation, resumed with <c>--conversation &lt;id&gt;</c>.
/// That exec-per-turn shape is invisible from a single round, and a regression in it is silent: the
/// review still "works", it just lands as one kcap session PER ROUND instead of one session for the
/// whole review, splitting the reviewer's history at exactly the point a re-review depends on it.
/// <see cref="Cert2_EveryRoundOfAMultiRoundReviewLandsAsOneSession"/> pins all three observable
/// halves of the claim — one stable conversation id, a distinct process per round, and the resume
/// flag actually carrying that id.</para>
///
/// <para><b>Gated</b> behind <c>KCAP_ANTIGRAVITY_REVIEWER_LIVE=1</c>: CI has no <c>agy</c> binary and
/// no Google account, and each case spends real model turns. Requires <c>agy</c> on <c>PATH</c> (or
/// <c>KCAP_ANTIGRAVITY_PATH</c>) — the harness records that build as this daemon's minimum, exactly
/// as enabling the reviewer does — plus the same durable ADC
/// credentials a supervised daemon needs — <c>gcloud auth application-default login</c>,
/// <c>GOOGLE_CLOUD_PROJECT</c> and <c>AGY_ADC_AUTH=1</c> — which the launch path deliberately
/// INHERITS from this process's environment rather than re-stamping, exactly as it inherits them
/// from the daemon's.</para>
/// </summary>
public class AntigravityReviewerLiveCertTests {
    const string GateEnvVar = "KCAP_ANTIGRAVITY_REVIEWER_LIVE";

    /// <summary>Bounded on purpose, and generously: a real model turn under a cold ADC handshake is
    /// slow, but "never completed" and "skipped or inconclusive" must stay distinguishable, or a
    /// broken reviewer looks exactly like a test that did not run.</summary>
    static readonly TimeSpan RoundBudget = TimeSpan.FromMinutes(6);

    const int LaunchTimeoutSeconds = 180;
    const int TurnTimeoutSeconds   = 300;

    /// <summary>
    /// The gate. <see cref="Skip"/> is the FIRST statement executed on every path, so an
    /// ungated run costs a process-environment read and nothing else — no filesystem, no spawn.
    ///
    /// <para>Everything after it fails LOUDLY rather than skipping: once an operator has asked for a
    /// live run, a missing <c>GOOGLE_CLOUD_PROJECT</c> presents downstream as
    /// <c>antigravity_reviewer_launch_timeout</c> — a bounded failure that names the wrong culprit
    /// unless the harness says so first.</para>
    /// </summary>
    static void Gate() {
        Skip.Unless(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"Gated live certification of the unattended Antigravity reviewer — set {GateEnvVar}=1 to run "
          + "(spends real agy turns; needs `agy` on PATH, and the daemon's "
          + "own ADC credentials: gcloud auth application-default login, GOOGLE_CLOUD_PROJECT=<project>, "
          + "AGY_ADC_AUTH=1). Re-run it against a new agy before recommending that build to operators.");

        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Antigravity reviewer is POSIX-only: its per-launch home holds review context and cannot "
          + "be created owner-only on Windows.");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")))
            throw new InvalidOperationException(
                $"{GateEnvVar}=1 but GOOGLE_CLOUD_PROJECT is unset. The launch path inherits the ADC "
              + "variables from this process rather than re-stamping them, so agy would sit on an "
              + "interactive login and the run would fail as antigravity_reviewer_launch_timeout — "
              + "naming the deadline rather than the missing project. Export GOOGLE_CLOUD_PROJECT (and "
              + "AGY_ADC_AUTH=1) and re-run.");
    }

    /// <summary>
    /// One real review round, launched exactly as a review flow launches it, completing with nothing
    /// to answer — the positive control, and the thing without which there is no feature.
    ///
    /// <para>Asserts the round REACHED A TERMINAL RESULT, not merely that the launch returned:
    /// <c>StartAsync</c> resolves at turn 1's <c>init</c>, so a reviewer that reported a conversation
    /// and then died mid-turn would satisfy a launch-only assertion. The runtime distinguishes the
    /// two for us — EOF without a terminal <c>result</c> is Terminal, a clean round is Idle — and
    /// Idle is what <c>HasExited == false</c> after the turn settles means.</para>
    /// </summary>
    [Test]
    public async Task Cert1_OneReviewRoundCompletesUnattended() {
        Gate();

        using var harness = LiveHarness.Create();

        var start = await harness.LaunchAsync("Reply with the single word OK. Do not use any tools.")
                                 .WaitAsync(RoundBudget);

        await using var runtime = start.Runtime;

        // The correlation identity the orchestrator binds a transcript to. Empty here is the silent,
        // permanent break the launch barrier exists to prevent.
        var conversationId = start.Transcript!.AcpSessionId;
        await Assert.That(conversationId).IsNotEmpty()
            .Because("no conversation id means the orchestrator binds this reviewer's transcript to \"\"");

        await harness.AwaitRoundAsync(runtime, rounds: 1);

        harness.Report("cert1", conversationId);

        await Assert.That(runtime.HasExited).IsFalse()
            .Because("the reviewer must be logically ALIVE between rounds — Terminal here means the turn "
                   + "hit EOF without a terminal `result`, i.e. the reviewer died mid-round");

        // A round that produced no transcript at all would still satisfy every assertion above.
        await Assert.That(LiveHarness.EnvelopeCount(start.Transcript!)).IsGreaterThan(0)
            .Because("a reviewer that reports a conversation and then emits nothing has recorded no review");
    }

    /// <summary>
    /// The exec-per-turn claim, in the only shape that can fail silently: three rounds must land as
    /// ONE kcap session.
    ///
    /// <para>Three independent observations, because each alone is satisfiable by a broken build.
    /// A stable id alone could come from a runtime that never actually resumed (it would simply
    /// report the id it already held while <c>agy</c> started a fresh conversation each time);
    /// distinct pids alone prove separate processes but say nothing about continuity; and the resume
    /// flag alone proves what we PASSED, not what the vendor did with it. Together they pin
    /// "separate process per round, resuming the one conversation the first round established" —
    /// and the runtime's own conversation-id-stability rule is the fourth: a vendor that forked the
    /// history mid-review drives Terminal, which the final liveness assertion would catch.</para>
    /// </summary>
    [Test]
    public async Task Cert2_EveryRoundOfAMultiRoundReviewLandsAsOneSession() {
        Gate();

        using var harness = LiveHarness.Create();

        var start = await harness.LaunchAsync("Reply with the single word ONE. Do not use any tools.")
                                 .WaitAsync(RoundBudget);

        await using var runtime = start.Runtime;

        var conversationId = start.Transcript!.AcpSessionId;
        await Assert.That(conversationId).IsNotEmpty();

        await harness.AwaitRoundAsync(runtime, rounds: 1);

        await runtime.SendUserInputAsync("Reply with the single word TWO. Do not use any tools.");
        await harness.AwaitRoundAsync(runtime, rounds: 2);

        await runtime.SendUserInputAsync("Reply with the single word THREE. Do not use any tools.");
        await harness.AwaitRoundAsync(runtime, rounds: 3);

        harness.Report("cert2", conversationId);

        // (1) ONE session across every round — read from the live runtime AFTER the last round, so a
        // mid-review fork could not have been overwritten back to the original value.
        await Assert.That(start.Transcript!.AcpSessionId).IsEqualTo(conversationId)
            .Because("a changed conversation id means the review forked into a second kcap session");

        // (2) A DISTINCT process per round — the literal exec-per-turn shape. A runtime that quietly
        // acquired a long-lived child would report the same pid three times.
        var pids = harness.Spawns.Select(s => s.Process.Pid).ToList();
        await Assert.That(pids.Distinct().Count()).IsEqualTo(3)
            .Because($"every round is its own `agy -p` invocation; observed pids [{string.Join(",", pids)}]");

        // (3) The resume flag, adjacent to the id — not merely present. A build that emitted the id
        // under a different flag, or the flag with a different value, would satisfy two independent
        // Contains checks while resuming nothing.
        await Assert.That(harness.Spawns[0].Psi.ArgumentList).DoesNotContain("--conversation")
            .Because("turn 1 has nothing to resume");

        foreach (var round in harness.Spawns.Skip(1)) {
            var argv = round.Psi.ArgumentList;
            var i    = argv.IndexOf("--conversation");

            await Assert.That(i).IsGreaterThanOrEqualTo(0)
                .Because("a round that omits --conversation starts a brand-new agy conversation");
            await Assert.That(argv[i + 1]).IsEqualTo(conversationId);
        }

        // (4) Still logically alive: the runtime reaps itself to Terminal on a conversation-id
        // mismatch, so this is what makes the stability rule's own enforcement part of the evidence.
        await Assert.That(runtime.HasExited).IsFalse();
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One spawned round: the vector the OS received, and the child it produced.</summary>
    readonly record struct SpawnedRound(ProcessStartInfo Psi, IAgyTurnProcess Process);

    /// <summary>
    /// Drives the PRODUCTION launch path — the same <see cref="AntigravityHostedAgentRuntimeFactory"/>
    /// a review flow reaches, with the real version probe and the real binary resolution — and spawns
    /// the real <see cref="AgyTurnProcess"/> from the <see cref="ProcessStartInfo"/> the factory built.
    ///
    /// <para>The turn-source seam is used only to OBSERVE (it records the psi and the child, then
    /// constructs exactly what production's default constructs), so nothing about the spawn shape is
    /// re-derived here: the argv assertions above run against the vector the OS actually got.</para>
    /// </summary>
    sealed class LiveHarness : IDisposable {
        readonly TempDir         _tmp;
        readonly TempDaemonStore _daemons;

        LiveHarness(TempDir tmp, TempDaemonStore daemons, string worktree, DaemonConfig config) {
            _tmp     = tmp;
            _daemons = daemons;
            Worktree = worktree;
            Config   = config;
        }

        internal string       Worktree { get; }
        internal DaemonConfig Config   { get; }

        internal List<SpawnedRound> Spawns { get; } = [];

        internal static LiveHarness Create() {
            var tmp      = new TempDir("agyc");
            var worktree = tmp.CreateDir("wt");
            var daemons  = new TempDaemonStore();

            // Something to be reviewing. The prompts deliberately never ask for it — a cert that
            // depended on the model reading a file would be measuring the model, not the reviewer —
            // but an empty cwd is not a review-shaped launch.
            worktree.CreateFile("subject.txt", "int Add(int a, int b) => a - b;\n");

            var config = new DaemonConfig {
                AntigravityPath                       = Environment.GetEnvironmentVariable("KCAP_ANTIGRAVITY_PATH") is
                                                        { Length: > 0 } path ? path : "agy",
                AntigravityUnattendedReviewerEnabled  = true,
                AntigravityReviewerLaunchTimeoutSeconds = LaunchTimeoutSeconds,
                AntigravityReviewerTurnTimeoutSeconds   = TurnTimeoutSeconds,
                Name                                  = "agy-live-cert",
                DaemonEpoch                           = "cert-" + Guid.NewGuid().ToString("N")[..8],
                Store                                 = daemons.Store
            };

            // Seeded through the DAEMON's own path, not a hand-written record: production records the
            // minimum from the consent event at startup, and this harness is standing in for a daemon
            // whose operator has just enabled the reviewer. It resolves the version by really running
            // the installed agy, so the cert still judges the INSTALLED build rather than a seamed one.
            DaemonRunner.SeedReviewerAffirmation(
                AntigravityHostedAgentRuntimeFactory.ReviewerStateDir(config),
                DaemonRunner.AntigravityVendor, enabled: true, config.AntigravityPath);

            return new LiveHarness(tmp, daemons, worktree, config);
        }

        internal Task<HostedRuntimeStart> LaunchAsync(string prompt) {
            // binaryExists/resolveVersion left to production: this cert exists to judge the INSTALLED
            // agy, so seaming the version would certify a build the gate would have refused.
            var factory = new AntigravityHostedAgentRuntimeFactory(
                Config, NullLoggerFactory.Instance, turnSource: SpawnAsync);

            var ctx = new RuntimeStartContext(
                AgentId: "agy-cert-" + Guid.NewGuid().ToString("N")[..8],
                Vendor: "antigravity",
                SourceRepoPath: Worktree,
                Worktree: new WorktreeInfo(Path: Worktree, Branch: "cert", SourceRepo: Worktree),
                Prompt: prompt,
                Model: null, Effort: null, Tools: null,
                IsReview: false, IsReviewFlow: true, Review: null,
                Cols: 80, Rows: 24,
                // A reachable tenant is not needed (and not assumed): the injected result channel is
                // part of the launch shape being certified, but this cert judges the reviewer's own
                // round, never a submitted result.
                ServerUrl: Environment.GetEnvironmentVariable("KCAP_URL") is { Length: > 0 } url
                           ? url : "http://kcap.invalid",
                DaemonBridgeUrl: null,
                CapacitorPath: Environment.GetEnvironmentVariable("KCAP_PATH") is { Length: > 0 } kcap
                               ? kcap : "kcap",
                DaemonId: "agy-live-cert",
                DaemonEpoch: Config.DaemonEpoch!);

            return factory.StartAsync(ctx, CancellationToken.None);
        }

        Task<IAgyTurnProcess> SpawnAsync(ProcessStartInfo psi, CancellationToken ct) {
            var process = new AgyTurnProcess(psi, NullLogger<AgyTurnProcess>.Instance);

            lock (Spawns) Spawns.Add(new SpawnedRound(psi, process));

            return Task.FromResult<IAgyTurnProcess>(process);
        }

        /// <summary>
        /// Waits for round <paramref name="rounds"/> to finish. Two barriers, in order, because
        /// neither alone is one: first the round must be OBSERVED spawned (the enqueue→gate hand-off
        /// is asynchronous, so <c>WaitForTurnIdleAsync</c> on its own can return against a
        /// momentarily-free gate before the worker has even dequeued), and only then does the
        /// gate-acquire genuinely queue behind the in-flight turn.
        /// </summary>
        internal async Task AwaitRoundAsync(IHostedAgentRuntime runtime, int rounds) {
            var deadline = DateTime.UtcNow + RoundBudget;

            while (SpawnCount < rounds && DateTime.UtcNow < deadline) await Task.Delay(25);

            if (SpawnCount < rounds)
                throw new TimeoutException(
                    $"Only {SpawnCount} agy turn(s) spawned; expected {rounds}. The reviewer never started "
                  + "this round — check the daemon's ADC environment and its recorded agy minimum.");

            using var cts = new CancellationTokenSource(RoundBudget);
            await runtime.WaitForTurnIdleAsync(cts.Token);
        }

        int SpawnCount { get { lock (Spawns) return Spawns.Count; } }

        /// <summary>How much transcript this reviewer actually produced. Drains what is READY only —
        /// the channel stays open for the whole session, so a blocking read would never return.</summary>
        internal static int EnvelopeCount(IAcpTranscriptSource transcript) {
            var count = 0;

            while (transcript.Envelopes.TryRead(out _)) count++;

            return count;
        }

        internal void Report(string label, string conversationId) {
            lock (Spawns)
                Console.WriteLine(
                    $"[agy-cert:{label}] conversation={conversationId} rounds={Spawns.Count} "
                  + $"pids=[{string.Join(",", Spawns.Select(s => s.Process.Pid))}] "
                  + $"binary={Config.AntigravityPath} worktree={Worktree}");
        }

        public void Dispose() {
            lock (Spawns)
                foreach (var spawn in Spawns) {
                    try { spawn.Process.TerminateAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                    catch { /* best-effort — the runtime owns these; this is the leak backstop */ }
                }

            _tmp.Dispose();
            _daemons.Dispose();
        }
    }
}
