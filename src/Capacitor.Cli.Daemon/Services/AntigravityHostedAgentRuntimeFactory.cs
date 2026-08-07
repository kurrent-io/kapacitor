// src/Capacitor.Cli.Daemon/Services/AntigravityHostedAgentRuntimeFactory.cs
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A turn process that can explain, after the fact, why it produced no protocol traffic. Kept off
/// <see cref="IAgyTurnProcess"/> — the runtime never needs it, only the launch path does, and a
/// runtime-visible diagnostics channel would invite exactly the kind of per-turn text accumulation
/// this deliberately bounds.
/// </summary>
internal interface IAgyTurnDiagnostics {
    /// <summary>A bounded capture of whatever the child wrote outside the NDJSON protocol (stderr,
    /// plus stdout lines that were not JSON at all), or null if it wrote nothing. Read once, on a
    /// failed launch, to turn "no <c>init</c> arrived" into a reason an operator can act on.</summary>
    string? Diagnostics { get; }
}

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for Antigravity's CLI (<c>agy</c>) as an unattended
/// review-flow reviewer, over the exec-per-turn <see cref="AntigravityHostedAgentRuntime"/>.
///
/// <para><b>What this factory owns that the runtime deliberately does not:</b> the argv and
/// environment of every turn child, the per-launch isolated <c>HOME</c> (and its removal), the
/// absolute bound on the launch handshake, and the ordering the orchestrator depends on — that
/// <see cref="StartAsync"/> does not return until turn 1's <c>init</c> has resolved the conversation
/// id, because the orchestrator reads <c>transcript.AcpSessionId</c> synchronously the moment a
/// launch returns and would otherwise bind the transcript to <c>""</c> forever.</para>
///
/// <para><b>Auth is deliberately not an advertisement gate</b> (owner decision). The factory
/// advertises whenever consent is given and the binary resolves; an operator whose only auth is an
/// interactive <c>agy</c> login gets a bounded, coded launch failure naming the ADC remedy, rather
/// than a vendor that silently disappears from the list.</para>
///
/// <para><b>No borrowed lane</b> — no <c>sandbox-exec</c> substrate here, so
/// <see cref="SupportsBorrowedReviewFlow"/> stays false and a borrowed request fails closed to an
/// owned worktree, exactly as Kiro and Claude do.</para>
/// </summary>
/// <param name="turnSource">Test seam ONLY. Production passes null, which spawns the real
/// <see cref="AgyTurnProcess"/> from the <see cref="ProcessStartInfo"/> <see cref="BuildTurnPsi"/>
/// built — so the seam changes nothing about production behaviour, and the argv assertions run
/// against the same builder a real launch uses.</param>
/// <param name="binaryExists">Test seam ONLY, for the availability half of the gate. Production
/// passes null, which resolves the real binary through <c>PATH</c>. A test pins it so the CONSENT
/// half is assertable on a host that happens to have (or not have) <c>agy</c> installed — otherwise
/// the tests pass for the wrong reason on one machine and fail on another.</param>
internal sealed partial class AntigravityHostedAgentRuntimeFactory(
        DaemonConfig                                                     config,
        ILoggerFactory                                                   loggerFactory,
        Func<ProcessStartInfo, CancellationToken, Task<IAgyTurnProcess>>? turnSource = null,
        Func<string, bool>?                                              binaryExists = null
    ) : IHostedAgentRuntimeFactory {
    readonly ILogger _logger = loggerFactory.CreateLogger<AntigravityHostedAgentRuntimeFactory>();

    readonly Func<ProcessStartInfo, CancellationToken, Task<IAgyTurnProcess>> _turnSource =
        turnSource ?? ((psi, _) => Task.FromResult<IAgyTurnProcess>(
            new AgyTurnProcess(psi, loggerFactory.CreateLogger<AgyTurnProcess>())));

    readonly Func<string, bool> _binaryExists = binaryExists ?? CliResolver.Exists;

    /// <summary>The vendor token, never <c>agy</c>: the server routes on this exact string and the
    /// capture side already knows <c>antigravity</c>. <c>agy</c> is only ever a binary name.</summary>
    public string Vendor => "antigravity";

    public bool IsAvailable() => _binaryExists(config.AntigravityPath);

    public bool SupportsUnattended => DescribeUnattendedSupport().Supported;

    /// <summary>
    /// The gate ladder, in ONE pass — consent, then platform, then binary presence. Consent is read
    /// first so a daemon that has not opted in never touches the filesystem to decide.
    ///
    /// <para>Every refusal here carries a reason, because this vendor DOES offer unattended hosting:
    /// the <see langword="null"/> case in <see cref="UnattendedSupport"/> is for a vendor that never
    /// claimed it, which is not this one. The reason names the switch an operator can act on.</para>
    /// </summary>
    public UnattendedSupport DescribeUnattendedSupport() {
        var withheld = ReviewerRefusal();

        return new(withheld is null, withheld);
    }

    /// <summary>Why this daemon refuses an unattended Antigravity reviewer, or null when it does not.
    /// The ONE place the ladder is written — advertisement and the launch boundary both read it, so a
    /// vendor cannot be dropped from advertisement and thereby never reach the path that holds the
    /// explanation.
    ///
    /// <para>Deliberately not cached: a long-running daemon must re-judge a binary installed (or
    /// removed) under it rather than read a startup snapshot.</para></summary>
    internal string? ReviewerRefusal() {
        if (!config.AntigravityUnattendedReviewerEnabled)
            return "antigravity_unattended_reviewer_disabled: unattended Antigravity reviews are off on "
                 + "this daemon. Set KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=1 in the daemon's environment "
                 + "to opt in.";

        if (OperatingSystem.IsWindows())
            return "antigravity_reviewer_unsupported_platform: the reviewer's per-launch home holds "
                 + "review context and cannot be created owner-only on Windows.";

        if (!IsAvailable())
            return $"antigravity_reviewer_binary_missing: '{config.AntigravityPath}' does not resolve to "
                 + "an executable. Install the Antigravity CLI (the `agy` binary — the IDE alone is not "
                 + "enough), or set KCAP_ANTIGRAVITY_PATH to its location.";

        return null;
    }

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        // An interactive launch is refused rather than silently accepted. This runtime parses agy's
        // NDJSON from stdout once per turn, and an inherited HOME lets agy's own kcap capture hooks
        // fire — which spawns a watcher that can hold a write end of that stdout open after agy exits,
        // so every turn would block forever with no visible cause. The isolated home below is what
        // makes that unreachable, and it is a reviewer-only construct today.
        if (!ctx.IsReviewFlow)
            throw new InvalidOperationException(
                "antigravity_interactive_launch_unsupported: the Antigravity runtime hosts unattended "
              + "review-flow reviewers only. An interactive launch would run under the operator's own "
              + "HOME, where agy's capture hooks fire and can hold this runtime's stdout open after the "
              + "turn exits.");

        // Defence in depth: the orchestrator's unattended gate runs first, but an explicit
        // `vendor: "antigravity"` request can reach a factory without consulting advertisement.
        if (ReviewerRefusal() is { } refusal) throw new InvalidOperationException(refusal);

        if (ctx.Work != WorkLocation.OwnedWorktree)
            throw new InvalidOperationException(
                "antigravity_reviewer_requires_owned_worktree: this runtime has no borrowed-review "
              + "containment strategy, so a review must run in a daemon-owned worktree.");

        // A blank agent id would still yield a non-empty server list and slip past a count-only guard,
        // so all three result-channel inputs are checked — a dead channel wedges the round.
        if (string.IsNullOrWhiteSpace(ctx.ServerUrl) || string.IsNullOrWhiteSpace(ctx.CapacitorPath)
         || string.IsNullOrWhiteSpace(ctx.AgentId))
            throw new InvalidOperationException(
                "antigravity_reviewer_result_channel_incomplete: cannot inject the kcap-flow-result "
              + "channel (missing server url / kcap path / agent id).");

        // Canonical wire names: agy's MCP surface is the file this launch writes, not a name-matched
        // allowlist the reviewed repository could impersonate an entry in, so the per-launch aliasing
        // Gemini and Kiro need buys nothing here.
        ctx = ctx with { LaunchIdentity = LaunchIdentity.ForLaunch(aliasResultChannel: false) };

        if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(ctx.McpAllowlist, out var allowlistServerIds, out var rejected))
            throw new InvalidOperationException(
                $"antigravity_reviewer_mcp_allowlist_rejected: '{rejected}' is not an auto-approvable "
              + "read-only server.");

        var injected = AcpReviewFlowMcp.Build(ctx, allowlistServerIds);

        LogLaunching(ctx.AgentId, ctx.Worktree.Path);

        // Created BEFORE any child exists, and owner-only from its first instant — the reviewer's own
        // conversation state lands in it. Its ABSENCE of a kcap plugin directory is what keeps capture
        // single-lane; the injected mcp_config.json is the reviewer's whole MCP surface.
        var stateDir = ReviewerStateDir(config);
        var home     = AntigravityReviewerHome.Create(
            stateDir, config.DaemonEpoch ?? "unpinned", ctx.AgentId, injected, _logger);

        // Created here rather than left to agy: TMPDIR must exist before the child writes into it, and
        // it is inside the home precisely so it is removed with it.
        Directory.CreateDirectory(TempDirIn(home));

        var model = ResolveModel(config, ctx);

        // Recorded so a failed launch can explain ITSELF — an unauthenticated agy produces no `init`,
        // and "the conversation id never arrived" is not a reason anyone can act on.
        IAgyTurnProcess? firstTurnProcess = null;

        async Task<IAgyTurnProcess> SpawnTurnAsync(string prompt, string? conversationId, CancellationToken turnCt) {
            var psi     = BuildTurnPsi(config, ctx, prompt, conversationId, home);
            var process = await _turnSource(psi, turnCt).ConfigureAwait(false);

            firstTurnProcess ??= process;

            return process;
        }

        var runtime = new AntigravityHostedAgentRuntime(
            spawnTurn: SpawnTurnAsync,
            logger: loggerFactory.CreateLogger<AntigravityHostedAgentRuntime>(),
            agentId: ctx.AgentId,
            model: model,
            cwd: ctx.Worktree.Path,
            launchDeadline: TimeSpan.FromSeconds(Math.Max(1, config.AntigravityReviewerLaunchTimeoutSeconds)),
            turnDeadline: TimeSpan.FromSeconds(Math.Max(1, config.AntigravityReviewerTurnTimeoutSeconds)),
            // Disposal, not disk hygiene: the home holds the reviewer's own conversation JSONL — the
            // caller's diff, source excerpts and findings. The daemon-epoch sweep is the crash
            // backstop, not the disposal path.
            onDisposed: () => AntigravityReviewerHome.Delete(home, stateDir, _logger));

        // MUST precede the first turn: a clock assigned later makes every stamp inside the launch a
        // silent no-op, and the reaper then judges this reviewer from an empty record.
        runtime.ActivityClock = ctx.ActivityClock;

        // ONE absolute budget across spawn → `init`, linked to the caller's token so a real shutdown
        // still wins and is never misreported as a timeout. This is what turns the measured
        // unauthenticated shapes — an immediate error, or an OAuth URL followed by a 60-second
        // interactive wait — into a bounded failure.
        using var launchDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        launchDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.AntigravityReviewerLaunchTimeoutSeconds)));

        try {
            await runtime.SendUserInputAsync(ctx.Prompt ?? "").ConfigureAwait(false);

            // The ordering guarantee. SendUserInputAsync returns as soon as the turn is enqueued, and
            // WaitForTurnIdleAsync is not a substitute (its enqueue→gate hand-off is itself async), so
            // this barrier is the only thing standing between the orchestrator and a transcript bound
            // to "".
            await runtime.WaitForConversationIdAsync(launchDeadline.Token).ConfigureAwait(false);
        } catch (Exception ex) {
            // The launch is over either way; leaving the child alive would leave an unreapable reviewer
            // holding a daemon slot, and leaving the home would leave review context on disk that
            // nobody downstream will ever dispose for us. DisposeAsync does both (it terminates, joins
            // the worker, then runs onDisposed).
            await runtime.DisposeAsync().ConfigureAwait(false);

            // A genuine shutdown propagates AS a cancellation. Dressing it up as a launch failure
            // would put a coded reviewer error in the record for every agent in flight when the
            // daemon stopped, and send an operator looking for a fault that never happened.
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;

            throw DescribeLaunchFailure(ex, firstTurnProcess, ct, launchDeadline.Token);
        }

        // The runtime IS the transcript source, so the orchestrator binds and forwards without
        // downcasting Runtime. McpConfigPath stays null deliberately: this launch's mcp config lives
        // INSIDE the home, and the home's own disposal owns it — reporting it separately would have
        // the orchestrator delete a file out of a directory it knows nothing about.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>
    /// Turns a failed handshake into a reason an operator can act on. The auth arm is
    /// <b>non-retryable and names the ADC remedy</b> — a generic launch failure would send an operator
    /// looking at the daemon, the flow or the network, when the actual fix is three environment
    /// variables.
    /// </summary>
    static InvalidOperationException DescribeLaunchFailure(
            Exception cause, IAgyTurnProcess? firstTurn, CancellationToken callerToken, CancellationToken launchToken) {
        if (firstTurn is IAgyTurnDiagnostics { Diagnostics: { } diagnostics } && LooksLikeAuthFailure(diagnostics))
            return new InvalidOperationException(
                "antigravity_reviewer_auth_unavailable: agy could not authenticate, and an unattended "
              + "reviewer has no way to complete an interactive login (its stdin is closed). Give the "
              + "daemon durable credentials: `gcloud auth application-default login`, then "
              + "GOOGLE_CLOUD_PROJECT=<project> and AGY_ADC_AUTH=1 in the daemon's environment. A "
              + "supervised daemon installed before this shipped must be reinstalled to capture them.",
                cause);

        if (launchToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
            return new InvalidOperationException(
                "antigravity_reviewer_launch_timeout: agy did not report a conversation within the "
              + "launch deadline. The child was terminated and its isolated home removed. An "
              + "unauthenticated agy can sit on an interactive login prompt rather than failing, which "
              + "is the shape this bound exists for.",
                cause);

        return new InvalidOperationException(
            "antigravity_reviewer_launch_failed: agy's first turn ended without reporting a "
          + "conversation id, so there is no session to bind a transcript to.",
            cause);
    }

    /// <summary>
    /// Whether a child's non-protocol output is the authentication signal. Substring, deliberately
    /// loose, and never the sole basis for anything destructive: the two measured shapes are an
    /// immediate <c>authentication required</c> error and an OAuth URL followed by an interactive
    /// wait, and both only ever change which REASON a launch that already failed reports.
    /// </summary>
    internal static bool LooksLikeAuthFailure(string? diagnostics) =>
        diagnostics is { Length: > 0 } text
     && (text.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
      || text.Contains("log in", StringComparison.OrdinalIgnoreCase)
      || text.Contains("oauth", StringComparison.OrdinalIgnoreCase));

    /// <summary>The effective reviewer model — the launch's own override, else the daemon-wide
    /// default. ONE derivation, read by both the argv builder and the runtime's
    /// <c>ResolvedModel</c>, so the model we report can never differ from the model we passed.
    /// Mirrors the <c>"default"</c> sentinel convention the rest of the daemon already uses for
    /// "no override requested".</summary>
    internal static string? ResolveModel(DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : config.AntigravityModel;

    /// <summary>This daemon's own reviewer state directory — the same <c>{StateDir}/{name}</c> shape
    /// the ACP reviewers use, so a reviewer home and a consent decision live under one owner-only root
    /// per daemon. Per DAEMON, never shared: the home sweep's safety depends on every directory in its
    /// root belonging to this daemon.</summary>
    internal static string ReviewerStateDir(DaemonConfig config) =>
        Path.Combine(config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name));

    internal static string TempDirIn(string home) => Path.Combine(home, "tmp");

    /// <summary>
    /// PURE builder for ONE turn's spawn shape — no process, no filesystem side effects. The real
    /// spawn path and the launch tests both go through it, so an assertion here certifies the vector
    /// the OS actually receives rather than a re-derivation of it.
    ///
    /// <para><b>What is deliberately absent.</b> No <c>--dangerously-skip-permissions</c>: the
    /// reviewer runs in a daemon-OWNED worktree and needs only to read it, which agy's headless
    /// defaults already permit — its soft-deny of shell and out-of-workspace operations IS the desired
    /// unattended posture, and widening it would grant a reviewer shell access it has no reason to
    /// hold. No <c>--sandbox</c>: a vendor-side terminal restriction overlapping what containment
    /// already provides, and unprobed.</para>
    ///
    /// <para><c>--print-timeout</c> is passed on EVERY invocation rather than relying on agy's own
    /// <c>5m0s</c> default — a vendor change would otherwise silently move a bound we did not
    /// choose.</para>
    /// </summary>
    internal static ProcessStartInfo BuildTurnPsi(
            DaemonConfig config, RuntimeStartContext ctx, string prompt, string? conversationId, string home) {
        var argv = new List<string> {
            "-p", prompt,
            "--output-format", "stream-json",
            "--disable-slash-commands",
            "--print-timeout", $"{Math.Max(1, config.AntigravityReviewerTurnTimeoutSeconds)}s"
        };

        // Absent on turn 1 (there is nothing to resume yet) and present on every turn after it — this
        // is what makes a multi-round review land as ONE conversation rather than one per round.
        if (!string.IsNullOrEmpty(conversationId)) {
            argv.Add("--conversation");
            argv.Add(conversationId);
        }

        // An unknown slug makes agy hard-fail, which is a clean audit signal rather than a silent
        // downgrade to whatever it would have picked.
        if (ResolveModel(config, ctx) is { Length: > 0 } model) {
            argv.Add("--model");
            argv.Add(model);
        }

        var psi = new ProcessStartInfo(config.AntigravityPath, argv) {
            WorkingDirectory = ctx.Worktree.Path,
            // Redirected and then CLOSED at spawn (see AgyTurnProcess): nothing can consume a pasted
            // OAuth code, so an unauthenticated agy fails against the launch deadline instead of
            // waiting on a human who is not there.
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            // Also what makes the daemon's own Google auth variables reach the child by inheritance —
            // they are deliberately not re-stamped below, so a rotated ADC path is read at spawn.
            UseShellExecute = false,
            CreateNoWindow  = true
        };

        // Containment: the whole HOME is the per-launch isolated one, and TMPDIR sits inside it so
        // agy's temp writes are removed with the home rather than left in the operator's tree.
        psi.Environment["HOME"]   = home;
        psi.Environment["TMPDIR"] = TempDirIn(home);

        if (!string.IsNullOrEmpty(ctx.ServerUrl)) psi.Environment["KCAP_URL"] = ctx.ServerUrl;

        // Without these a surviving turn child is invisible to OrphanReaper's env-marker pass — and
        // nothing fails visibly when they are omitted, which is exactly why they are stamped here
        // rather than left to the runtime.
        psi.Environment["KCAP_AGENT_ID"] = ctx.AgentId;
        if (!string.IsNullOrEmpty(ctx.DaemonId))    psi.Environment["KCAP_DAEMON_ID"]    = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) psi.Environment["KCAP_DAEMON_EPOCH"] = ctx.DaemonEpoch;

        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Antigravity reviewer launch: agentId={AgentId} cwd={Cwd}")]
    partial void LogLaunching(string agentId, string cwd);
}

/// <summary>
/// <see cref="IAgyTurnProcess"/> over a real <see cref="Process"/> — ONE <c>agy -p</c> turn. Mirrors
/// <see cref="AcpChildProcess"/>'s terminate/wait semantics (SIGTERM-then-kill via
/// <see cref="Process.Kill(bool)"/>, bounded waits that return silently on timeout), and honours the
/// two contracts <see cref="IAgyTurnProcess"/> states: <see cref="DisposeAsync"/> is idempotent, and
/// <see cref="TerminateAsync"/> is safe after it — the runtime can reach both on the same instance
/// microseconds apart when a stop races a turn's own unwinding.
///
/// <para><b>Identity is captured at construction</b>, not read on demand: <see cref="Process.Id"/>
/// throws once the process object is disposed, and the PID is exactly what an orphan reaper needs
/// most after the fact.</para>
/// </summary>
internal sealed partial class AgyTurnProcess : IAgyTurnProcess, IAgyTurnDiagnostics {
    /// <summary>Enough to carry an auth error and the URL under it, and small enough that a chatty
    /// child cannot grow the daemon's heap one turn at a time. Over-cap output is dropped, never
    /// rotated — this exists to explain a FAILED launch, and that explanation is at the start.</summary>
    const int DiagnosticsCap = 4096;

    readonly Process                 _process;
    readonly ILogger                 _logger;
    readonly CancellationTokenSource _stderrDrainCts = new();
    readonly Task                    _stderrDrainTask;
    readonly Lock                    _diagnosticsGate = new();
    readonly StringBuilder           _diagnostics     = new();

    int _disposed;

    internal AgyTurnProcess(ProcessStartInfo psi, ILogger logger)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"antigravity_turn_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               logger) { }

    internal AgyTurnProcess(Process process, ILogger logger) {
        _process = process;
        _logger  = logger;
        Pid      = SafePid(process);

        // Closed immediately, and this is a containment decision rather than tidiness: an
        // unauthenticated agy prints an OAuth URL and waits for a pasted code, and a child with no
        // readable stdin cannot be handed one. The launch deadline then bounds it.
        try {
            _process.StandardInput.Close();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Antigravity: could not close a turn child's stdin.");
        }

        _stderrDrainTask = DrainStderrAsync(_stderrDrainCts.Token);
    }

    public int  Pid       { get; }
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

    public int? ExitCode {
        get {
            try {
                return _process.HasExited ? _process.ExitCode : null;
            } catch {
                return null;
            }
        }
    }

    public string? Diagnostics {
        get { lock (_diagnosticsGate) return _diagnostics.Length == 0 ? null : _diagnostics.ToString(); }
    }

    /// <summary>
    /// This turn's stdout NDJSON, line by line, ending at EOF. Lines that are not JSON at all are
    /// still yielded (the runtime's parser drops them) AND snooped into the diagnostics buffer:
    /// measured, agy reports "authentication required" as plain text on this stream, so a
    /// stderr-only capture would miss the one failure this whole bound exists for.
    /// </summary>
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (true) {
            string? line;

            try {
                line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            } catch (IOException) {
                break;   // pipe torn down (the child was killed mid-read) — EOF, per this method's contract
            } catch (ObjectDisposedException) {
                break;   // stream disposed under us by a racing DisposeAsync — likewise EOF
            }

            if (line is null) break;

            if (!line.StartsWith('{')) Capture(line);

            yield return line;
        }
    }

    /// <summary>Keeps the stderr pipe drained — an undrained one fills at ~64KB and blocks the child
    /// on its next write, which for this runtime would wedge a turn forever. Never logs the text
    /// itself: agy's stderr can carry prompt fragments, paths and, on the auth path, a URL bearing a
    /// login code.</summary>
    async Task DrainStderrAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null) break;      // EOF — the child exited and closed the stream
                if (line.Length == 0) continue;

                Capture(line);
                LogStderrShape(line.Length);
            }
        } catch (OperationCanceledException) {
            // Disposal asked the drain to stop — expected.
        } catch (IOException) {
            // Pipe torn down on teardown — expected.
        } catch (ObjectDisposedException) {
            // Stream disposed out from under the read — expected.
        }
    }

    void Capture(string line) {
        lock (_diagnosticsGate) {
            if (_diagnostics.Length >= DiagnosticsCap) return;

            _diagnostics.Append(line).Append('\n');
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        try {
            if (timeout is { } t) {
                using var cts = new CancellationTokenSource(t);

                try {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // Timed out — return silently, per this method's contract.
                }
            } else {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        } catch {
            // Already exited or disposed — nothing left to wait for.
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        try {
            if (_process.HasExited) return;

            _process.Kill(entireProcessTree: true);
        } catch {
            // Already exited, already disposed (the contract explicitly permits this call after
            // DisposeAsync), or the kill raced the exit — nothing left to terminate either way.
            return;
        }

        await WaitForExitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // idempotent, per the interface contract

        try {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        } catch {
            // Best-effort — already exited or inaccessible.
        }

        try {
            await _stderrDrainCts.CancelAsync().ConfigureAwait(false);
        } catch {
            // Best-effort.
        }

        try {
            await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } catch {
            // DrainStderrAsync already swallows its expected exceptions; never let a stuck drain hang
            // or fault a dispose.
        }

        _stderrDrainCts.Dispose();

        try {
            _process.Dispose();
        } catch {
            // Best-effort.
        }
    }

    static int SafePid(Process process) {
        try {
            return process.Id;
        } catch {
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Antigravity turn stderr: {Length} chars")]
    partial void LogStderrShape(int length);
}
