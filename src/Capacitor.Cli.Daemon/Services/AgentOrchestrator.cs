using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal record AgentInstance(
        string                  Id,
        string?                 Prompt,
        string                  Model,
        string?                 Effort,
        string                  RepoPath,
        string                  Vendor,
        IHostedAgentRuntime     Runtime,
        WorktreeInfo            Worktree,
        CancellationTokenSource ReadCts
    ) {
    public string?              SessionId         { get; set; }
    public string               Status            { get; set; } = "Starting";
    public DateTime             CreatedAt         { get; init; } = DateTime.UtcNow;
    public DateTime             LastOutputAt      { get; set; } = DateTime.UtcNow;
    public bool                 HasReceivedOutput { get; set; }
    public TerminalOutputBuffer OutputBuffer      { get; } = new();

    /// <summary>Phase B (D2): the launch kind + (for a ReviewFlow launch) the flow identity,
    /// captured from <see cref="LaunchAgentCommand"/> at construction. Reported in
    /// <c>LiveAgents</c>/<c>DaemonStatusReport</c> so a restarted server can associate a surviving
    /// unassigned reviewer with its role. Defaults preserve pre-D2 behavior for any non-D2 launch path.</summary>
    public LaunchKind           Kind              { get; init; } = LaunchKind.Default;
    public string?              FlowRunId         { get; init; }
    public string?              FlowRole          { get; init; }

    /// <summary>The applied Codex sandbox/approval pair — the values actually passed to the vendor
    /// CLI, whether caller-selected or derived. Set only for an interactive Codex launch on a
    /// daemon-owned worktree; null everywhere else. Stored HERE (not recomputed at each send) so the
    /// initial registration and every reconnect re-registration report the same pair, which is what
    /// lets the value survive a server restart.</summary>
    public string?              SandboxPolicy     { get; init; }
    public string?              ApprovalPolicy    { get; init; }

    /// <summary>Phase B (D1): single-flight teardown latch — a plain field (not a property) so
    /// <see cref="System.Threading.Interlocked.CompareExchange(ref int,int,int)"/> can gate it. Exactly
    /// one teardown runs even if the launch-catch and the read-loop's finally race.</summary>
    public int CleanupStarted;

    /// <summary>Phase B (D4): the child's exact start-identity captured ONCE at spawn (the
    /// <c>ProcessStartToken</c>). Teardown uses THIS stored token — never a freshly-recaptured one — so a
    /// pid recycled after the child exited can't be adopted and later killed. Null only when the pid was
    /// never capturable (a non-live/degenerate pid — nothing to track).</summary>
    public string? StartIdentity { get; set; }

    /// <summary>Temp MCP config path written for hosted PR reviews; deleted on cleanup.</summary>
    public string? McpConfigPath { get; set; }

    /// <summary>The per-reviewer LocalPermissionBridge token URL minted for an unattended review-flow
    /// launch (null otherwise). Revoked on cleanup so the auto-approve path dies with the reviewer.</summary>
    public string? ReviewerBridgeToken { get; init; }

    /// <summary>
    /// Reason string sent to the server when ending the AgentSession. Defaults to
    /// "agent_exited" (claude exited on its own); HandleStopAgent flips it to
    /// "agent_stopped" so a user-initiated stop is still attributed correctly even
    /// if HandleStopAgent's own EndAgentSessionAsync call fails and the read-loop's
    /// finally-block call is the only one that lands.
    /// </summary>
    public string PendingEndReason { get; set; } = "agent_exited";

    // ── Local terminal attach (Phase 1) ──────────────────────────────────
    // Internal: these expose the daemon-internal ITerminalSink, so they can't be public
    // on this public record (CS0053). They're only touched inside the daemon assembly.
    /// <summary>Local-terminal clients attached over the control socket.</summary>
    internal List<ITerminalSink> LocalSinks { get; } = [];
    internal Lock                SinksLock  { get; } = new();
    /// <summary>Each attached local client's last-reported size, for the resize min-clamp.</summary>
    internal Dictionary<ITerminalSink, Dim> ClientDims { get; } = [];
    public readonly record struct Dim(ushort Cols, ushort Rows);

    /// <summary>The server-aggregated min size across all web viewers (one value per agent,
    /// computed server-side from per-connection web dims), folded into the same min-clamp as the
    /// local clients so a small web viewer and a large local terminal share the one PTY at the
    /// smallest size — tmux semantics across surfaces. <c>null</c> when no web viewer is
    /// attached, so the clamp grows back to the local-only size. Guarded by <see cref="SinksLock"/>.</summary>
    internal Dim? WebDims { get; set; }

    /// <summary>Tripped when the agent terminates (CleanupAgentAsync) so an attached local
    /// client that's blocked waiting on the user's keystrokes wakes, flushes the last output,
    /// and sends an Exited frame instead of hanging.</summary>
    internal CancellationTokenSource ExitedCts { get; } = new();

    /// <summary>
    /// True for locally-launched agents: the orchestrator makes no per-agent server call
    /// and does not attach the SignalR sink. An explicit share (Phase 2) clears this.
    /// </summary>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// True for agents started from a local terminal (`kcap agent start`), whether registered or
    /// `--private`. Such an agent has a live local terminal as its primary surface, so the read
    /// loop streams to the server <b>non-blocking</b> (drop+count on a full backlog) rather than
    /// back-pressuring the PTY on a remote tunnel stall — the local terminal must not freeze when
    /// the cloud hiccups. Hosted agents (server is the only consumer) keep lossless back-pressure.
    /// </summary>
    public bool IsLocalSpawned { get; init; }

    /// <summary>Owned worktree (daemon-created — safe to remove on cleanup) vs borrowed cwd
    /// (the user's own checkout — never removed).</summary>
    public WorkLocation Work { get; init; } = WorkLocation.OwnedWorktree;

    /// <summary>Authorized live checkout mirrored into this owned worktree for a runtime that
    /// cannot safely execute in-place. Refreshed before each later review round.</summary>
    public string? BorrowedSnapshotSource { get; init; }
    public SemaphoreSlim BorrowedSnapshotGate { get; } = new(1, 1);

    /// <summary>Current PTY dimensions — the single source of truth for every dims send
    /// (registration, reconnect). Updated by every resize path (local clamp + web resize).
    /// Hosted agents initialise these to the fixed HostedPtyCols/Rows; ushort read/write is
    /// atomic, and stale-by-one-resize is harmless for best-effort dims.</summary>
    public ushort CurrentCols { get; set; }
    public ushort CurrentRows { get; set; }

    /// <summary>
    /// The live ACP transcript forwarder for this agent, set once
    /// <see cref="AgentOrchestrator.HandleLaunchAgent"/>'s post-registration bind
    /// (<c>AcpSessionStarted</c>) succeeds and the forwarder is constructed. <see langword="null"/>
    /// for every PTY agent (claude/codex — <see cref="HostedRuntimeStart.Transcript"/> is null for
    /// them) and for an ACP agent whose initial bind failed (nothing to drain in that case — see
    /// <see cref="AgentOrchestrator.StartAcpForwardingAsync"/>). Read by
    /// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> to run the bounded final-drain before
    /// ending the session.
    /// </summary>
    public AcpForwarderHandle? AcpForwarder { get; set; }

    /// <summary>
    /// Per-agent/per-setup <see cref="CancellationTokenSource"/>, linked to the daemon's shutdown
    /// token, created in <see cref="AgentOrchestrator.HandleLaunchAgent"/> BEFORE the fire-and-forget
    /// <see cref="AgentOrchestrator.StartAcpForwardingAsync"/> call — so it exists immediately,
    /// independent of whether the bind ever resolves. Both the bind/setup task and (once started) the
    /// forwarder's run task use ITS token rather than the raw daemon-wide shutdown token, so
    /// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> can cancel just this agent's ACP work
    /// (on drain-timeout, and unconditionally at finalize) without touching any other agent or the
    /// daemon's own shutdown gate. <see langword="null"/> for every PTY agent (claude/codex) and set
    /// exactly once, at launch, for every ACP-capable runtime — never re-created for the same agent.
    /// </summary>
    public CancellationTokenSource? AcpCts { get; set; }
}

/// <summary>
/// Pairs a started <see cref="AcpTranscriptForwarder"/> with its fire-and-forget run task
/// (<see cref="AgentOrchestrator.ForwardAcpTranscriptAsync"/>'s return
/// value) so <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> can await the SAME task (bounded)
/// at teardown without re-deriving or re-wrapping it.
/// </summary>
internal sealed record AcpForwarderHandle(AcpTranscriptForwarder Forwarder, Task RunTask);

/// <summary>Ring buffer that keeps the last 2 MB of terminal output.</summary>
public class TerminalOutputBuffer {
    readonly List<byte[]> _chunks = [];
    int                   _totalBytes;
    const int             MaxBytes = 2 * 1024 * 1024;

    public void Append(byte[] data) {
        lock (_chunks) {
            _chunks.Add(data);
            _totalBytes += data.Length;

            while (_totalBytes > MaxBytes && _chunks.Count > 1) {
                _totalBytes -= _chunks[0].Length;
                _chunks.RemoveAt(0);
            }
        }
    }

    public List<byte[]> GetAll() {
        lock (_chunks) { return [.._chunks]; }
    }

    /// <summary>Flattens the retained ring into one buffer for a one-time replay to a
    /// newly-attached client (bounded by <see cref="MaxBytes"/>).</summary>
    public byte[] Snapshot() {
        lock (_chunks) {
            var ms = new MemoryStream(_totalBytes);
            foreach (var c in _chunks) ms.Write(c);

            return ms.ToArray();
        }
    }
}

internal partial class AgentOrchestrator : IAsyncDisposable {
    readonly ConcurrentDictionary<string, AgentInstance>       _agents = new();

    // Phase B (D4): durable PID records + this daemon's logical identity/epoch for
    // crash-survivor reaping. Initialized in the ctor from config.
    AgentPidRecordStore? _pidRecords;
    AgentKillQuarantine? _quarantine;
    OrphanReaper?        _orphanReaper;

    // Tail-of-PTY capture for a FAILED launch, under the same per-daemon record root as the PID
    // records ({state}/{name}/agents/failed/) — survives worktree teardown for post-mortem.
    FailedLaunchLog?     _failedLaunchLog;
    string               _daemonId    = "";
    string               _daemonEpoch = "";

    // Phase B2-b (sequenced-settlement design §4.2.4): the durable positive-death-evidence outbox.
    // Fed at every CONFIRMED-gone seam — the OrphanReaper record pass (Hook A), the quarantine drain
    // (Hook B), and the StopAgent-fallback reap (Hook C) — always ledger-append BEFORE source-delete so
    // a crash between the two re-derives from the leftover source and Upsert (idempotent on the
    // source-stable (AgentId, OldEpoch) key) collapses onto the committed entry. Lives in the same
    // state dir as _pidRecords, under the same atomic temp+rename discipline.
    readonly ResolvedCandidatesLedger? _resolvedLedger;

    // Phase B2-b (sequenced-settlement design §4.2.4): the durable marker-candidate source store for a
    // RECORDLESS prior-epoch survivor (Hook D). The env-marker scan persists a source BEFORE the kill and
    // resolves it through the (a)/(b)/(c) matrix; on confirmed death it emits into _resolvedLedger with a
    // NULL flow (the env is untrusted) unless a co-existing durable RECORD supplies a trusted flow.
    readonly MarkerCandidateStore? _markerCandidates;

    // Phase B2-b (sequenced-settlement design §4.2.3): the durable coverage boot-chain verdict,
    // folded in DaemonRunner (before Connect) and stashed on config. Advertised on the enriched
    // DaemonConnect payload; a Linux/macOS value is inert (the server consumes it only on Windows).
    readonly bool        _recordlessSurvivorsImpossible;

    // Phase B2-b (sequenced-settlement design §4.2.2): the epoch-scoped sequenced-command handler.
    // Owns the two serialized lanes + the contiguous-prefix watermark; injected with this orchestrator's
    // ReadLiveness / the server's CommandAck+CommandRejected sends so it stays unit-testable without a
    // live hub. Only Seq'd LaunchAgentCommand + StopAgentV2 route through it; un-Seq'd commands stay on
    // the legacy unsequenced lane (old-server compat) and never advance the watermark.
    readonly SequencedCommandProcessor? _processor;

    // Phase B (D4 §6.4(2a)/(3)): single-flight latches so a slow sweep (each survivor consumes a
    // ~5s TERM grace sequentially) can't overlap itself when the next heartbeat tick fires — otherwise
    // sweeps accumulate, double-signal, and re-scan /proc concurrently. A tick whose prior sweep is still
    // running is simply skipped. 0 = idle, 1 = running (Interlocked-gated).
    int _orphanSweepRunning;
    int _quarantineSweepRunning;
    readonly DaemonConfig                                      _config;
    readonly ServerConnection                                  _server;
    readonly WorktreeManager                                   _worktreeManager;
    readonly RepoMatcher                                       _repoMatcher;
    readonly IPtyProcessFactory                                _ptyFactory;
    readonly IHttpClientFactory                                _httpClientFactory;
    readonly LocalPermissionBridge                             _permissionBridge;
    readonly IReadOnlyDictionary<string, IHostedAgentLauncher> _launchers;
    readonly IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> _runtimeFactories;
    readonly ILogger<AgentOrchestrator>                        _logger;

    /// <summary>Serialises + coalesces the background capability refresh fired after a certification
    /// rejection. See SingleFlightRefresh for why bare fire-and-forget was unsafe here.</summary>
    readonly SingleFlightRefresh _capabilityRefresh = new();

    // Hosted-agent PTYs are spawned at a fixed size and never resized. The daemon
    // reports these dims to the server right after the agent registers (and on
    // reconnect) so the read-only viewers (web/desktop xterm) lock to exactly the
    // width Claude drew for — otherwise the viewer auto-fits its panel and the
    // mismatched columns garble the TUI. PtyDefaults is the single source
    // of truth, shared with IPtyProcessFactory.Spawn's defaults so they can't drift.
    const ushort HostedPtyCols = PtyDefaults.Cols;
    const ushort HostedPtyRows = PtyDefaults.Rows;

    readonly PeriodicTimer _heartbeatTimer = new(TimeSpan.FromSeconds(30));

    // heartbeat tightened from 60 s SendAsync to round-trip Ping.
    // tick halved (15 → 7 s) and deadline halved (10 → 5 s) so a
    // displaced-slot mismatch or a hung transport is caught within ~10 s
    // instead of ~25 s. This is independent of SignalR's transport timeout
    // (which stays at the 30 s default) — the heartbeat is the daemon's
    // application-level liveness probe.
    readonly PeriodicTimer _daemonHeartbeat = new(TimeSpan.FromSeconds(7));

    static readonly TimeSpan PingDeadline = TimeSpan.FromSeconds(5);

    // proactively refresh the active profile's auth token ahead of expiry so a
    // continuously-running daemon keeps a WorkOS sliding-inactivity session alive (up to its
    // absolute lifetime) rather than forcing a `kcap login` after an idle period. The tick is
    // cheap (a token-file read + expiry compare) and only calls the refresh endpoint when the
    // token is within ProactiveRefreshWindow of expiry; TokenRefreshLoop further rate-limits
    // attempts to at most one per ProactiveRefreshMinInterval, so refresh traffic stays bounded
    // even for a failing refresh or a short-lived token that keeps re-entering the window.
    readonly PeriodicTimer _tokenRefresh = new(TimeSpan.FromSeconds(60));

    // Task 12: periodic sweep of the cross-vendor lifecycle + transcript spools. Covers
    // backlogs left behind by vendors whose session-end never fires another `kcap` hook process
    // (Kiro/OpenCode watcher-owned session-end, Antigravity/Codex-desktop GUI idle/parent-exit) —
    // see SpoolDrainLoop's doc comment. 60s mirrors the reaper-style cadence of the other timers;
    // the drain's own per-tick budget keeps a slow/unreachable server from stalling the daemon.
    readonly PeriodicTimer _spoolDrain = new(TimeSpan.FromSeconds(60));

    // Refresh once the token is within this much of its expiry. Comfortably above the 60 s tick
    // so the window is never stepped over.
    static readonly TimeSpan ProactiveRefreshWindow = TimeSpan.FromMinutes(5);

    // Hit the refresh endpoint at most once per this interval (see TokenRefreshLoop). Small
    // enough that a healthy token issued with a short lifetime is still renewed before it
    // lapses during idle; large enough that a dead/rotated refresh token isn't re-hit every
    // tick.
    static readonly TimeSpan ProactiveRefreshMinInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long <see cref="FinalizeAgentRunAsync"/> waits on the (reconnect-retrying)
    /// EndAgentSession call before proceeding with local cleanup regardless. Covers a
    /// typical transient SignalR blip with margin; a longer outage proceeds to cleanup
    /// while the retry continues in the background. Settable so tests don't wait 30s.
    /// </summary>
    internal TimeSpan EndAgentSessionBudget { get; set; } = TimeSpan.FromSeconds(30);

    // Linked to IHostApplicationLifetime.ApplicationStopping so the shutdown gate
    // trips as soon as the host begins stopping — the same instant ServerConnection's
    // _ct (also ApplicationStopping) cancels SignalR calls. Otherwise there's a
    // window between ApplicationStopping firing and DisposeAsync running where
    // server calls would still throw TaskCanceledException unguarded.
    readonly CancellationTokenSource _shutdownCts;

    public AgentOrchestrator(
            DaemonConfig                                      config,
            ServerConnection                                  server,
            WorktreeManager                                   worktreeManager,
            RepoMatcher                                       repoMatcher,
            IPtyProcessFactory                                ptyFactory,
            IHttpClientFactory                                httpClientFactory,
            LocalPermissionBridge                             permissionBridge,
            IReadOnlyDictionary<string, IHostedAgentLauncher> launchers,
            IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> runtimeFactories,
            IHostApplicationLifetime                          lifetime,
            ILogger<AgentOrchestrator>                        logger
        ) {
        _shutdownCts       = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _config            = config;
        _server            = server;
        _worktreeManager   = worktreeManager;
        _repoMatcher       = repoMatcher;
        _ptyFactory        = ptyFactory;
        _httpClientFactory = httpClientFactory;
        _permissionBridge  = permissionBridge;
        _launchers         = launchers;
        _runtimeFactories  = runtimeFactories;
        _logger            = logger;

        // Phase B (D4): per-daemon PID-record store + this daemon's logical id + boot epoch.
        // Records live under "{stateDir}/{name}/agents" so they are unambiguously THIS daemon's own
        // (the startup reap only touches its own leftovers). DaemonId is a stable per-name identity;
        // DaemonEpoch is fresh per boot so the env-marker scan can tell a prior incarnation's
        // survivors from the current incarnation's live children.
        var recordRoot = Path.Combine(
            config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name));
        _pidRecords  = new AgentPidRecordStore(recordRoot, logger);
        _failedLaunchLog = new FailedLaunchLog(recordRoot);
        _quarantine  = new AgentKillQuarantine(logger);
        _daemonId    = ComputeDaemonId(config.Name);
        _daemonEpoch = config.DaemonEpoch ?? Guid.NewGuid().ToString("N");
        _recordlessSurvivorsImpossible = config.RecordlessSurvivorsImpossible;

        // Phase B2-b (sequenced-settlement design §4.2.4): the resolved-candidates ledger shares the PID
        // record root so all durable per-daemon state lives together. The OrphanReaper's record-pass
        // callback (Hook A) emits into it before the source delete; the drain (Hook B) and StopAgent
        // fallback (Hook C) below emit directly. All three are append-before-delete + idempotent.
        _resolvedLedger = new ResolvedCandidatesLedger(recordRoot, logger);
        // Phase B2-b (sequenced-settlement design §4.2.4): the marker-candidate source store (Hook D)
        // shares the same record root. A recordless survivor resolves with a NULL flow (env untrusted);
        // a co-existing durable record's TRUSTED flow is routed through onRecordResolved by EmitAndClear.
        _markerCandidates = new MarkerCandidateStore(recordRoot, logger);
        _orphanReaper = new OrphanReaper(_pidRecords, _daemonId, _daemonEpoch, logger,
            onRecordResolved: (a, e, fr, role) => _resolvedLedger?.Upsert(a, e, fr, role),
            markerStore: _markerCandidates,
            onMarkerResolved: (a, e) => _resolvedLedger?.Upsert(a, e, null, null));

        // Phase B2-b (sequenced-settlement design §4.2.2): the epoch-scoped sequenced-command processor.
        // Scoped to the shipped per-boot _daemonEpoch; ReadLiveness gives it the confirmed-death-precedence
        // liveness read a duplicate CommandAck needs, and the two server sends are its ack/reject channels.
        _processor = new SequencedCommandProcessor(
            _daemonEpoch, ReadLiveness, _server.CommandAckAsync, _server.CommandRejectedAsync, logger);

        // Wire up server commands
        _server.OnLaunchAgent            += HandleLaunchAgent;
        _server.OnStopAgent              += HandleStopAgent;
        _server.OnSendInput              += HandleSendInput;
        _server.OnSendSpecialKey         += HandleSendSpecialKey;
        _server.OnResizeTerminal         += HandleResizeTerminal;
        _server.ReRegisterAgentsHook          =  ReRegisterAgentsAsync;
        _server.FindRepoForRemoteHandler      =  HandleFindRepoForRemote;
        _server.ProbeBorrowSourceHandler      =  HandleProbeBorrowSource;
        // Task 8: the side-effect-free reviewer-model preflight. Pure resolution over the
        // advertised resolvers — no subprocess/worktree/config side effects.
        _server.ResolveReviewerModelHandler   =  req => Task.FromResult(HandleResolveReviewerModel(req));

        // Phase B2-b (sequenced-settlement design §4.2.4): the server prunes the resolved-candidates
        // ledger per-entry via AckResolvedCandidates (synchronous void handler); the connect payload
        // re-advertises the un-acked snapshot alongside the periodic DaemonStatusReport.
        _server.OnAckResolvedCandidates       += HandleAckResolvedCandidates;
        _server.GetResolvedStartupCandidates  =  () => [.. _resolvedLedger?.Snapshot() ?? []];
        // Phase B2-b (sequenced-settlement design §5.5): advertise the ledger's monotonic high-water on
        // the connect payload (BuildStatusReport carries it on the periodic self-report) so the server
        // learns the generation frontier even after sparse acks prune entries.
        _server.GetHighestResolutionGeneration =  () => _resolvedLedger?.HighestResolutionGeneration;

        // Phase B2-b (sequenced-settlement design): mirror the per-platform startup-completeness signals
        // into the DaemonConnect payload (the periodic DaemonStatusReport carries them via
        // BuildStatusReport). Additive/inert until the paired server PR consumes them; finalized
        // alongside the sequenced counters in a later task.
        _server.GetStartupReapComplete         =  ComputeStartupReapComplete;
        _server.GetUnresolvedStartupCandidates =  () => [.. _orphanReaper?.BlockedCandidates() ?? []];
        _server.GetStartupDiscovery            =  () => _orphanReaper?.CurrentDiscovery;

        // Phase B2-b (sequenced-settlement design §4.2.2): route the sequenced-command receive seams and
        // mirror the watermark counters + kill-quarantine snapshot onto the connect payload. StopAgentV2
        // goes through the processor's serial lane; AckProcessedPrefix retires identity-cache entries;
        // RequestStatusReport is answered by an immediate out-of-band DaemonStatusReport.
        _server.OnStopAgentV2          += HandleStopAgentV2;
        _server.OnAckProcessedPrefix   += ack => _processor?.AckPrefix(ack);
        _server.OnRequestStatusReport  += SendDaemonStatusReportOnceAsync;
        _server.GetHighestAcceptedSeq  =  () => _processor?.HighestAcceptedSeq;
        _server.GetLastProcessedSeq    =  () => _processor?.LastProcessedSeq;
        _server.GetQuarantined         =  () => [.. QuarantineSnapshot()];
        // Phase B2-b (sequenced-settlement design): the DaemonConnect epoch reads THIS orchestrator's
        // per-boot _daemonEpoch (the same source the processor is scoped to), so the advertised epoch
        // can't diverge from it even if config.DaemonEpoch were left unpinned (tests).
        _server.GetDaemonEpoch         =  () => _daemonEpoch;

        _server.GetLiveAgentIds = () => [
            .. _agents
                .Where(kvp => (kvp.Value.Status is "Starting" or "Running") && !kvp.Value.IsPrivate)
                .Select(kvp => kvp.Key)
        ];

        // Phase B (D2): richer live-agent metadata (kind + flow identity) alongside the ids.
        _server.GetLiveAgents = () => [.. BuildLiveAgents()];

        // Start heartbeat loops
        _ = RunHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunDaemonHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunTokenRefreshLoopAsync(_shutdownCts.Token);
        _ = RunSpoolDrainLoopAsync(_shutdownCts.Token);
        _ = RunDaemonStatusReportLoopAsync(_shutdownCts.Token); // Phase B (D2): periodic self-report
    }

    internal int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");

    /// <summary>Phase B (D3): clock seam so the reviewer-TTL heartbeat check is testable with a
    /// fixed time. Production uses the real UTC clock.</summary>
    internal Func<DateTime> ClockUtc { get; set; } = () => DateTime.UtcNow;

    /// <summary>Phase B (D3): the ReviewFlow agents the heartbeat should reap now — past
    /// <see cref="DaemonConfig.ReviewerMaxLifetime"/> (reason <c>reviewer_ttl_expired</c>) or
    /// <see cref="DaemonConfig.ReviewerIdleTimeout"/> (reason <c>reviewer_idle_expired</c>). Only
    /// Running ReviewFlow agents; a <see cref="TimeSpan.Zero"/> bound disables it; interactive agents
    /// are never returned. Pure (no side effects) so the heartbeat and tests share one decision.</summary>
    internal IReadOnlyList<(string Id, string Reason)> FindReviewersToReap() {
        var now    = ClockUtc();
        var result = new List<(string, string)>();

        foreach (var a in _agents.Values) {
            if (a.Kind != LaunchKind.ReviewFlow || a.Status != "Running") continue;

            if (_config.ReviewerMaxLifetime > TimeSpan.Zero && now - a.CreatedAt > _config.ReviewerMaxLifetime)
                result.Add((a.Id, "reviewer_ttl_expired"));
            else if (_config.ReviewerIdleTimeout > TimeSpan.Zero && now - a.LastOutputAt > _config.ReviewerIdleTimeout)
                result.Add((a.Id, "reviewer_idle_expired"));
        }

        return result;
    }

    /// <summary>Phase B (D2): the daemon's self-report snapshot — its authoritative
    /// <see cref="ActiveCount"/> plus the live-agent metadata (and, once D4/Task 8 lands, the
    /// kill-quarantine). Pure; the send loop + tests share it.</summary>
    internal DaemonStatusReport BuildStatusReport() =>
        new(ActiveCount, [.. BuildLiveAgents()], [.. QuarantineSnapshot()],
            // Phase B2-b (sequenced-settlement design §4.2.4): re-advertise the durable resolved-
            // candidates ledger on every self-report until the server prunes it per-entry via
            // AckResolvedCandidates. Epoch is the shipped per-boot _daemonEpoch.
            Epoch: _daemonEpoch,
            // Phase B2-b (sequenced-settlement design §4.2.2): the sequenced-command watermark counters
            // from the processor (LastProcessedSeq = contiguous terminal prefix; HighestAcceptedSeq =
            // highest accepted). Null before the processor exists; 0 on a fresh epoch (nothing accepted).
            LastProcessedSeq: _processor?.LastProcessedSeq,
            HighestAcceptedSeq: _processor?.HighestAcceptedSeq,
            // Phase B2-b (sequenced-settlement design): the per-platform startup-completeness signals.
            // StartupReapComplete is a computed roll-up; UnresolvedStartupCandidates always lists the
            // blocked known-id set so a completion-false report carries its reason; StartupDiscovery
            // surfaces the recordless-survivor marker-scan state (Pending/Complete/Failed on Linux,
            // NotApplicable off it). Additive/inert until the paired server PR consumes them.
            StartupReapComplete: ComputeStartupReapComplete(),
            ResolvedStartupCandidates: [.. _resolvedLedger?.Snapshot() ?? []],
            UnresolvedStartupCandidates: [.. _orphanReaper?.BlockedCandidates() ?? []],
            StartupDiscovery: _orphanReaper?.CurrentDiscovery,
            // Phase B2-b (sequenced-settlement design §5.5): the resolved-candidates ledger's monotonic
            // high-water, so once sparse acks prune entries the server still knows the generation frontier.
            HighestResolutionGeneration: _resolvedLedger?.HighestResolutionGeneration);

    /// <summary>Phase B2-b (sequenced-settlement design): the per-platform startup-reap-complete
    /// roll-up. A blocked known-id candidate (pending_marker / legacy_unresolvable /
    /// identity_unresolvable) always keeps it false. Otherwise completion is platform-specific:
    /// <list type="bullet">
    /// <item><b>Linux</b> — needs BOTH no blocked candidates AND one clean env-marker-scan pass
    /// (<see cref="MarkerScanState.Complete"/>): the scan is the only proof a recordless survivor was
    /// enumerated.</item>
    /// <item><b>Windows with <c>RecordlessSurvivorsImpossible</c></b> — trivially complete once the
    /// record pass leaves nothing blocked (the boot-chain attestation proves no recordless class
    /// exists).</item>
    /// <item><b>pre-W1 Windows + macOS</b> — record-pass-only completion (a weakened proof: there is no
    /// scan and no boot-chain guarantee, so "no blocked record-tracked candidates" is the best available
    /// signal).</item>
    /// </list></summary>
    internal bool ComputeStartupReapComplete() {
        var blocked = _orphanReaper?.BlockedCandidates().Count ?? 0;
        if (blocked > 0) return false;
        if (OperatingSystem.IsLinux())
            return _orphanReaper?.CurrentDiscovery.MarkerScanState == MarkerScanState.Complete;
        if (OperatingSystem.IsWindows() && _recordlessSurvivorsImpossible) return true;
        return true; // pre-W1 Windows / macOS: record-pass-only completion (no blocked record-tracked candidates)
    }

    /// <summary>Phase B2-b (sequenced-settlement design §5.5/§4.2.2): the single lifecycle-state read
    /// (confirmed-death precedence Live &gt; Quarantined &gt; Dead) over the same collections
    /// <see cref="CleanupAgentAsync"/> + <see cref="AgentKillQuarantine"/> mutate. The design mandates that a
    /// duplicate CommandAck's CurrentState be read so a teardown racing the read can NEVER surface a transient
    /// false Dead. This read is lock-free (it does not take the per-agent lifecycle lock) and is SOUND ONLY
    /// BECAUSE OF THE SHIPPED CleanupAgentAsync ORDERING INVARIANT: the confirmed-death teardown adds the
    /// surviving child to <c>_quarantine</c> BEFORE removing it from <c>_agents</c> (AgentOrchestrator.cs —
    /// "Add to quarantine BEFORE removing from _agents so EffectiveCount never dips"), so an agent is
    /// CONTINUOUSLY present in <c>_agents ∪ _quarantine</c> from spawn until its quarantine entry is drained
    /// (RetryQuarantineOnceAsync) — there is no window where a live/tearing-down agent is absent from both,
    /// hence no transient false Dead. Dead is returned only after the genuine drain (confirmed death). If that
    /// ordering invariant is ever broken, this must instead take the per-agent lifecycle lock. NotFound
    /// collapses to Dead here (see the appendix note) — both satisfy confirmed-absence.</summary>
    /// <summary>Evaluates the reviewer certification arm-by-arm so a rejection can NAME the one that
    /// failed. Extracted and internal so the arms are unit-testable — the previous inline expression
    /// collapsed four distinguishable conditions into one boolean and one message, and that message
    /// actively misdirected: it reported the certification revision (which matches on every one of
    /// these paths) and told the operator to update a CLI that was usually fine.</summary>
    internal static (bool Ok, string Reason) EvaluateReviewerCertification(
            string vendor, string? probedVersion, string? currentConnectionId,
            ReviewerCertificationRequirement certification) {
        if (!string.Equals(certification.Vendor, vendor, StringComparison.Ordinal))
            return (false, $"the launch is for '{vendor}' but the certification is for '{certification.Vendor}'");

        if (!string.Equals(currentConnectionId, certification.ExpectedDaemonConnectionId, StringComparison.Ordinal))
            return (false, "this daemon reconnected after the certification was issued " +
                           "(connection id changed) — retry the flow");

        if (!string.Equals(certification.RequiredLauncherPolicyVersion,
                DaemonRunner.ClaudeLauncherPolicyVersion, StringComparison.Ordinal))
            return (false, $"the server requires launcher policy '{certification.RequiredLauncherPolicyVersion}' " +
                           $"but this daemon implements '{DaemonRunner.ClaudeLauncherPolicyVersion}' — " +
                           "update kcap and restart the daemon");

        // A NULL advertised version means the registration-time probe failed — a transient condition,
        // not evidence the CLI changed. This arm exists to catch a CLI SWAP between advertisement and
        // launch, and null-vs-value is not a swap. Treating it as one rejected every launch for the
        // daemon's lifetime, and restarting on a loaded host merely re-poisoned the advertisement.
        // A null advertised value falls through to the range check below, which is the real gate.
        // A failed LAUNCH probe is its own condition, classified before the swap arm: otherwise a
        // timeout reads as "the CLI changed, restart", when nothing changed and restarting repeats
        // it. Fails closed either way; only the diagnosis and remedy differ.
        if (string.IsNullOrEmpty(probedVersion))
            return (false, $"could not read the installed {vendor} CLI version (the version probe " +
                           "failed or timed out) — this is usually transient under load; retry the flow");

        // Range BEFORE swap: an out-of-range replacement would otherwise be told only to restart,
        // and restarting re-advertises the same out-of-range version.
        if (!DaemonRunner.CliVersionAllowed(probedVersion, certification.AllowedCliRanges))
            return (false, $"the installed {vendor} CLI '{probedVersion}' is outside the " +
                           $"server's allowed range '{certification.AllowedCliRanges}'");

        // A missing advertised version means the registration probe failed — not evidence of a CLI
        // swap, which is all this arm exists to catch. It falls through to the range check above.
        // Null or empty: declared non-nullable, but populated from a probe returning null over JSON.
        if (!string.IsNullOrEmpty(certification.ExpectedCliVersion) &&
            !string.Equals(probedVersion, certification.ExpectedCliVersion, StringComparison.Ordinal))
            return (false, $"the installed {vendor} CLI is '{probedVersion}' but this daemon " +
                           $"advertised '{certification.ExpectedCliVersion}' at registration — " +
                           "restart the daemon so it re-advertises");

        return (true, "");
    }

    internal AgentLiveness ReadLiveness(string agentId) {
        // Order matters: check _agents first (Live/Quarantined-by-status), then _quarantine, then Dead.
        // The add-to-quarantine-before-remove-from-_agents invariant makes this ordering false-Dead-free.
        if (_agents.TryGetValue(agentId, out var a))
            return a.Status is "Starting" or "Running" ? AgentLiveness.Live : AgentLiveness.Quarantined;
        if (_quarantine?.IsQuarantined(agentId) == true) return AgentLiveness.Quarantined;
        return AgentLiveness.Dead;
    }

    /// <summary>Phase B (D4 §6.4(2a)): the kill-quarantine snapshot for the status report.</summary>
    internal IReadOnlyList<QuarantinedAgentInfo> QuarantineSnapshot() => _quarantine?.Snapshot() ?? [];

    /// <summary>Phase B (D4 §6.4(2a)): the daemon's admission gate — EVERY live registry entry
    /// (not just Starting/Running — a Completed/Failed agent still mid-teardown holds its slot until
    /// CleanupAgentAsync's count-preserving remove) PLUS unconfirmed-death quarantined ones. Using the
    /// full <c>_agents.Count</c> (rather than <see cref="ActiveCount"/>, whose Starting/Running meaning is
    /// the wire contract) keeps a slot reserved across the whole teardown, so a concurrent launch can't
    /// observe a transiently-freed slot and over-admit. A persistent kill/record-write failure shrinks
    /// admission (fails closed) rather than minting processes beyond the budget.</summary>
    internal int EffectiveCount => _agents.Count + (_quarantine?.Count ?? 0);

    /// <summary>Phase B (D4): this daemon's stable logical id = a hash of its name, written
    /// into each child's <c>KCAP_DAEMON_ID</c> marker. Per-name so a different daemon under the same
    /// user is never mistaken for ours by the env-marker scan.</summary>
    static string ComputeDaemonId(string name) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name ?? "")))
        [..16].ToLowerInvariant();

    /// <summary>Phase B (D4 §6.4(2)/(2a)); L1-managed(b) extends this for Unix: capture the child's
    /// EXACT start-identity ONCE, store it on the agent (teardown reuses it — never re-captures a
    /// possibly-recycled pid), and persist the durable PID record FAIL-CLOSED. A write failure (I/O)
    /// or a live-but-unidentifiable child THROWS — caught by the post-insert single-flight cleanup, so
    /// a spawned child we cannot durably track never stays admitted holding capacity. A non-live/
    /// degenerate pid (already gone, or pid&lt;=0) has nothing to track, so it returns cleanly rather
    /// than failing an already-doomed launch.
    ///
    /// <paramref name="capturedStartIdentity"/> is the runtime's own natively-captured identity
    /// (<see cref="IHostedAgentRuntime.StartIdentity"/>) — non-null on Unix (post-L1): the shim
    /// captures (or definitively fails to capture) identity INSIDE pty_spawn, immediately post-fork,
    /// which is the ONLY correct place to read it (the capture-binding rule — a post-hoc re-capture
    /// here could adopt an unrelated process if the pid was already recycled by the time we get here).
    /// Null means the runtime never captures this way (Windows; the ACP runtime has no PTY at all) —
    /// that path falls back to the ORIGINAL post-hoc <see cref="ProcessIdentity.Capture"/>, unchanged
    /// from before this task.</summary>
    void PersistPidRecordOrThrow(AgentInstance agent, int pid, string? capturedStartIdentity) {
        if (_pidRecords is null) return;

        if (capturedStartIdentity is not null) {
            // Unix (post-L1): NEVER re-capture — capturedStartIdentity is already the exact token
            // read by the shim immediately after the child existed. "" is a deliberate,
            // well-formed "identity_unavailable" record (capture was attempted and failed), NOT a
            // launch failure — see UnixPtyProcess's design note for why agent.StartIdentity is set
            // to "" rather than left null: CleanupAgentAsync's teardown check
            // (`agent.StartIdentity is { } startIdentity && ... MatchesTri(pid, startIdentity) != false`)
            // treats "" as permanently uncomparable (MatchesTri returns null — no ':' scheme
            // separator), which is exactly "ambiguity never kills": a still-alive
            // identity-unavailable agent gets quarantined and retried, never silently dropped.
            //
            // M1-A (spec §4.3): the record's identity_kind makes "" a well-formed, distinguishable
            // IdentityUnavailable marker rather than an inferred-from-emptiness convention.
            agent.StartIdentity = capturedStartIdentity; // "" is intentional, not a bug

            var identityKind = capturedStartIdentity.Length == 0
                ? PidIdentityKind.IdentityUnavailable
                : PidIdentityKind.Present;

            _pidRecords.Write(new AgentPidRecord(
                agent.Id, pid, capturedStartIdentity, identityKind, agent.Kind.ToString(), agent.Vendor,
                agent.FlowRunId, agent.FlowRole, _daemonId, _daemonEpoch, DateTimeOffset.UtcNow));

            return;
        }

        // Legacy path (Windows / ACP runtimes with no shim-based capture): unchanged behavior.
        var identity = ProcessIdentity.Capture(pid);
        if (identity is null) {
            if (ProcessIdentity.IsAlive(pid))
                throw new InvalidOperationException(
                    $"Could not capture start-identity for live agent {agent.Id} (pid {pid}) — failing launch closed");

            return; // no live capturable process → nothing to record or leak
        }

        agent.StartIdentity = identity;

        // Write throws on I/O failure → propagates → post-insert single-flight cleanup (fail closed):
        // a spawned, capturable child we cannot durably record must not stay admitted without a record.
        // This legacy path only reaches here with a non-null capture (see the guard above) — always Present.
        _pidRecords.Write(new AgentPidRecord(
            agent.Id, pid, identity, PidIdentityKind.Present, agent.Kind.ToString(), agent.Vendor,
            agent.FlowRunId, agent.FlowRole, _daemonId, _daemonEpoch, DateTimeOffset.UtcNow));
    }

    /// <summary>Delete an agent's PID record after its death is confirmed (teardown / confirmed reap).</summary>
    void DeletePidRecord(string agentId) => _pidRecords?.Delete(agentId);

    /// <summary>Test seams (this daemon's PID-record store) so a unit test can seed/inspect records
    /// without a real launch. Never used in production.</summary>
    internal void WritePidRecordForTest(AgentPidRecord record)     => _pidRecords?.Write(record);
    internal IReadOnlyList<AgentPidRecord> PidRecordsForTest()      => _pidRecords?.ReadAll() ?? [];
    internal string DaemonIdForTest                                 => _daemonId;
    internal string DaemonEpochForTest                             => _daemonEpoch;
    internal bool   RecordlessSurvivorsImpossibleForTest           => _recordlessSurvivorsImpossible;

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.4): the resolved-candidates ledger's
    /// un-acked snapshot, so a test can assert the confirmed-gone hooks (quarantine drain / StopAgent
    /// fallback / record pass) emitted positive per-id death evidence. Never used in production.</summary>
    internal IReadOnlyList<ResolvedStartupCandidate> ResolvedLedgerSnapshotForTest => _resolvedLedger?.Snapshot() ?? [];

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.4): honor the server's per-entry
    /// AckResolvedCandidates prune (sparse, deliver-once). SYNCHRONOUS — the ledger's <c>Ack</c> is a
    /// synchronous void, so this stays void (a <c>void</c> event/receive seam is never awaited).</summary>
    internal void HandleAckResolvedCandidates(AckResolvedCandidates ack) => _resolvedLedger?.Ack(ack.Entries ?? []);

    /// <summary>Test seam: seed a resolved-candidate ledger entry so a test can drive the advertise/ack
    /// prune path without a real confirmed-death hook. Never used in production.</summary>
    internal ResolvedStartupCandidate SeedResolvedCandidateForTest(string agentId, string oldEpoch)
        => _resolvedLedger!.Upsert(agentId, oldEpoch, null, null);

    /// <summary>Test seam: route an ack through the SYNCHRONOUS <see cref="HandleAckResolvedCandidates"/>
    /// handler (no await — the ledger Ack is void). Never used in production.</summary>
    internal void HandleAckResolvedCandidatesForTest(AckResolvedCandidates ack) => HandleAckResolvedCandidates(ack);

    /// <summary>Test seam: seed a kill-quarantine entry so a test can drive the drain hook without a
    /// real launch+teardown. Mirrors <see cref="WritePidRecordForTest"/>; never used in production.</summary>
    internal void QuarantineForTest(AgentKillQuarantine.Entry entry) => _quarantine?.Add(entry);

    /// <summary>Phase B2-b (sequenced-settlement design): test seam — persist a marker-candidate source
    /// so a test can assert it surfaces as a <c>pending_marker</c> blocked candidate (keeping
    /// <see cref="ComputeStartupReapComplete"/> false) without a real recordless survivor. The pid is
    /// irrelevant: <see cref="OrphanReaper.BlockedCandidates"/> lists every persisted source WITHOUT a
    /// liveness check, and the assertion reads the blocked surface directly via
    /// <see cref="BuildStatusReport"/> (no scan runs, so the dead pid is never resolved away). Never used
    /// in production.</summary>
    internal void SeedPendingMarkerCandidateForTest(string agentId, string oldEpoch) =>
        _markerCandidates!.Write(new MarkerCandidate(agentId, _daemonId, oldEpoch, 999_999));

    /// <summary>Looks up a persisted PID record by agent id, or null if none exists. Read-only and
    /// policy-free — shared by the reap below and by the local-stop protection check in
    /// AgentOrchestrator.LocalIpc.cs, which decides whether to reap at all before this ever runs.</summary>
    internal AgentPidRecord? FindPidRecord(string agentId) {
        if (_pidRecords is null) return null;

        var record = _pidRecords.ReadAll().FirstOrDefault(r => r.AgentId == agentId);

        return record.AgentId == agentId ? record : null;
    }

    /// <summary>Phase B (D4 §6.4(3) StopAgent fallback): the caller had no in-memory agent for
    /// this id — consult the PID record and, if a live process still matches its EXACT identity (and,
    /// on Unix, carries the expected <c>KCAP_AGENT_ID</c> env — ambiguity spares), reap it by identity
    /// and delete the record on confirmed death. This makes the server's registry-independent S2 stop
    /// effective even against a NEW daemon incarnation that never knew the agent in memory.</summary>
    async Task<bool> TryStopByPidRecordAsync(string agentId) {
        if (FindPidRecord(agentId) is not { } record) return false;

        var confirmedGone = await ProcessReaper.ReapByRecordAsync(record, _logger, _shutdownCts.Token);
        if (confirmedGone) {
            // Phase B2-b (sequenced-settlement design §4.2.4) Hook C: ledger-append the positive per-id
            // death evidence (from the TRUSTED record — its epoch + flow identity) BEFORE deleting the
            // source record. A crash between the two leaves a committed entry + leftover record; the next
            // boot's OrphanReaper record pass re-derives it and Upsert (idempotent on the source-stable
            // (AgentId, OldEpoch) key) collapses onto the committed entry, then completes the delete.
            _resolvedLedger?.Upsert(agentId, record.DaemonEpoch, record.FlowRunId, record.FlowRole);
            _pidRecords?.Delete(agentId); // delete ONLY on confirmed death (spec §6.4(2))
        }

        return confirmedGone;
    }

    /// <summary>Phase B (D4 §6.4(3)): run the startup orphan reap once — called by DaemonRunner
    /// at boot under the daemon lock (next to WorktreeManager.CleanupOrphanedAsync), and re-run on each
    /// heartbeat tick. SINGLE-FLIGHT: if a prior sweep is still running (a long /proc scan + sequential
    /// TERM graces can outlast the 30s heartbeat, and the ctor-started heartbeat can overlap the boot
    /// call), this tick is skipped rather than piling on. Best-effort: a reaper fault is logged and
    /// swallowed, never faulting the caller.</summary>
    internal async Task ReapOrphansOnceAsync(CancellationToken ct = default) {
        if (_orphanReaper is null) return;
        if (Interlocked.CompareExchange(ref _orphanSweepRunning, 1, 0) != 0) return; // a sweep is already in flight

        try { await _orphanReaper.ReapOnceAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "OrphanReaper sweep faulted — continuing"); }
        finally { Interlocked.Exchange(ref _orphanSweepRunning, 0); }
    }

    /// <summary>Phase B (D4 §6.4(2a)): retry the kill-quarantine once — SINGLE-FLIGHT, mirroring
    /// <see cref="ReapOrphansOnceAsync"/>, so a slow retry (each entry a ~5s TERM grace) can't overlap the
    /// next heartbeat tick. Skipped when empty or already running.</summary>
    async Task RetryQuarantineOnceAsync(CancellationToken ct) {
        if (_quarantine is not { Count: > 0 }) return;
        if (Interlocked.CompareExchange(ref _quarantineSweepRunning, 1, 0) != 0) return;

        try {
            // Delete the durable PID record of every agent whose death the retry CONFIRMED — teardown
            // retained it (with the current epoch) while the child was quarantined, so without this it
            // would be skipped by the orphan sweep and leak until the next daemon restart.
            //
            // Phase B2-b (sequenced-settlement design §4.2.4) Hook B: for each drained (confirmed-dead)
            // entry, ledger-append its positive per-id death evidence BEFORE deleting the record. The
            // shipped _quarantine is current-incarnation only, so its entries carry the CURRENT epoch —
            // emit (AgentId, _daemonEpoch, flow…). That same-epoch id is harmless per outbox idempotency
            // (prior-epoch proofs come from the record pass + marker scan) and gives the server id-level
            // absence proof. Append-before-delete: a crash between the two leaves a committed entry + the
            // retained record (its DaemonEpoch == _daemonEpoch), which the next boot's OrphanReaper record
            // pass reconciles on the source-stable (AgentId, OldEpoch) key (single emit) then deletes.
            foreach (var e in await _quarantine.RetryAllAsync(ct)) {
                _resolvedLedger?.Upsert(e.AgentId, _daemonEpoch, e.FlowRunId, e.FlowRole);
                DeletePidRecord(e.AgentId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "quarantine retry sweep faulted — continuing"); }
        finally { Interlocked.Exchange(ref _quarantineSweepRunning, 0); }
    }

    /// <summary>Phase B (D2): build + send one status report, one-way, swallowing errors (an
    /// old server has no handler; a transient send failure must not touch the agent loops).</summary>
    internal async Task SendDaemonStatusReportOnceAsync() {
        try { await _server.DaemonStatusReportAsync(BuildStatusReport()); }
        catch (Exception ex) { _logger.LogDebug(ex, "DaemonStatusReport send failed — ignoring"); }
    }

    async Task RunDaemonStatusReportLoopAsync(CancellationToken ct) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct)) {
            try { await SendDaemonStatusReportOnceAsync(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "DaemonStatusReport loop tick faulted — continuing"); }
        }
    }

    /// <summary>Phase B (D2): one <see cref="LiveAgentInfo"/> per currently-live (Starting or
    /// Running), non-private agent, carrying its kind + flow identity. Mirrors the
    /// <see cref="ServerConnection.GetLiveAgentIds"/> filter (private-local agents excluded).</summary>
    internal IReadOnlyList<LiveAgentInfo> BuildLiveAgents() =>
        [.. _agents.Values
            .Where(a => a.Status is "Starting" or "Running" && !a.IsPrivate)
            .Select(a => new LiveAgentInfo(a.Id, a.Kind.ToString(), a.CreatedAt, a.FlowRunId, a.FlowRole))];

    /// <summary>Phase B: test-only seam — insert a minimal <see cref="AgentInstance"/> (Noop
    /// PTY runtime, no real process/worktree) so unit tests can exercise <see cref="BuildLiveAgents"/>
    /// / status-report / reviewer-TTL logic without a live launch. Never called in production.</summary>
    internal AgentInstance SeedAgentForTest(
            string id, LaunchKind kind = LaunchKind.Default, string status = "Running",
            string? flowRunId = null, string? flowRole = null,
            DateTime? createdAt = null, DateTime? lastOutputAt = null, bool isPrivate = false,
            IPtyProcess? pty = null, string? startIdentity = null) {
        var agent = new AgentInstance(
            id, null, "default", null, "/repo", "codex",
            new PtyHostedAgentRuntime("codex", pty ?? NoopPtyProcess.Instance),
            new WorktreeInfo("/repo", "b", "/repo"),
            new CancellationTokenSource()) {
            Kind = kind, FlowRunId = flowRunId, FlowRole = flowRole, IsPrivate = isPrivate,
            CreatedAt = createdAt ?? DateTime.UtcNow, StartIdentity = startIdentity
        };
        agent.Status = status;
        if (lastOutputAt is { } lo) agent.LastOutputAt = lo;
        _agents[id] = agent;
        return agent;
    }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2/§5.5): route a launch. A fully-Seq'd command
    /// (Epoch + Seq + CommandId ALL present) goes through the processor's serial lane — accepted exactly-in-order,
    /// executed once, and turned into a terminal CommandAck/CommandRejected from the <see cref="CommandOutcome"/>
    /// the core returns. An un-Seq'd command (NONE of the three — old server) runs the legacy unsequenced lane
    /// directly and never advances the watermark. Anything in between (a PARTIAL tuple, or the processor somehow
    /// missing) is a malformed sequenced command and FAILS CLOSED with a LaunchFailed — never the legacy lane,
    /// whose retry could be re-accepted on the sequenced lane and double-create the generation.</summary>
    async Task HandleLaunchAgent(LaunchAgentCommand cmd) {
        var anySeq = cmd.Epoch is not null || cmd.Seq is not null || cmd.CommandId is not null;
        if (!anySeq) { await HandleLaunchAgentCore(cmd); return; } // old server — legacy unsequenced lane

        // Phase B2-b (sequenced-settlement design §5.5): a capable server sends ALL of
        // Epoch/Seq/CommandId. Anything less (a partial tuple, or the processor somehow missing) is a
        // malformed sequenced command and must FAIL CLOSED — never the unwatermarked legacy lane, whose
        // retry could be re-accepted on the sequenced lane and double-create the generation
        // (at-most-once-per-generation).
        if (_processor is { } proc && cmd.Epoch is { } epoch && cmd.Seq is { } seq && cmd.CommandId is { } cmdId) {
            await proc.SubmitAsync(
                new SequencedItem(SequencedKind.Launch, epoch, seq, cmdId, cmd.AgentId),
                () => HandleLaunchAgentCore(cmd));
            return;
        }

        await _server.LaunchFailedAsync(cmd.AgentId, "Malformed sequenced launch: partial Epoch/Seq/CommandId");
    }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2): the shipped launch body, now returning the
    /// terminal <see cref="CommandOutcome"/> the sequenced lane needs (the legacy caller ignores it). Every
    /// shipped pre-flight rejection maps to <c>LaunchRejected</c> — capacity to <c>daemon_capacity</c>, all
    /// other validations to <c>semantic</c> — so the sequenced lane emits a CommandRejected alongside the
    /// unchanged LaunchFailed; a spawn/registration failure that was cleaned up maps to
    /// <c>launch_failed_cleaned</c>; a registered agent maps to <c>launch_executed</c>. The shipped
    /// LaunchFailed / worktree-teardown / cleanup side effects are UNCHANGED — only the return value is added.</summary>
    async Task<CommandOutcome> HandleLaunchAgentCore(LaunchAgentCommand cmd) {
        var agentId       = cmd.AgentId;
        var prompt        = cmd.Prompt;
        var model         = cmd.Model;
        // A protocol-v3 explicit-reviewer-model launch pins the server-resolved LaunchModel VERBATIM.
        // Compute the effective model ONCE here so every site that records/reports the model the
        // process actually runs — the launch log, the runtime start context, and the registered
        // AgentInstance the server sees via RegisterAgentAsync / AgentRunStarted / every reconnect
        // re-registration — reads the same value. Null block ⇒ legacy path, cmd.Model unchanged.
        var effectiveModel = cmd.ExplicitReviewerModel?.LaunchModel ?? model;
        var effort        = cmd.Effort;
        var repoPath      = cmd.RepoPath;
        var tools         = cmd.Tools;
        var attachmentIds = cmd.AttachmentIds;
        var isReview      = cmd.Kind == LaunchKind.Review;
        var isReviewFlow  = cmd.Kind == LaunchKind.ReviewFlow;

        // Guard for a null/blank vendor before the dictionary lookup: LaunchAgentCommand crosses
        // the SignalR boundary where the non-null annotation isn't enforced, and Dictionary
        // .TryGetValue(null) throws ArgumentNullException — which SafeInvoke would swallow, dropping
        // the launch with no LaunchFailed reaching the server. (The removed vendor allowlist used to
        // absorb this incidentally.)
        if (string.IsNullOrWhiteSpace(cmd.Vendor) || !_runtimeFactories.TryGetValue(cmd.Vendor, out var runtimeFactory)) {
            await _server.LaunchFailedAsync(cmd.AgentId, $"Unknown vendor: {cmd.Vendor}");

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // fail an unattended (review-flow) launch fast when the selected vendor's
        // runtime can't run unattended — before creating a worktree, so there's nothing to
        // clean up. This guards every newly registered runtime through the same capability seam.
        if (UnattendedLaunchPolicy.RejectionReason(cmd.Vendor, runtimeFactory.SupportsUnattended, isReviewFlow) is { } unattendedRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, unattendedRejection);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        if (isReviewFlow && cmd.Borrowed && !runtimeFactory.SupportsBorrowedReviewFlow) {
            await _server.LaunchFailedAsync(cmd.AgentId,
                $"Borrowed review flows are not certified for '{cmd.Vendor}'; retry with an owned review worktree.");

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // A caller-selected Codex posture is validated here — before the repo checks and the worktree
        // creation below, so a rejection leaves nothing to clean up. Selection is only ever valid for
        // an interactive daemon-owned-worktree launch: a posture on a borrowed, review-flow or
        // PR-review launch would defeat a containment invariant, so it fails the launch outright
        // rather than being silently dropped or silently honoured.
        if (CodexPosturePolicy.RejectionReason(cmd) is { } postureRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, postureRejection);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        if (isReviewFlow && cmd.ReviewerCertification is { } certification) {
            var version = string.Equals(cmd.Vendor, "claude", StringComparison.Ordinal)
                ? DaemonRunner.ProbeCliVersionForLaunch(_config.ClaudePath)
                : null;
            var certificationCheck = EvaluateReviewerCertification(
                cmd.Vendor, version, _server.CurrentConnectionId, certification);
            if (!certificationCheck.Ok) {
                // Codex review round 2 (P1): tell the caller FIRST. Recomputing capabilities re-runs
                // the REGISTRATION probe — three 10s attempts plus backoff — so doing it before the
                // notification meant a failed launch probe cost ~10s here and then ~30.75s more
                // before the caller heard anything. The single-attempt launch probe did not bound the
                // launch path at all while this sat in front of the rejection; my claim that it did
                // was wrong.
                await _server.LaunchFailedAsync(cmd.AgentId,
                    $"reviewer_certification_changed: {certificationCheck.Reason}.");

                // The self-heal (recompute + re-advertise) is genuinely useful — a certification
                // mismatch usually means the advertisement is stale — but nothing waits on it, and
                // the caller already has its answer.
                //
                // SINGLE-FLIGHT, not bare fire-and-forget. Codex review round 3: concurrent rejected
                // launches each starting an independent refresh reintroduces this PR's own bug — a
                // slow failing refresh can complete AFTER a fast successful one and overwrite valid
                // capabilities with a failed-probe null, durably disabling the reviewer again.
                // Serialising publication is necessary; coalescing keeps a burst of rejections from
                // queueing a refresh each, and the rerun pass guarantees the LAST write is the
                // NEWEST computation.
                _capabilityRefresh.Trigger(
                    async () => {
                        _config.UnattendedVendorCapabilities =
                            DaemonRunner.ComputeUnattendedVendorCapabilities(_runtimeFactories.Values, _config);
                        await _server.ReRegisterAsync();
                    },
                    ex => _logger.LogDebug(ex,
                        "Capability recompute after a certification rejection failed; the next " +
                        "registration or launch re-evaluates it."));
                return new CommandOutcome(
                    CommandOutcomeKind.LaunchRejected,
                    agentId,
                    RejectReason: CommandRejectedReason.Semantic);
            }
        }

        WorktreeInfo? worktree      = null;
        string?       mcpConfigPath = null;

        // Declared OUTSIDE the try so it is in scope in the catch blocks below: the failed-launch
        // cleanup must consult it to decide whether the worktree is ours to remove. A borrowed cwd
        // is the user's real checkout — never removed on any path (spec's top safety invariant).
        var snapshotBorrow = cmd.Borrowed && runtimeFactory.BorrowedReviewRequiresIndependentSnapshot;
        var work = cmd.Borrowed && !snapshotBorrow
            ? WorkLocation.BorrowedCwd
            : WorkLocation.OwnedWorktree;
        string? borrowedSnapshotSource = null;

        // The per-reviewer bridge token URL (if this is an unattended review-flow launch), hoisted to
        // method scope so the failure catch can revoke it when no AgentInstance was created to carry it.
        string? reviewerToken = null;

        try {
            if (EffectiveCount >= _config.MaxConcurrentAgents) {
                await _server.LaunchFailedAsync(agentId, $"At max capacity ({_config.MaxConcurrentAgents} agents)");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.DaemonCapacity);
            }

            if (!_config.IsRepoAllowed(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path not allowed: {repoPath}");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
            }

            if (!Directory.Exists(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path does not exist: {repoPath}");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
            }

            if (isReview) {
                if (cmd.Review is not { } review) {
                    await _server.LaunchFailedAsync(agentId, "Review launch missing PR info");

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }

                // Final guard: re-validate that the chosen path's origin really
                // matches the PR's repo. The match the UI saw could have moved
                // (remote renamed, repo moved) between picker and launch.
                var actual = await GetOriginRemoteAsync(repoPath);

                if (actual is null) {
                    await _server.LaunchFailedAsync(agentId, $"No origin remote at {repoPath}");

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }

                var expected = $"github.com/{review.Owner}/{review.Repo}";

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
                    await _server.LaunchFailedAsync(agentId, $"Repo at {repoPath} no longer matches {review.Owner}/{review.Repo} (origin: {actual})");

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }
            }

            // "auto" means let the CLI decide — don't pass --effort at all
            if (string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase)) {
                effort = null;
            }

            // Validate effort level before expensive worktree setup
            if (!string.IsNullOrEmpty(effort) && !ValidEffortLevels.Contains(effort)) {
                await _server.LaunchFailedAsync(agentId, $"Invalid effort level: {effort}");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
            }

            LogLaunching(agentId, repoPath, effort ?? "default", effectiveModel);

            // Review launches base the worktree on the PR head ref so the agent
            // works against the PR's actual state, not the local HEAD.
            var baseRef = isReview && cmd.Review is { } reviewInfo
                ? $"refs/pull/{reviewInfo.PrNumber}/head"
                : cmd.BaseRef;

            if (cmd.Borrowed) {
                // Defense-in-depth re-authorization: the server already probed this cwd, but the
                // daemon NEVER borrows a path just because the server said so (TOCTOU-safe). The
                // reason is surfaced verbatim to the server via the catch → LaunchFailedAsync below;
                // Phase B (server) keys off the `borrow_auth_failed:` prefix.
                var auth = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(cmd.BorrowCwd ?? "");

                if (!auth.Allowed) {
                    throw new InvalidOperationException($"borrow_auth_failed: {auth.Reason}");
                }

                if (snapshotBorrow) {
                    borrowedSnapshotSource = auth.CanonicalCwd
                        ?? throw new InvalidOperationException("borrow_auth_failed: canonical_cwd_missing");
                    var snapshotGitRoot = auth.CanonicalGitRoot
                        ?? throw new InvalidOperationException("borrow_auth_failed: not_a_git_repository");
                    worktree = await _worktreeManager.CreateBorrowedSnapshotAsync(
                        snapshotGitRoot, borrowedSnapshotSource, $"borrowed-{agentId}", _shutdownCts.Token);
                } else {
                    // Direct borrowed runtimes have their own certified read-only boundary.
                    worktree = WorktreeInfo.Borrowed(auth.CanonicalCwd!);
                }
            } else {
                worktree = await _worktreeManager.CreateAsync(repoPath, baseRef: baseRef);
            }

            if (work == WorkLocation.OwnedWorktree) {
                // Download attachments into worktree (best-effort)
                if (attachmentIds is { Length: > 0 }) {
                    try {
                        var paths = await DownloadAttachmentsAsync(worktree.Path, attachmentIds);

                        if (paths.Count > 0) {
                            var suffix = $"\n\n[Attached files: {string.Join(", ", paths)}]";
                            prompt = string.IsNullOrEmpty(prompt) ? suffix.TrimStart() : prompt + suffix;
                        }
                    } catch (Exception ex) {
                        LogAttachmentDownloadFailed(ex, agentId);
                    }
                }
            }

            // An unattended review-flow reviewer must auto-approve its kcap tool calls (no human is
            // present): mint a dedicated bridge token bound to the launch's read-only kcap allowlist
            // and hand the reviewer that token's URL as KCAP_DAEMON_URL. An invalid allowlist fails
            // the launch FAST rather than falling back to a prompt that would hang.
            var daemonBridgeUrl    = _permissionBridge.BaseUrl;
            var effectiveAllowlist = cmd.McpAllowlist;

            // Only Codex reviewers get a token: their MCP config-lock is what makes a bare tool name
            // provably a bound kcap tool (see LocalPermissionBridge.IsReviewerToolAllowed). Claude
            // reviewers run via bypassPermissions with only the flow-result channel, so they need
            // none. Also guarded on the bridge listening (BaseUrl != null) — a graceful no-op otherwise.
            if (isReviewFlow && daemonBridgeUrl is not null
                && string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase)) {
                if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(cmd.McpAllowlist, out var reviewerServers, out var rejected)) {
                    await _server.LaunchFailedAsync(agentId,
                        $"Review-flow reviewer MCP allowlist contains a server that is not auto-approvable: '{rejected}'.");

                    if (work == WorkLocation.OwnedWorktree) {
                        try { await WorktreeManager.RemoveAsync(worktree); } catch { /* best-effort */ }
                    }

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }

                daemonBridgeUrl    = _permissionBridge.RegisterReviewerToken(reviewerServers);
                reviewerToken      = daemonBridgeUrl;   // the URL doubles as the revoke handle
                effectiveAllowlist = reviewerServers;   // single source: the set the launcher materializes
            }

            var runtimeCtx = new RuntimeStartContext(
                AgentId: agentId,
                Vendor: cmd.Vendor,
                SourceRepoPath: repoPath,
                Worktree: worktree,
                Prompt: prompt,
                // Task 8: for an explicit-model reviewer launch, launch with the server-pinned
                // LaunchModel VERBATIM (the launcher passes ctx.Model straight to the argument list —
                // never recanonicalized). effectiveModel (computed once above) resolves this — the
                // registered AgentInstance below reads the SAME local so they can never diverge.
                Model: effectiveModel,
                Effort: effort,
                Tools: tools,
                IsReview: isReview,
                IsReviewFlow: isReviewFlow,
                Review: cmd.Review,
                Cols: HostedPtyCols,
                Rows: HostedPtyRows,
                ServerUrl: _config.ServerUrl,
                DaemonBridgeUrl: daemonBridgeUrl,
                CapacitorPath: _config.CapacitorPath,
                McpAllowlist: effectiveAllowlist,
                Work: work,
                DaemonId: _daemonId,       // Phase B (D4 §6.4(3)): child env markers for the OrphanReaper scan
                DaemonEpoch: _daemonEpoch,
                IsBorrowedSnapshot: snapshotBorrow,
                CodexPosture: cmd.CodexPosture
            );

            HostedRuntimeStart start;

            // Captured BEFORE the spawn so the transcript-based session-id fallback
            // (DetectSessionIdAsync) can filter the shared project/rollout dir to files
            // written by THIS agent's process, not the user's earlier sessions.
            var spawnedAtUtc = DateTime.UtcNow;

            try {
                start = await runtimeFactory.StartAsync(runtimeCtx, _shutdownCts.Token);
            } catch (Exception ex) when (ex is CodexHooksNotInstalledException or CodexReviewerMcpIsolationException) {
                // CodexHooksNotInstalledException: hooks preflight failed in Prepare.
                // CodexReviewerMcpIsolationException: the review-flow reviewer's inherited MCP
                // servers could not be authoritatively enumerated, so the recursion guard cannot be
                // proven — fail the launch CLOSED rather than spawn a reviewer that might inherit a
                // flow-starting server. Both map to the same cleanup path.
                await _server.LaunchFailedAsync(agentId, ex.Message);

                // No AgentInstance was created, so CleanupAgentAsync won't run — revoke the reviewer
                // token here (if we minted one) so it doesn't leak into the live-token set.
                if (reviewerToken != null) _permissionBridge.RevokeReviewerToken(reviewerToken);

                // Still need to clean up the worktree before returning — but ONLY if we own it.
                // A borrowed cwd is the user's real checkout; removing it here would `git worktree
                // remove` the user's tree (spec's top safety invariant; mirrors CleanupAgentAsync).
                if (work == WorkLocation.OwnedWorktree) {
                    try { await WorktreeManager.RemoveAsync(worktree); } catch {
                        /* best-effort */
                    }
                }

                return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
            }

            mcpConfigPath = start.McpConfigPath;
            var runtime = start.Runtime;

            LogAgentSpawned(agentId, runtime.Pid, worktree.Path, runtimeFactory.Vendor);

            var cts = new CancellationTokenSource();

            // Applied-posture echo, stamped only for an interactive Codex launch on a daemon-owned
            // worktree. A review-flow or PR-review launch reports nothing (its posture is the
            // containment invariant, not a choice), and neither does a borrowed one. Both borrow
            // conditions are tested: `work` alone is not enough, because a snapshot-backed borrow
            // maps to OwnedWorktree while still being a borrow the caller never chose a posture for.
            (string Sandbox, string Approval)? appliedPosture =
                string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase)
             && cmd.Kind == LaunchKind.Default
             && !cmd.Borrowed
             && work == WorkLocation.OwnedWorktree
                    ? CodexPosturePolicy.Resolve(work, isReviewFlow, cmd.CodexPosture)
                    : null;

            // The permission bridge can never prompt under `never`, and danger-full-access reaches
            // outside the worktree entirely. The dashboard warns the user before launch; this is the
            // operator-side record of what actually ran.
            if (appliedPosture is { } applied
             && (string.Equals(applied.Approval, "never", StringComparison.Ordinal)
              || string.Equals(applied.Sandbox, "danger-full-access", StringComparison.Ordinal))) {
                LogBridgeDefeatingPosture(agentId, applied.Sandbox, applied.Approval);
            }

            var agent = new AgentInstance(agentId, prompt, effectiveModel, effort, repoPath, cmd.Vendor, runtime, worktree, cts) {
                McpConfigPath       = mcpConfigPath,
                CurrentCols         = HostedPtyCols,
                CurrentRows         = HostedPtyRows,
                Work                = work,
                SandboxPolicy       = appliedPosture?.Sandbox,
                ApprovalPolicy      = appliedPosture?.Approval,
                ReviewerBridgeToken = reviewerToken,
                BorrowedSnapshotSource = borrowedSnapshotSource,
                Kind                = cmd.Kind,       // Phase B (D2): flow identity + kind for LiveAgents/status report
                FlowRunId           = cmd.FlowRunId,
                FlowRole            = cmd.FlowRole
            };
            _agents[agentId] = agent;

            // Phase B (D4 §6.4(2)): capture the start-identity + write the durable PID record
            // immediately after the process exists (before registration) so a daemon crash right after
            // this leaves a reapable record. FAIL-CLOSED: a write/identity failure throws → the catch
            // routes it through the single-flight cleanup (the agent is already in _agents).
            PersistPidRecordOrThrow(agent, runtime.Pid, runtime.StartIdentity);

            await RegisterAgentAsync(agent);

            // A runtime with no terminal output (ACP/cursor) has no output-chunk signal to flip
            // Starting→Running on — ReadAgentOutputAsync's read loop never yields a byte for such
            // a runtime, so without this the agent would sit in "Starting" until the heartbeat's
            // StartupTimeout auto-stops it as stuck (Fix B/E). Flip to Running immediately:
            // the runtime factory's StartAsync already completed the ACP initialize/session-new
            // handshake by the time we get here, so the session really is established. PTY
            // runtimes are unaffected — they keep the existing on-first-chunk flip in
            // ReadAgentOutputAsync unchanged.
            if (!runtime.EmitsTerminalOutput) {
                agent.Status            = "Running";
                agent.HasReceivedOutput = true;
                if (!agent.IsPrivate) _ = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);
            }

            // Bind + start live transcript forwarding for any runtime that exposes an ACP
            // transcript source (Cursor today; null for every PTY runtime — no branch taken for
            // claude/codex). Fire-and-forget from here, exactly like ReadAgentOutputAsync below: the
            // bind call is IsReady-gated and can block across a reconnect outage (ConnectionRetry),
            // and HandleLaunchAgent must never stall on it — a stalled launch would queue every OTHER
            // inbound hub command behind it on this daemon's single SignalR connection.
            // StartAcpForwardingAsync itself still enforces the load-bearing ordering (bind strictly
            // after RegisterAgentAsync above, strictly before any AcpSessionEvents) by awaiting the
            // bind before constructing the forwarder.
            if (start.Transcript is { } transcript) {
                // Create + store the per-agent CTS BEFORE firing the setup task, so it exists for
                // FinalizeAgentRunAsync to cancel even if the agent finalizes before the bind below
                // ever resolves (see AgentInstance.AcpCts).
                var acpCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                agent.AcpCts = acpCts;
                _ = StartAcpForwardingAsync(agent, transcript, cmd.Vendor, acpCts);
            }

            // Start reading output
            _ = ReadAgentOutputAsync(agent);

            // Fallback session-id discovery: the primary link (the spawned harness's
            // session-start hook POSTing agent_host_id to /hooks/session-start) silently
            // breaks when the hook can't authenticate (e.g. expired kcap token → 401) or
            // doesn't land in time (an unattended/borrowed reviewer), leaving the agent
            // without a session id for correlation/display. The daemon can discover the id
            // itself from the transcript/rollout the harness writes and report it over its
            // own authenticated connection. Vendor-dispatched — Claude reads its per-worktree
            // project transcript, Codex reads its ~/.codex/sessions rollout; vendors without a
            // daemon-side locator no-op (the hook stays their only source). Best-effort
            // background task, cancelled with the agent — the server converges incarnations on
            // daemon liveness, so a missing id never blocks a launch.
            _ = DetectSessionIdAsync(agent, cmd.Vendor, spawnedAtUtc);

            // Report the resolved model so the server can display / validate the real model the agent
            // is running. Best-effort: never let a report failure break the launch.
            if (cmd.ExplicitReviewerModel is { } explicitReviewerModel) {
                // Task 8: a protocol-v3 explicit-model reviewer launch reports the CONCRETE
                // resolved model via the dedicated ReportExplicitReviewerModelResolved channel, keyed by
                // the durable LaunchAttemptId, so the server's one-shot waiter can validate + re-price it.
                ReportExplicitReviewerModel(agentId, cmd.Vendor, explicitReviewerModel, runtimeFactory);
            } else if (string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase)) {
                // Legacy path (name/arity/behavior UNCHANGED): the dispatched `model` may be the "default"
                // no-override sentinel, in which case Codex resolves the model from ~/.codex/config.toml.
                // Codex-only — Claude/other agents never call the ReportAgentResolvedModel hub.
                ReportResolvedModel(agentId, cmd.Vendor, model);
            }

            // Phase B2-b (sequenced-settlement design §4.2.2): the launch executed — the agent is registered.
            // The sequenced lane turns this into a terminal CommandAck(Processed) with a LIVE CurrentState read
            // at ack time; a fire-and-forget read loop that already finalized+cleaned the agent (e.g. an
            // immediate-exit runtime) reads as launch_failed_cleaned instead. Legacy callers ignore the value.
            return _agents.TryGetValue(agentId, out var launched)
                ? new CommandOutcome(CommandOutcomeKind.LaunchExecuted, agentId, launched.SessionId)
                : new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
        } catch (Exception ex) {
            LogLaunchFailed(ex, agentId);

            // Phase B (D1): a post-insert failure (agent already in _agents — e.g. a throwing
            // RegisterAgentAsync) routes teardown through the single-flight CleanupAgentAsync so it
            // can't strand a live child; a pre-insert failure falls through to the transient cleanup below.
            if (_agents.ContainsKey(agentId)) {
                await CleanupAgentAsync(agentId);
                await _server.LaunchFailedAsync(agentId, ex.Message);
                return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
            }

            // If a reviewer token was minted before the failure and no AgentInstance was created to
            // own it, revoke it here so it can't linger in the bridge's live-token set.
            if (reviewerToken != null) _permissionBridge.RevokeReviewerToken(reviewerToken);

            // Only tear down a worktree we OWN. A borrowed cwd is the user's real checkout — never
            // remove it, its branch, or its Claude project symlink on a failed launch (spec's top
            // safety invariant; mirrors the normal-stop guard in CleanupAgentAsync). For a borrowed
            // launch there is nothing daemon-created to clean up anyway (no CreateAsync, no mirror,
            // no attachments), and StartAsync throwing means mcpConfigPath was never assigned.
            if (worktree != null && work == WorkLocation.OwnedWorktree) {
                if (_launchers.TryGetValue(cmd.Vendor, out var launcherForCleanup)) {
                    try {
                        // Build a transient AgentInstance with a no-op PTY just so launcher.Cleanup
                        // can run its symlink/mcp-config teardown without a live agent.
                        var transient = new AgentInstance(
                            agentId,
                            prompt,
                            model,
                            effort,
                            repoPath,
                            cmd.Vendor,
                            new PtyHostedAgentRuntime(cmd.Vendor, NoopPtyProcess.Instance),
                            worktree,
                            new CancellationTokenSource()
                        ) {
                            McpConfigPath = mcpConfigPath
                        };
                        launcherForCleanup.Cleanup(transient);
                    } catch (Exception cleanupEx) {
                        LogCleanupStepFailed(cleanupEx, "launcher.Cleanup (failed-launch)", agentId);
                    }
                }

                try { await WorktreeManager.RemoveAsync(worktree); } catch {
                    /* best-effort */
                }
            }

            await _server.LaunchFailedAsync(agentId, ex.Message);

            // Phase B2-b (sequenced-settlement design §4.2.2): a pre-insert failure — the worktree (if any)
            // was torn down and no agent was ever registered; terminal for the sequenced lane.
            return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
        }
    }

    /// <summary>
    /// Best-effort: report the model the agent will actually run to the server so the UI can
    /// show the real model instead of the dispatched value. Codex-only — the hub contract
    /// (ReportAgentResolvedModel) and the config resolution are Codex-specific, so the caller
    /// gates this on <c>vendor == "codex"</c>. The dispatched <paramref name="model"/> may be
    /// the "default" no-override sentinel (or empty), in which case Codex resolves the model from
    /// <c>~/.codex/config.toml</c> — we resolve the same value here. Never throws: a resolve/report
    /// failure must not break launch.
    /// </summary>
    void ReportResolvedModel(string agentId, string vendor, string model) {
        try {
            var isDefault = string.IsNullOrEmpty(model) || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase);

            var resolved = isDefault && string.Equals(vendor, "codex", StringComparison.OrdinalIgnoreCase)
                ? CodexResolvedModel(model)
                : model;

            if (string.IsNullOrEmpty(resolved)) return;

            _ = _server.ReportAgentResolvedModelAsync(agentId, resolved);
        } catch (Exception ex) {
            LogReportResolvedModelFailed(ex, agentId);
        }
    }

    /// <summary>
    /// Task 8: reports the CONCRETE resolved model for an explicit-model reviewer launch via the
    /// dedicated <c>ReportExplicitReviewerModelResolved</c> channel. The concrete model is the exact,
    /// server-pinned <c>LaunchModel</c> the daemon launched with (never a recanonicalized or date-suffixed
    /// value — for Codex the slug-level equivalence key is date-SENSITIVE, so a date-suffixed
    /// session-metadata model would drift it; for Claude the family-level key absorbs any date, so the
    /// launch model's key still matches the pinned family anchor). The reported equivalence key is DERIVED
    /// from that concrete model via the vendor's own resolver so the server's key-equality validation is
    /// meaningful. Best-effort: a resolver-absent / unresolvable model or a send failure never breaks the
    /// launch — the server's report waiter then times out and fails the attempt CLOSED.
    /// </summary>
    void ReportExplicitReviewerModel(
            string agentId, string vendor, ExplicitReviewerModelLaunch block, IHostedAgentRuntimeFactory runtimeFactory) {
        try {
            var report = BuildExplicitReviewerModelReport(agentId, vendor, block, runtimeFactory.ReviewerModelResolver);

            if (report is null) {
                LogExplicitReviewerModelUnreportable(agentId, vendor, block.LaunchModel);
                return;
            }

            _ = _server.ReportExplicitReviewerModelResolvedAsync(report);
        } catch (Exception ex) {
            LogReportResolvedModelFailed(ex, agentId);
        }
    }

    /// <summary>
    /// Reads the top-level <c>model = "…"</c> from <c>~/.codex/config.toml</c> (honouring
    /// <c>CODEX_HOME</c> via <see cref="CodexPaths"/>); falls back to <paramref name="fallback"/>
    /// when the file is missing/unreadable or has no top-level model key.
    /// </summary>
    static string CodexResolvedModel(string fallback) {
        var fromConfig = CodexConfigToml.ReadTopLevelModel();

        return string.IsNullOrWhiteSpace(fromConfig) ? fallback : fromConfig;
    }

    static readonly TimeSpan GitGuardTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads <c>git remote get-url origin</c> at <paramref name="repoPath"/>
    /// and normalises it to <c>host/owner/repo</c> form (or null if missing
    /// or if git times out / blocks on a credential prompt). Used as a final
    /// guard before a hosted PR review is launched, so it must never hang the
    /// launch path.
    /// </summary>
    static async Task<string?> GetOriginRemoteAsync(string repoPath) {
        try {
            var psi = new ProcessStartInfo("git", ["remote", "get-url", "origin"]) {
                WorkingDirectory       = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                Environment = {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GCM_INTERACTIVE"]     = "Never"
                }
            };

            using var proc = Process.Start(psi);

            if (proc is null) return null;

            using var cts = new CancellationTokenSource(GitGuardTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch {
                    /* best-effort */
                }

                return null;
            }

            if (proc.ExitCode != 0) return null;

            var raw = (await proc.StandardOutput.ReadToEndAsync()).Trim();

            return string.IsNullOrWhiteSpace(raw) ? null : RemoteMatcher.NormalizeRemoteUrl(raw);
        } catch {
            return null;
        }
    }

    async Task ReadAgentOutputAsync(AgentInstance agent) {
        // The terminal-output enqueue back-pressures (awaits) when the send queue is
        // full. Tie that await to BOTH this agent's stop (ReadCts) and daemon shutdown
        // so HandleStopAgent releasing ReadCts unblocks the read loop — otherwise a
        // stop mid-outage would leave the finally-block finalization/cleanup stalled
        // until the whole daemon exits.
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(agent.ReadCts.Token, _shutdownCts.Token);

        // An UNATTENDED reviewer (bypassPermissions, no human present) can wedge forever on a
        // one-time consent/trust dialog — the original silent failure. Watch its PTY stream for the
        // known banners and fail the launch fast with an actionable reason instead of dying at the
        // server's session-id timeout. Only for unattended review-flow agents: an interactive agent's
        // human viewer can dismiss the prompt themselves, so we must not fail-fast for them.
        var dialogDetector = agent is { Kind: LaunchKind.ReviewFlow, Runtime.EmitsTerminalOutput: true }
            ? new ConsentDialogDetector()
            : null;

        try {
            await foreach (var data in agent.Runtime.ReadOutputAsync(agent.ReadCts.Token)) {
                agent.LastOutputAt      = DateTime.UtcNow;
                agent.HasReceivedOutput = true;

                if (agent.Status == "Starting") {
                    agent.Status = "Running";
                    if (!agent.IsPrivate) _ = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);
                }

                // Consent/trust dialogs are a PRE-SESSION concern: they render once at startup, before
                // any session exists. Once the session is live (SessionId resolved from the transcript
                // by DetectSessionIdAsync) the dialog phase is over — stop scanning so ordinary
                // reviewer/tool output that merely quotes a banner phrase (e.g. a reviewer reading the
                // detector's own source) can't latch a false wedge and kill a healthy reviewer.
                if (dialogDetector is not null) {
                    if (agent.SessionId is not null) {
                        dialogDetector = null; // session live — release the detector + its window
                    } else if (dialogDetector.Observe(data) is { } wedgeReason) {
                        await FailWedgedLaunchAsync(agent, data, wedgeReason);
                        return; // stop reading — the finally runs finalize + cleanup (kills the wedged PTY)
                    }
                }

                // Append to the replay buffer AND fan out to local sinks atomically under
                // SinksLock — paired with attach taking its snapshot + subscribing under the
                // same lock, so a chunk can't land in both a new client's replay and its live
                // stream (duplication), nor in neither (gap). TryEnqueue is non-blocking so the
                // lock is held only briefly; a slow client force-detaches inside TryEnqueue.
                lock (agent.SinksLock) {
                    agent.OutputBuffer.Append(data);
                    foreach (var sink in agent.LocalSinks) sink.TryEnqueue(data);
                }

                if (!agent.IsPrivate) {
                    var base64 = Convert.ToBase64String(data);

                    if (agent.IsLocalSpawned) {
                        // Local-first: a registered local agent has a live local terminal as its
                        // primary surface, so NEVER block the PTY read loop on a remote tunnel
                        // stall. Enqueue non-blocking; a full backlog (sustained outage) drops +
                        // counts the chunk and the web mirror re-syncs from the server's own buffer
                        // on reconnect. Keeps the local terminal responsive when the cloud hiccups.
                        _server.TrySendTerminalOutput(agent.Id, base64);
                    } else {
                        // Hosted: the server is the only consumer, so back-pressure here when the
                        // queue is full (slow/down transport) — a chunk is never dropped, since
                        // losing one byte garbles the whole redraw-TUI mirror. sendCts
                        // releases this await on agent stop or daemon shutdown.
                        await _server.SendTerminalOutputAsync(agent.Id, base64, sendCts.Token);
                    }
                }
            }
        } catch (OperationCanceledException) {
            /* expected on stop */
        } catch (Exception ex) {
            LogOutputReadError(ex, agent.Id);
        } finally {
            // Daemon shutdown: ServerConnection's _ct (= lifetime.ApplicationStopping)
            // is cancelled and the hub is being disposed, so every server call here
            // would throw TaskCanceledException. DisposeAsync owns the local cleanup
            // path for in-flight agents; the server detects the daemon disconnection
            // and ends its sessions on its own. Skip to avoid noisy warnings.
            if (!_shutdownCts.IsCancellationRequested) {
                await FinalizeAgentRunAsync(agent);
            }
        }
    }

    /// <summary>
    /// Fails a launch that the PTY dialog detector caught wedged on a consent/trust dialog: reports
    /// an actionable <c>LaunchFailed</c> (so the server surfaces it instead of timing out silently),
    /// persists the terminal tail for post-mortem, terminates the wedged (still-alive) process, and
    /// cancels the read loop so its finally runs the normal finalize + cleanup. Sets Status="Failed"
    /// FIRST so FinalizeAgentRunAsync skips its own startup-failure classification (no double report).
    /// </summary>
    async Task FailWedgedLaunchAsync(AgentInstance agent, byte[] triggeringChunk, string reason) {
        LogConsentDialogWedge(agent.Id, reason);

        // The detector inspects a chunk BEFORE the read loop appends it to OutputBuffer, so append the
        // triggering chunk now — otherwise a banner delivered in the first chunk (the common case) is
        // absent from the persisted tail, defeating the very capture this log exists to preserve.
        // TerminalOutputBuffer.Append is self-synchronized; the read loop has stopped feeding it.
        agent.OutputBuffer.Append(triggeringChunk);

        // Capture the banner before termination/cleanup discards the buffer.
        PersistFailedLaunchLog(agent, reason);

        agent.Status           = "Failed";
        agent.PendingEndReason = "consent_dialog_wedge";

        if (!agent.IsPrivate) {
            _ = _server.LaunchFailedAsync(agent.Id, reason);
            _ = _server.AgentStatusChangedAsync(agent.Id, "Failed", agent.SessionId);
            _ = _server.AppendAgentRunEventAsync(agent.Id, new AgentRunStopped("failed", null));
        }

        // The wedged process is still ALIVE on the dialog (unlike an ordinary startup failure, which
        // has already exited) — actively terminate it so it can't hold a daemon slot.
        try { await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) { LogStopError(ex, agent.Id); }

        // Cancel the read loop's token so the fallback session-id poll (DetectSessionIdAsync)
        // stops too; the loop itself exits via the `return` at the call site.
        try { await agent.ReadCts.CancelAsync(); } catch { /* best-effort */ }
    }

    /// <summary>Best-effort: persist the tail of an agent's PTY output to the retained failed-launch
    /// log. Never throws (FailedLaunchLog swallows I/O errors) — a diagnostic write must not disturb
    /// teardown.</summary>
    void PersistFailedLaunchLog(AgentInstance agent, string reason) {
        var path = _failedLaunchLog?.Persist(agent.Id, agent.OutputBuffer.Snapshot(), reason);
        if (path is not null) LogFailedLaunchCaptured(agent.Id, path);
    }

    async Task FinalizeAgentRunAsync(AgentInstance agent) {
        try {
            // PTY output can end before waitpid reports the child as exited.
            // Wait briefly for the process to finalize so we get a real exit code.
            await agent.Runtime.WaitForExitAsync(TimeSpan.FromSeconds(5));

            var exitCode = agent.Runtime.ExitCode;

            var status = agent.Runtime.HasExited
                ? exitCode is null or 0 ? "Completed" : "Failed"
                : "Failed";

            if (agent.Status is not "Completed" and not "Failed") {
                // A startup failure means the process exited before establishing
                // a real interactive session (CLI config error, auth issue, immediate
                // crash). A real session keeps producing output throughout its
                // lifetime, so the gap between CreatedAt and LastOutputAt is the
                // discriminator: tiny gap → startup failure; sustained → real session.
                //
                // We avoid agent.Status because the first output chunk flips it to
                // "Running" — a one-line error banner triggers that flip too. We
                // also avoid wall-clock since spawn: a user who types /exit shortly
                // after starting produces a short-but-real session that must not be
                // flagged as a launch failure. HasReceivedOutput guards
                // against a no-output process whose CreatedAt/LastOutputAt
                // initializers happened to straddle a long pause.
                //
                // This whole heuristic is output-stream-centric and only applies to a runtime
                // whose ReadOutputAsync yields real terminal bytes (PTY). A no-terminal runtime
                // (ACP/cursor) never has output to key off, so gate the check on
                // EmitsTerminalOutput — such a runtime is Completed/Failed purely by exit code
                // (Fix B/E), never misclassified as a startup failure just for having
                // produced no output.
                if (agent.Runtime.EmitsTerminalOutput && IsStartupFailure(agent.CreatedAt, agent.LastOutputAt, agent.HasReceivedOutput)) {
                    var output = ExtractTerminalText(agent.OutputBuffer);

                    var reason = !string.IsNullOrWhiteSpace(output)
                        ? output
                        : exitCode is null or 0
                            ? "Process exited before establishing a session"
                            : $"Process exited immediately (exit code {exitCode})";

                    status = "Failed";

                    LogStartupFailed(agent.Id, exitCode, reason);

                    // Persist the PTY tail before cleanup drops the in-memory buffer and removes the
                    // worktree, so a startup failure is diagnosable post-mortem (see FailedLaunchLog).
                    PersistFailedLaunchLog(agent, reason);

                    if (!agent.IsPrivate) _ = _server.LaunchFailedAsync(agent.Id, reason);
                }

                agent.Status = status;

                // PrivateLocal agents make no per-agent server calls (deny-all).
                if (!agent.IsPrivate) {
                    await _server.AgentStatusChangedAsync(agent.Id, status, agent.SessionId);

                    var stopReason = status == "Completed" ? "exited" : "failed";

                    await _server.AppendAgentRunEventAsync(agent.Id, new AgentRunStopped(stopReason, exitCode));
                }
            }

            LogAgentExited(agent.Id, exitCode);

            // For an ACP agent with a live forwarder, give the transcript a bounded chance to drain
            // BEFORE ending the session — this must NEVER pin shutdown (see
            // FinalDrainAcpTranscriptAsync's remarks); it always returns within AcpFinalDrainBudget
            // regardless of outcome. PTY agents have no AcpForwarder and take none of this path — the
            // runtime is disposed exactly where it always was, inside CleanupAgentAsync below.
            if (agent.AcpForwarder is { } acpForwarder) {
                await FinalDrainAcpTranscriptAsync(agent, acpForwarder);
            }

            // Cancel the per-agent ACP CTS unconditionally here — not only inside
            // FinalDrainAcpTranscriptAsync's own timeout branch above — so a bind/setup task that's
            // STILL in flight (the agent exited before its bind ever completed, so AcpForwarder is
            // still null and the drain step above never ran at all) observes cancellation now and can
            // abort at its liveness check (StartAcpForwardingAsync) before it ever registers a
            // binding for an agent that is finalizing right now. Runs BEFORE EndAgentSessionAsync so
            // any forwarder is fully stopped before the binding goes terminal server-side (the same
            // ordering the drain above already protects). Idempotent/harmless if already cancelled by
            // the drain step.
            if (agent.AcpCts is { } acpCts) {
                try { await acpCts.CancelAsync(); } catch { /* best-effort */ }
            }

            // Tell the server to end the AgentSession. Claude doesn't reliably fire
            // its own session-end hook on SIGTERM/exit, so without this call the
            // session would stay "active" forever in the read model. Server-side is
            // idempotent — if claude did fire session-end first, this is a no-op.
            // Reason is read from agent.PendingEndReason so a user-initiated stop is
            // recorded as "agent_stopped" rather than "agent_exited".
            //
            // EndAgentSessionAsync retries across SignalR reconnects, so it can block
            // for the length of an outage. We must NOT let that stall local cleanup
            // (worktree/process disposal, removing the agent from _agents), so bound how
            // long we WAIT on it to EndAgentSessionBudget. The retry keeps running in the
            // background — a reconnect shortly after still lands the session-end — and a
            // genuinely long outage falls back to server-side daemon-disconnect reconcile.
            //
            // PrivateLocal agents have no server-side session to end (deny-all).
            if (!agent.IsPrivate) {
                var endTask = _server.EndAgentSessionAsync(agent.Id, agent.PendingEndReason);

                try {
                    var result = await endTask.WaitAsync(EndAgentSessionBudget, _shutdownCts.Token);

                    // The daemon doesn't track sessionId on its own (only agentId), so
                    // the server returns it in the result. Spawn what's-done locally
                    // when the server says yes.
                    if (result is { GenerateWhatsDone: true, SessionId: not null }) {
                        SpawnWhatsDoneGenerator(result.SessionId);
                    }
                } catch (TimeoutException) {
                    // Outage outlasted the budget. Don't block cleanup; the retry continues
                    // in the background (observed below so a later fault isn't unobserved).
                    LogEndSessionTimedOut(agent.Id, EndAgentSessionBudget.TotalSeconds);
                    ObserveEndSessionInBackground(endTask, agent.Id);
                } catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) {
                    // Shutdown fired mid-wait — connection is being torn down. Server
                    // detects daemon disconnection independently. No warning needed.
                } catch (Exception ex) {
                    LogEndSessionFailed(ex, agent.Id);
                }
            }

            // Drop the reconnect re-bind registration now that EndAgentSessionAsync above has
            // (best-effort) made this binding terminal server-side — a later reconnect must not try
            // to re-bind a session that's already ended. Unconditional — NOT gated on AcpForwarder
            // having ever been set — so a binding a late/racing setup managed to register despite the
            // cancellation above (StartAcpForwardingAsync's liveness check narrows but can't fully
            // eliminate that window) still gets cleaned up here; UnregisterAcpBinding is a no-op when
            // nothing was ever registered, so this call is always safe to make unconditionally.
            _server.UnregisterAcpBinding(agent.Id);

            // Clean up worktree and unregister from server. Runs unconditionally — even
            // when end-session timed out and is still retrying in the background — so a
            // prolonged outage can never pin the agent in _agents or leak its worktree.
            await CleanupAgentAsync(agent.Id);
        } catch (Exception ex) {
            LogCleanupError(ex, agent.Id);
        }
    }

    /// <summary>
    /// Initial wait after sending /exit to give claude a chance to flush its session-end
    /// hook (which writes SessionEnded plus the what's-done summary). 15s covers a typical
    /// session-end POST + watcher drain on a healthy connection.
    /// </summary>
    static readonly TimeSpan GracefulExitWait = TimeSpan.FromSeconds(15);

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2): the sequenced stop. Routes through the
    /// processor's serial lane (accepted exactly-in-order, executed once, terminal StopExecuted outcome →
    /// CommandAck). Falls back to the legacy <see cref="HandleStopAgent"/> path if the processor is absent
    /// (never happens in production — always constructed in the ctor).</summary>
    async Task HandleStopAgentV2(StopAgentV2 cmd) {
        if (_processor is { } proc) {
            await proc.SubmitAsync(
                new SequencedItem(SequencedKind.Stop, cmd.Epoch, cmd.Seq, cmd.CommandId, cmd.AgentId),
                async () => {
                    await HandleStopAgent(cmd.AgentId);
                    return new CommandOutcome(CommandOutcomeKind.StopExecuted, cmd.AgentId);
                });
            return;
        }

        await HandleStopAgent(cmd.AgentId); // legacy unsequenced fallback
    }

    internal async Task HandleStopAgent(string agentId) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            // Phase B (D4 §6.4(3)): no in-memory agent — this may be a survivor of a PRIOR
            // daemon incarnation the server is still trying to stop (S2). Fall back to the PID record:
            // reap by exact identity if a matching live process is still there.
            await TryStopByPidRecordAsync(agentId);
            return;
        }

        // Defence-in-depth: a --private agent is invisible to the server (unregistered, not in
        // LiveAgentIds), so never act on a server-origin command for one even if its id leaks.
        // The local-socket path (HandleLocalStopAsync) deliberately bypasses this — that request
        // comes from the owner of the 0600 socket, not from the server.
        if (agent.IsPrivate) return;

        await StopAgentCoreAsync(agent);
    }

    /// <summary>
    /// The stop itself, with no caller-authorisation policy: graceful /exit, then terminate.
    /// Server-origin stops reach this through <see cref="HandleStopAgent"/> (which refuses
    /// private agents); local-socket stops call it directly. Returns true once
    /// <c>agent.Runtime.HasExited</c> confirms the process is actually gone after
    /// <see cref="IHostedAgentRuntime.TerminateAsync"/>; false if that confirmation never lands
    /// or any step above throws.
    /// </summary>
    async Task<bool> StopAgentCoreAsync(AgentInstance agent) {
        var agentId = agent.Id;

        try {
            LogStopping(agentId);

            // Set status BEFORE cancelling ReadCts so the read loop's finally
            // block sees "Completed" and skips its own status change / event append.
            agent.Status = "Completed";
            // Mark this as a user-initiated stop so the read-loop's finally-block
            // EndAgentSessionAsync call uses "agent_stopped" if it ends up being
            // the only successful call (e.g., transient SignalR failure here).
            // Phase B (D3): but PRESERVE a backstop reason the heartbeat already stamped
            // (reviewer_ttl_expired / reviewer_idle_expired) — only overwrite the "agent_exited"
            // default, so server-side attribution can tell a TTL/idle reap from a user stop.
            if (agent.PendingEndReason == "agent_exited") agent.PendingEndReason = "agent_stopped";

            // An unregistered agent has no server-side row to update.
            if (!agent.IsPrivate) {
                _ = _server.AgentStatusChangedAsync(agentId, "Completed", agent.SessionId);
                _ = _server.AppendAgentRunEventAsync(agentId, new AgentRunStopped("user", null));
            }

            // Try a graceful shutdown first: send /exit so claude can fire its own
            // session-end hook (drains transcript, writes SessionEnded + summary,
            // optionally schedules what's-done). Falls through to SIGTERM/SIGKILL
            // below if claude doesn't exit in time.
            //
            // Claude CLI requires the slash-command text and the Enter key to arrive
            // as separate PTY writes (with a small delay between them) — sending them
            // in a single write makes Claude treat the carriage return as part of the
            // command buffer instead of a submit. HandleSendInput uses the same split
            // pattern; matching it here makes the graceful path actually fire.
            try {
                await agent.Runtime.RequestGracefulStopAsync();
                await agent.Runtime.WaitForExitAsync(GracefulExitWait);
            } catch (Exception ex) {
                LogGracefulExitFailed(ex, agentId);
            }

            // PTY WaitForExitAsync(timeout) returns silently when the timeout elapses,
            // so a graceful-exit *timeout* doesn't throw. Check HasExited explicitly
            // so we can tell from logs whether the graceful path is actually working
            // in production or if this vendor's CLI is consistently being SIGTERMed instead.
            if (!agent.Runtime.HasExited) {
                LogGracefulExitTimedOut(agentId, GracefulExitWait.TotalSeconds, agent.Vendor);
            }

            // Cancel the read loop and terminate the process. We deliberately do NOT end
            // the AgentSession here: EndAgentSessionAsync now retries across SignalR
            // reconnects (so it can block while a dropped connection recovers), and a
            // user-initiated stop must not wait on that. Cancelling ReadCts unblocks the
            // read loop, whose finally block runs FinalizeAgentRunAsync once the process
            // exits — that ends the session (with retry) using agent.PendingEndReason
            // ("agent_stopped") and spawns the what's-done generator if the server asks.
            // So session-end is reliable as the post-exit backstop without delaying
            // teardown, and is idempotent: if claude already fired its own session-end
            // during the graceful window above, the backstop call is a server-side no-op.
            await agent.ReadCts.CancelAsync();
            await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(10));

            // TerminateAsync's SIGKILL is followed by a single non-blocking waitpid, so the
            // child is usually not reaped yet; poll briefly before calling the stop a failure.
            if (!agent.Runtime.HasExited) await agent.Runtime.WaitForExitAsync(TimeSpan.FromSeconds(2));

            return agent.Runtime.HasExited;
        } catch (Exception ex) {
            LogStopError(ex, agentId);

            return false;
        }
    }

    /// <summary>
    /// Observes a background EndAgentSession retry that outlived the finalize budget so a
    /// later fault isn't an unobserved task exception. Success and shutdown-cancellation
    /// are intentionally ignored; only a genuine fault is logged.
    /// </summary>
    void ObserveEndSessionInBackground(Task<EndAgentSessionResult> endTask, string agentId) =>
        _ = endTask.ContinueWith(
            t => LogEndSessionFailed(t.Exception!.GetBaseException(), agentId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    /// <summary>
    /// Spawns <c>kcap generate-whats-done {sessionId}</c> as a detached process.
    /// Used when the daemon-driven session-end path supplants claude's own session-end
    /// hook — claude normally spawns this generator from its CLI session-end handler,
    /// but when claude crashed or was killed before firing session-end the daemon has
    /// to do it instead. Best-effort: failure is logged but doesn't block other
    /// cleanup, and a missing kcap binary just means no what's-done summary.
    /// </summary>
    void SpawnWhatsDoneGenerator(string sessionId) {
        try {
            var psi = new ProcessStartInfo(_config.CapacitorPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KCAP_URL"] = _config.ServerUrl
                }
            };
            psi.ArgumentList.Add("generate-whats-done");
            psi.ArgumentList.Add(sessionId);

            using var proc = Process.Start(psi);

            if (proc is null) {
                LogWhatsDoneSpawnFailed(null, sessionId);

                return;
            }

            // Detach: close redirected streams so we don't hold pipes for the child's
            // lifetime. The child runs to completion on its own and posts its result
            // to the server.
            proc.StandardInput.Close();
            proc.StandardOutput.Close();
            proc.StandardError.Close();

            LogWhatsDoneSpawned(sessionId, proc.Id);
        } catch (Exception ex) {
            LogWhatsDoneSpawnFailed(ex, sessionId);
        }
    }

    async Task HandleSendInput(SendInputCommand cmd) {
        var (agentId, text, attachmentIds) = cmd;

        if (!_agents.TryGetValue(agentId, out var agent)) {
            LogSendInputUnknownAgent(agentId, _agents.Count);
            return;
        }

        if (agent.IsPrivate) {
            LogSendInputPrivateAgent(agentId);
            return; // server-origin input ignored for private agents
        }

        LogSendInputReceived(agentId, agent.Runtime.Vendor, text.Length, attachmentIds?.Length ?? 0);

        await agent.BorrowedSnapshotGate.WaitAsync(_shutdownCts.Token);
        try {
            if (!await TryRefreshBorrowedSnapshotAsync(agent)) return;

            var message = text;

            if (attachmentIds is { Length: > 0 }) {
                var paths = await DownloadAttachmentsAsync(agent.Worktree.Path, attachmentIds);

                if (paths.Count > 0) {
                    message = $"{text}\n\n[Attached files: {string.Join(", ", paths)}]";
                }
            }

            // PTY runtimes use bracketed paste; ACP runtimes send a structured prompt.
            if (agent.BorrowedSnapshotSource is not null)
                await agent.Runtime.SendUserInputAndWaitForWriteAsync(message);
            else
                await agent.Runtime.SendUserInputAsync(message);

            LogSendInputDelivered(agentId, agent.Runtime.Vendor, message.Length);
        } finally {
            agent.BorrowedSnapshotGate.Release();
        }
    }

    static readonly TimeSpan BorrowedSnapshotRefreshTimeout = TimeSpan.FromSeconds(30);

    async Task<bool> TryRefreshBorrowedSnapshotAsync(AgentInstance agent) {
        if (agent.BorrowedSnapshotSource is not { } source || agent.Work != WorkLocation.OwnedWorktree)
            return true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        timeout.CancelAfter(BorrowedSnapshotRefreshTimeout);
        try {
            await agent.Runtime.WaitForTurnIdleAsync(timeout.Token);
            var auth = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(source);
            if (!auth.Allowed ||
                !SameFileSystemPath(auth.CanonicalCwd, source) ||
                !SameFileSystemPath(auth.CanonicalGitRoot, agent.Worktree.SourceRepo))
                throw new InvalidOperationException($"borrow_auth_failed: {auth.Reason ?? "source_identity_changed"}");
            await _worktreeManager.SyncFromSourceAsync(
                agent.Worktree.SourceRepo, agent.Worktree.SnapshotRoot ?? agent.Worktree.Path,
                agent.Worktree.Path, [], timeout.Token);
            return true;
        } catch (Exception ex) when (ex is not OperationCanceledException || !_shutdownCts.IsCancellationRequested) {
            LogBorrowedSnapshotRefreshFailed(ex, agent.Id);
            // Fail closed: the disposable snapshot may be partial, so terminate this reviewer
            // and never retry or reuse it for another round.
            agent.PendingEndReason = "borrowed_snapshot_refresh_failed";
            try { await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(10)); } catch { /* cleanup owns final reap */ }
            return false;
        }
    }

    static bool SameFileSystemPath(string? left, string? right) =>
        left is not null && right is not null && string.Equals(
            Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    async Task HandleSendSpecialKey(string agentId, string key) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            LogSendSpecialKeyUnknownAgent(agentId, key, _agents.Count);
            return;
        }

        if (agent.IsPrivate) return; // server-origin key ignored for private agents

        await agent.Runtime.SendSpecialKeyAsync(key);
    }

    async Task<List<string>> DownloadAttachmentsAsync(string worktreePath, string[] attachmentIds) {
        var attachDir = Path.Combine(worktreePath, ".attached");
        Directory.CreateDirectory(attachDir);

        // Write .gitignore to prevent accidental commits
        var gitignorePath = Path.Combine(attachDir, ".gitignore");

        if (!File.Exists(gitignorePath)) {
            await File.WriteAllTextAsync(gitignorePath, "*\n");
        }

        var paths = new List<string>();

        foreach (var id in attachmentIds) {
            try {
                using var httpClient = _httpClientFactory.CreateClient("Attachments");

                var resolution = await TokenStore.GetValidTokensForServerAsync(_config.ServerUrl);

                if (resolution.Tokens is not null) {
                    httpClient.DefaultRequestHeaders.Authorization = new("Bearer", resolution.Tokens.AccessToken);
                }

                var response = await httpClient.GetAsync($"/api/attachments/{id}");

                if (!response.IsSuccessStatusCode) {
                    LogAttachmentNotFound(id, response.StatusCode);

                    continue;
                }

                var rawFileName = response.Content.Headers.ContentDisposition?.FileNameStar
                 ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                 ?? $"attachment-{id[..8]}";

                // Sanitize: strip path separators to prevent directory traversal
                var fileName = Path.GetFileName(rawFileName);

                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = $"attachment-{id[..8]}";

                var filePath = GetUniqueFilePath(attachDir, fileName);
                var fullPath = Path.GetFullPath(filePath);
                var safeDir  = Path.GetFullPath(attachDir) + Path.DirectorySeparatorChar;

                if (!fullPath.StartsWith(safeDir)) {
                    LogAttachmentPathEscape(rawFileName);

                    continue;
                }

                await using var fs = File.Create(filePath);
                await response.Content.CopyToAsync(fs);

                paths.Add($".attached/{Path.GetFileName(filePath)}");
            } catch (Exception ex) {
                LogAttachmentError(ex, id);
            }
        }

        return paths;
    }

    static string GetUniqueFilePath(string directory, string fileName) {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path)) {
            return path;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext            = Path.GetExtension(fileName);
        var counter        = 2;

        do {
            path = Path.Combine(directory, $"{nameWithoutExt}-{counter}{ext}");
            counter++;
        } while (File.Exists(path));

        return path;
    }

    Task<string[]> HandleFindRepoForRemote(FindRepoForRemoteRequest req)
        => _repoMatcher.FindAsync(req.Owner, req.Repo, req.CandidatePaths ?? [], _shutdownCts.Token);

    /// <summary>
    /// Handles the server's <c>ProbeBorrowSource</c> client-result invocation (Phase A, task
    /// A3): "can you borrow this path?". Delegates the actual policy (allowlist, git-root resolution,
    /// symlink canonicalization) to <see cref="BorrowAuthorizer"/> — constructed fresh over the
    /// daemon's current <see cref="DaemonConfig"/> so a config reload is picked up on the next probe —
    /// and maps its <see cref="BorrowAuthResult"/> onto the wire-facing <see cref="BorrowProbeResult"/>.
    /// </summary>
    async Task<BorrowProbeResult> HandleProbeBorrowSource(string path) {
        var result = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(path);

        return new BorrowProbeResult(result.Allowed, result.CanonicalCwd, result.CanonicalGitRoot, result.Reason);
    }

    /// <summary>
    /// Task 8: handles the server's <c>ResolveReviewerModel</c> client-result invocation — the
    /// side-effect-free reviewer-model preflight. PURE resolution: it consults the SELECTED vendor's
    /// advertised resolver first (via the shared cross-vendor coordinator, which inspects the OTHER
    /// advertised unattended resolvers only after the selected one rejects, to name a mismatch vendor)
    /// and NEVER spawns a subprocess, creates a worktree, or writes config. Echoes the exact
    /// <c>RequestId</c>/<c>Vendor</c>; returns the daemon's own RPC protocol version so a protocol drift
    /// fails the preflight closed; and maps the daemon-internal disposition set onto the server's
    /// accepted/unavailable/invalid wire dispositions (a vendor mismatch becomes an <c>unavailable</c>
    /// carrying <c>RecognizedVendor</c>).
    /// </summary>
    ReviewerModelResolveResponseV1 HandleResolveReviewerModel(ReviewerModelResolveRequestV1 req) {
        // The ADVERTISED unattended resolvers — the same predicate DaemonRunner.ComputeUnattendedVendor-
        // Capabilities gates the capability on (installed + unattended-certified + resolver present).
        // Vendor-neutral: we read each factory's own resolver, never a central vendor→model table.
        var advertised = _runtimeFactories.Values
            .Where(f => f.IsAvailable() && f.SupportsUnattended && f.ReviewerModelResolver is not null)
            .Select(f => f.ReviewerModelResolver!)
            .ToList();

        // The coordinator resolves on the SELECTED vendor's resolver first and inspects the OTHER
        // advertised resolvers only after the selected one rejects (to name a mismatch vendor).
        var resolution = ReviewerModelResolvers.Resolve(req.Vendor, req.RequestedModel, advertised);

        // Echo the exact RequestId + Vendor (the server rejects a mismatch), and return THIS daemon's own
        // RPC protocol version — never an echo of the request's expectation — so a protocol drift on
        // either side is detected and fails the preflight CLOSED.
        var response = new ReviewerModelResolveResponseV1(
            req.RequestId, req.Vendor, ReviewerModelResolvers.RpcProtocolVersion, Disposition: "unavailable");

        return resolution.Disposition switch {
            ReviewerModelDisposition.Accept => response with {
                Disposition             = "accepted",
                CanonicalRequestedModel = resolution.CanonicalRequestedModel,
                LaunchModel             = resolution.LaunchModel,
                EquivalenceKey          = resolution.EquivalenceKey,
            },
            // A cross-vendor recognition maps to unavailable with the ordinal-first OTHER vendor named;
            // the server re-validates RecognizedVendor against what this daemon currently advertises.
            ReviewerModelDisposition.VendorMismatch => response with {
                Disposition      = "unavailable",
                RecognizedVendor = resolution.DiagnosticCode,
            },
            ReviewerModelDisposition.Invalid => response with {
                Disposition    = "invalid",
                DiagnosticCode = resolution.DiagnosticCode,
            },
            // Unavailable, or any unexpected disposition — fail closed as plain unavailable.
            _ => response,
        };
    }

    /// <summary>
    /// Task 8: builds the post-launch <see cref="ExplicitReviewerModelResolvedV1"/> report for an
    /// explicit-model reviewer launch, or <see langword="null"/> when it cannot be built (no resolver for
    /// the vendor, or the launch model no longer resolves — both fail the report closed rather than send
    /// an unvalidatable one). The reported <c>EquivalenceKey</c> is DERIVED from the concrete launch model
    /// via the SAME resolver (so the server's key-equality validation is meaningful, not a trivial echo),
    /// and the <c>PolicyVersion</c> is the resolver's own per-vendor policy version. Pure/static so it is
    /// unit-testable in isolation from the launch gauntlet.
    /// </summary>
    static ExplicitReviewerModelResolvedV1? BuildExplicitReviewerModelReport(
            string agentId, string vendor, ExplicitReviewerModelLaunch block, IReviewerModelResolver? resolver) {
        // The explicit path is gated by the server on the vendor advertising a resolver — defensively,
        // no resolver means we can neither derive nor validate a key: fail the report CLOSED.
        if (resolver is null) return null;

        // The concrete model the daemon launched with is the server-pinned LaunchModel VERBATIM. For
        // Codex this is load-bearing: its slug-level equivalence key is date-SENSITIVE, so reporting a
        // date-suffixed session-metadata model would drift the key and fail the server's echo
        // validation (see the Codex_DatedSlug regression test). For Claude the family-level key absorbs
        // any date, so the launch model's key still matches the pinned family anchor.
        var resolution = resolver.Resolve(block.LaunchModel);

        // Derive the equivalence key from that concrete model via the SAME resolver — so the server's
        // key-equality check is meaningful, not a trivial echo of the pinned block. A launch model that
        // no longer anchored-accepts (a policy drift) can't produce a valid report: fail closed.
        if (resolution.Disposition != ReviewerModelDisposition.Accept || resolution.EquivalenceKey is null)
            return null;

        return new ExplicitReviewerModelResolvedV1(
            AgentId:         agentId,
            LaunchAttemptId: block.LaunchAttemptId,
            Vendor:          vendor,
            ResolvedModel:   block.LaunchModel,          // verbatim — never recanonicalized/date-suffixed
            PolicyVersion:   resolver.PolicyVersion,     // the per-vendor resolver policy version
            EquivalenceKey:  resolution.EquivalenceKey); // DERIVED via the resolver; equals the pinned key
    }

    /// <summary>
    /// Registers an agent with the server exactly as a UI-launched agent: AgentRegistered +
    /// terminal dims + AgentRunStarted, then persists/announces the repo path. No-ops for a
    /// PrivateLocal agent. Shared by the hosted launch and the registered local launch so the
    /// two cannot drift. Dims come from <see cref="AgentInstance.CurrentCols"/>/<c>CurrentRows</c>
    /// (hosted = HostedPtyCols/Rows; local = the client's terminal size).
    /// </summary>
    async Task RegisterAgentAsync(AgentInstance agent) {
        if (agent.IsPrivate) return;

        await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.SandboxPolicy, agent.ApprovalPolicy);

        // Report the PTY size so read-only viewers lock their xterm to it. Best-effort.
        try {
            await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
        } catch (Exception ex) {
            LogTerminalDimsSendFailed(ex, agent.Id);
        }

        _ = _server.AppendAgentRunEventAsync(
            agent.Id,
            new AgentRunStarted(agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.Worktree.Path, agent.Vendor)
        );

        // Persist repo path and notify server so the launch dialog updates.
        _ = Task.Run(async () => {
                try {
                    await RepoPathStore.AddAsync(agent.RepoPath);
                    await _server.UpdateRepoPathsAsync();
                } catch (Exception ex) {
                    LogRepoPathPersistFailed(ex, agent.Id);
                }
            }
        );
    }

    /// <summary>
    /// Binds the ACP canonical session to <paramref name="agent"/> (<c>AcpSessionStarted</c>) —
    /// this call MUST run after <see cref="RegisterAgentAsync"/> has already registered the agent (the server rejects a
    /// bind for an unregistered agent) and strictly before any transcript event reaches the server;
    /// callers (<see cref="HandleLaunchAgent"/>) enforce the first half by only calling this after
    /// <c>await RegisterAgentAsync(agent)</c>, and this method enforces the second half itself by
    /// awaiting the bind before ever constructing the forwarder. Once bound, registers the binding
    /// for reconnect re-bind (<see cref="ServerConnection.RegisterAcpBinding"/>), builds the
    /// synthesized <c>SessionStarted@Seq0</c> envelope (<see cref="AcpEventTranslator.BuildSessionStarted"/>),
    /// and starts <see cref="ForwardAcpTranscriptAsync"/> as background work — the resulting task is
    /// kept on <see cref="AgentInstance.AcpForwarder"/> so <see cref="FinalizeAgentRunAsync"/> can
    /// coordinate the bounded final-drain at teardown.
    ///
    /// Best-effort: any failure in the bind/setup step is logged and swallowed, never propagated to
    /// the caller. By the time this runs, <paramref name="agent"/> is already registered with the
    /// server and its ACP process is already live — letting a transcript-plumbing failure escape
    /// into <see cref="HandleLaunchAgent"/>'s outer catch would incorrectly route it through the
    /// failed-launch cleanup path (worktree removal) against an agent that is actually running.
    /// Degrades to "no live transcript for this session" rather than failing the launch.
    ///
    /// <b>Bind-vs-finalize stale-binding race:</b>
    /// <paramref name="acpCts"/> is <paramref name="agent"/>'s <see cref="AgentInstance.AcpCts"/>,
    /// created by the caller BEFORE this task was fired. Its token — not the raw daemon shutdown
    /// token — gates the bind call below AND (once built) the forwarder's run task, so
    /// <see cref="FinalizeAgentRunAsync"/> can cancel just this setup/forwarder. The bind call can
    /// block for the length of a reconnect outage (<c>ConnectionRetry</c>); if the agent's whole
    /// lifecycle finalizes while it's still in flight, a LATE successful bind must not register a
    /// binding for what is by then a dead agent (it would leak into <c>_acpBindings</c> and be
    /// replayed on every future reconnect with nothing left to ever drain it) — the liveness check
    /// immediately below the bind await closes that race.
    /// </summary>
    async Task StartAcpForwardingAsync(AgentInstance agent, IAcpTranscriptSource transcript, string vendor, CancellationTokenSource acpCts) {
        try {
            await _server.AcpSessionStartedAsync(
                agent.Id,
                vendor,
                transcript.AcpSessionId,
                transcript.Cwd,
                transcript.ResolvedModel,
                null, // metadata: no wire-contract fields required for the prototype
                acpCts.Token
            );

            // Liveness check: the await above can span a reconnect outage.
            // If finalize already ran (cancelling acpCts and/or removing the agent from _agents)
            // while we were waiting, abort here — do not register a binding, build a forwarder, or
            // start it for an agent that's finalizing or already gone.
            if (acpCts.IsCancellationRequested || !_agents.ContainsKey(agent.Id)) {
                LogAcpBindAbortedAgentGone(agent.Id);

                return;
            }

            _server.RegisterAcpBinding(
                agent.Id,
                new AcpBindInfo(vendor, transcript.AcpSessionId, transcript.Cwd, transcript.ResolvedModel)
            );

            // Post-register re-check (TOCTOU): finalize can run between the liveness check above and
            // this register, having already cancelled/unregistered+cleaned up the agent — leaving the
            // binding we just registered stale (replayed on reconnect for a dead agent). Undo it. The
            // finalizer's own unconditional UnregisterAcpBinding covers the mirror case (finalize after
            // this point); UnregisterAcpBinding is idempotent so a double-remove is harmless.
            if (acpCts.IsCancellationRequested || !_agents.ContainsKey(agent.Id)) {
                _server.UnregisterAcpBinding(agent.Id);
                LogAcpBindAbortedAgentGone(agent.Id);

                return;
            }

            var sessionStarted = AcpEventTranslator.BuildSessionStarted(
                seq: 0,
                DateTimeOffset.UtcNow.ToString("O"),
                cwd: transcript.Cwd,
                model: transcript.ResolvedModel,
                rawSessionId: transcript.AcpSessionId
            );

            var forwarder = new AcpTranscriptForwarder(
                send: (batch, ct) => _server.SendAcpEventsAsync(agent.Id, transcript.AcpSessionId, batch, ct),
                initialEnvelope: sessionStarted,
                envelopes: transcript.Envelopes,
                logger: _logger
            );

            var runTask = ForwardAcpTranscriptAsync(agent, forwarder, acpCts.Token);
            agent.AcpForwarder = new AcpForwarderHandle(forwarder, runTask);
        } catch (Exception ex) {
            LogAcpBindFailed(ex, agent.Id);
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="AcpTranscriptForwarder.RunAsync"/> — a forwarder fault must NEVER
    /// crash the agent or the daemon. <see cref="AcpTranscriptForwarder.RunAsync"/> already swallows
    /// its own cancellation and retries indefinitely on a send failure, but this wrapper is the outer
    /// safety net for anything else that could still escape it (e.g. the transcript channel itself
    /// faulting from a translator bug upstream) — logged, never rethrown, so the returned task always
    /// completes successfully and <see cref="FinalizeAgentRunAsync"/> can safely await it.
    /// </summary>
    async Task ForwardAcpTranscriptAsync(AgentInstance agent, AcpTranscriptForwarder forwarder, CancellationToken ct) {
        try {
            await forwarder.RunAsync(ct);
        } catch (Exception ex) {
            LogAcpForwarderFaulted(ex, agent.Id);
        }
    }

    /// <summary>
    /// Time budget for the ACP bounded final-drain — how long
    /// <see cref="FinalDrainAcpTranscriptAsync"/> waits for the forwarder's run task to finish
    /// draining (after the runtime is disposed) before giving up and letting
    /// <see cref="FinalizeAgentRunAsync"/> proceed to <see cref="ServerConnection.EndAgentSessionAsync"/>
    /// regardless. Deliberately small and independent of <see cref="EndAgentSessionBudget"/> — a
    /// slow/stuck drain degrades to "no trailing transcript", never a stacked delay on top of the
    /// session-end budget, and never pins shutdown (the primary invariant this exists to protect).
    /// Settable so tests don't wait for the real value.
    /// </summary>
    internal TimeSpan AcpFinalDrainBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Disposes the ACP runtime FIRST so its <c>DisposeAsync</c> completes the
    /// transcript channel (courtesy-flushing any still-open aggregation run) — <paramref name="acpForwarder"/>'s
    /// run task can only ever return once that channel completes — then gives the forwarder a FINITE
    /// budget (<see cref="AcpFinalDrainBudget"/>) to drain whatever's left to the server before
    /// returning UNCONDITIONALLY. Never throws and never blocks past the budget: a disposal fault or
    /// a drain that exceeds it is logged, not propagated, so <see cref="FinalizeAgentRunAsync"/>'s
    /// own outage-cleanup guarantee (<see cref="EndAgentSessionBudget"/>) is never compounded by this
    /// call. This is also what keeps the forwarder stopped BEFORE the binding goes terminal (the
    /// caller ends the session immediately after this returns) — the ordering the hot-loop
    /// guard's edge case relies on in the normal (non-outage) flow.
    ///
    /// When the drain misses its budget, the forwarder is
    /// presumably still blocked/retrying a send against an unresponsive connection —
    /// <see cref="AcpTranscriptForwarder.RunAsync"/> otherwise retries indefinitely. Cancel
    /// <paramref name="agent"/>'s per-agent <see cref="AgentInstance.AcpCts"/> so it unwinds
    /// promptly (it is <c>ct</c>-aware at every await point) instead of leaking an orphaned task
    /// that keeps sending against an agent that's finalizing right now.
    /// </summary>
    async Task FinalDrainAcpTranscriptAsync(AgentInstance agent, AcpForwarderHandle acpForwarder) {
        try {
            await agent.Runtime.DisposeAsync();
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "disposing ACP runtime for final transcript drain", agent.Id);
        }

        var completed = await Task.WhenAny(acpForwarder.RunTask, Task.Delay(AcpFinalDrainBudget));

        if (completed != acpForwarder.RunTask) {
            LogAcpFinalDrainTimedOut(agent.Id, AcpFinalDrainBudget.TotalSeconds);

            if (agent.AcpCts is { } acpCts) {
                try { await acpCts.CancelAsync(); } catch { /* best-effort — never let this pin teardown */ }
            }
        }
    }

    Task HandleResizeTerminal(ResizeTerminalCommand cmd) {
        // Ignore server-origin resize for private agents (defence-in-depth; see HandleStopAgent).
        if (_agents.TryGetValue(cmd.AgentId, out var agent) && !agent.IsPrivate) {
            // The server sends the min aggregate across all web viewers, or (0,0) when the
            // last web viewer left. Fold it into the same min-clamp as the local clients rather than
            // resizing the PTY directly — a small web viewer must not corrupt a large local terminal
            // (or vice-versa), and a departing web viewer must let the PTY grow back to the local size.
            //
            // (0,0) clears WebDims. Accept other dims only when they're positive AND fit the PTY's
            // ushort winsize — ignore anything else (negative, or > ushort.MaxValue) so a bad value
            // can't wrap on the cast and poison the shared clamp (e.g. a wrapped 0 would block all
            // resizing). The server already bounds-checks; this is defence-in-depth — the daemon must
            // not trust the wire.
            var clear = cmd is { Cols: 0, Rows: 0 };
            var valid = cmd is { Cols: > 0 and <= ushort.MaxValue, Rows: > 0 and <= ushort.MaxValue };

            if (clear || valid) {
                lock (agent.SinksLock) {
                    agent.WebDims = clear ? null : new AgentInstance.Dim((ushort)cmd.Cols, (ushort)cmd.Rows);
                    ClampPtyLocked(agent);
                }

                // Announce the clamped size so every web viewer re-locks (and reconnect resends the
                // real size, not stale ones). Outside the lock, best-effort, fire-and-forget — same
                // as the local resize path in ApplyResizeClamp.
                _ = SafeSendDimsAsync(agent);
            }
        }

        return Task.CompletedTask;
    }

    static readonly TimeSpan ReRegisterRetryDelay = TimeSpan.FromMilliseconds(250);
    const           int      ReRegisterMaxAttempts = 3;

    /// <summary>
    /// Re-registers this daemon's live agents with the server (AgentRegistered +
    /// AgentStatusChanged) so per-session ownership is restored after a (re-)connect. Wired into
    /// <see cref="ServerConnection.ReRegisterAgentsHook"/> and awaited inside
    /// <see cref="ServerConnection.RegisterDaemon"/> BEFORE readiness is restored — so a
    /// permission invoke gated on <c>IsReady</c> can't fire before ownership recovery.
    ///
    /// Each agent's re-registration is retried a bounded number of times before giving up, so a
    /// transient blip doesn't leave that agent's ownership unrestored while the daemon still
    /// flips ready (the qodo "ready despite reregister failures" gap). On final failure we log and
    /// move on rather than throw: one agent's persistent failure must NOT withhold readiness for
    /// the whole daemon (that would block every other agent's permissions and loop reconnects).
    /// The bounded ownership-retry in <see cref="ServerConnection.RequestPermissionAsync"/> is the
    /// final safety net for the residual case.
    /// </summary>
    async Task ReRegisterAgentsAsync() {
        // PrivateLocal agents are never registered with the server, so never re-register them.
        foreach (var agent in _agents.Values.Where(a => (a.Status is "Starting" or "Running") && !a.IsPrivate)) {
            for (var attempt = 1; ; attempt++) {
                try {
                    await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.SandboxPolicy, agent.ApprovalPolicy);
                    await _server.AgentStatusChangedAsync(agent.Id, agent.Status, agent.SessionId);

                    // Re-send the fixed PTY dims. The server stores them in memory, so a
                    // server restart (not just a daemon blip) wipes them — without this
                    // resend the read-only viewers never re-lock and the TUI garbles
                    // again exactly as before the fix. Best-effort: its own
                    // catch keeps a dims-send failure from escaping to the retry handler
                    // (which would re-register the agent) or withholding readiness.
                    try {
                        await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
                    } catch (Exception ex) {
                        LogTerminalDimsSendFailed(ex, agent.Id);
                    }

                    // do NOT replay the full output buffer here. The old
                    // replay re-sent the entire 2 MB ring on every reconnect, which
                    // the server appended to its own buffer and live-broadcast on
                    // top of the current screen — duplicated, and interleaved with
                    // the read loop's concurrent live sends, producing the garbled
                    // Terminal tab. The server retains its own per-agent buffer
                    // across a daemon rebind (it only clears on reconcile-to-Failed),
                    // so late-joining web clients still get history via the server's
                    // SubscribeToTerminal replay. Continuity of in-flight output is
                    // handled by TerminalOutputSender, which holds unsent chunks
                    // while the transport is down and flushes them, in order, once
                    // the connection is back.
                    break;
                } catch (Exception) when (attempt < ReRegisterMaxAttempts && !_shutdownCts.IsCancellationRequested) {
                    try {
                        await Task.Delay(ReRegisterRetryDelay, _shutdownCts.Token);
                    } catch (OperationCanceledException) {
                        return;
                    }
                } catch (Exception ex) {
                    LogReRegisterFailed(ex, agent.Id);

                    break;
                }
            }
        }
    }

    static readonly TimeSpan StartupTimeout     = TimeSpan.FromSeconds(90);
    static readonly TimeSpan MinSessionLifespan = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when the agent process exited before establishing a real interactive
    /// session. We require both that output was actually received (the read loop
    /// observed at least one chunk) AND that the gap between spawn and the last
    /// output is at least <see cref="MinSessionLifespan"/>. The
    /// <paramref name="hasReceivedOutput"/> guard prevents a no-output process
    /// from being misclassified when the <c>CreatedAt</c> and <c>LastOutputAt</c>
    /// field initializers happen to straddle a long pause.
    /// </summary>
    internal static bool IsStartupFailure(DateTime createdAt, DateTime lastOutputAt, bool hasReceivedOutput)
        => !hasReceivedOutput || lastOutputAt - createdAt < MinSessionLifespan;

    static readonly HashSet<string> ValidEffortLevels = ["low", "medium", "high", "max"];

    static readonly TimeSpan SessionIdPollInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan SessionIdPollTimeout  = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Vendor-dispatched, best-effort background fallback that discovers a spawned agent's
    /// session id from the transcript/rollout its harness writes and reports it to the server,
    /// for when the session-start hook (the primary source of the agent↔session link) fails or
    /// doesn't land in time — e.g. an expired kcap token 401s every /hooks POST, or an
    /// unattended/borrowed reviewer completes before the hook correlates. The server no longer
    /// gates incarnation completion on this id (it converges on daemon liveness), so this only
    /// resolves the id lazily for correlation/display and never blocks a launch.
    ///
    /// Claude reads its per-worktree Claude project dir (symlinked to the SOURCE repo's, shared
    /// with the user's own sessions — <see cref="SessionTranscriptLocator"/> disambiguates by
    /// cwd). Codex reads its <c>~/.codex/sessions</c> rollout tree (shared across all the user's
    /// Codex sessions — <see cref="CodexSessionRolloutLocator"/> disambiguates by
    /// <c>payload.cwd</c> + spawn time). A vendor with no daemon-side locator is a no-op: the
    /// hook stays its only session-id source.
    /// </summary>
    async Task DetectSessionIdAsync(AgentInstance agent, string vendor, DateTime spawnedAtUtc) {
        // The locator scans a shared dir, so a foreign-session file is cached in ruledOut and
        // never re-opened. A cwd is fixed, so a definitive non-match is permanent; a file with
        // no cwd yet (still being written) is NOT cached, so the agent's own freshly-created
        // transcript/rollout is always re-checked.
        Func<ISet<string>, string?>? locate = vendor.ToLowerInvariant() switch {
            "claude" => ruledOut => SessionTranscriptLocator.TryLocate(
                ClaudePaths.ProjectDir(agent.Worktree.Path), agent.Worktree.Path, spawnedAtUtc, ruledOut),
            "codex" => ruledOut => CodexSessionRolloutLocator.TryLocate(
                CodexPaths.Sessions, agent.Worktree.Path, spawnedAtUtc, ruledOut),
            _ => null,
        };

        if (locate is null) return;

        await PollForSessionIdAsync(agent, locate);
    }

    /// <summary>
    /// Shared poll loop for <see cref="DetectSessionIdAsync"/>. Polls <paramref name="locate"/>
    /// until it resolves a session id, the id is set by other means (hook succeeded), the agent
    /// exits (ReadCts), the daemon shuts down, or the timeout elapses. On a match it sets
    /// <see cref="AgentInstance.SessionId"/> and best-effort reports via AgentStatusChanged (live
    /// registry link) AND an <see cref="AgentRunHeartbeat"/> (so the server's restart-recovery
    /// FindAgentSessionIdAsync path works too). Once SessionId is set, the 30 s heartbeat loop and
    /// reconnect re-registration keep re-sending it, so a transient report failure self-heals.
    /// Never breaks the launch.
    /// </summary>
    async Task PollForSessionIdAsync(AgentInstance agent, Func<ISet<string>, string?> locate) {
        try {
            var deadline = DateTime.UtcNow + SessionIdPollTimeout;
            var ruledOut = new HashSet<string>();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(agent.ReadCts.Token, _shutdownCts.Token);

            while (DateTime.UtcNow < deadline) {
                if (agent.SessionId is not null) return; // linked by other means (hook succeeded)

                if (locate(ruledOut) is { } sessionId) {
                    agent.SessionId ??= sessionId;

                    LogSessionIdDetected(agent.Id, sessionId);

                    if (!agent.IsPrivate) {
                        // Enqueue the run-event first: it's a non-throwing local enqueue, whereas
                        // the SignalR status update can throw during a reconnect. Ordering it first
                        // ensures a transient connection hiccup can't skip the durable report — the
                        // status update then re-sends via the heartbeat loop regardless.
                        await _server.AppendAgentRunEventAsync(agent.Id, new AgentRunHeartbeat(sessionId));
                        await _server.AgentStatusChangedAsync(agent.Id, agent.Status, sessionId);
                    }

                    return;
                }

                await Task.Delay(SessionIdPollInterval, cts.Token);
            }

            LogSessionIdNotDetected(agent.Id, SessionIdPollTimeout.TotalSeconds);
        } catch (OperationCanceledException) {
            // Agent stopped or daemon shutting down — nothing to report.
        } catch (Exception ex) {
            // Best-effort by design: if the immediate report failed after SessionId was set,
            // the heartbeat loop / reconnect re-registration will re-send it.
            LogSessionIdDetectFailed(ex, agent.Id);
        }
    }

    async Task RunHeartbeatLoopAsync(CancellationToken ct) {
        while (await _heartbeatTimer.WaitForNextTickAsync(ct)) {
            // Phase B (D3): reap review-flow reviewers past their lifetime/idle backstop. Done
            // before the per-agent loop so a reaped reviewer isn't also heartbeated this tick. Reason
            // stamped on PendingEndReason so the end attribution is correct even if HandleStopAgent's
            // own end call loses to the read-loop's.
            // Phase B (D4 §6.4(2a)): retry killing any unconfirmed-death quarantined process,
            // draining those confirmed gone (frees admission). Single-flight (skips if a prior retry runs).
            _ = RetryQuarantineOnceAsync(ct);

            // Phase B (D4 §6.4(3)): re-run the orphan reap — record pass is epoch-guarded (never
            // touches a current-incarnation live agent), env-marker scan reaps a prior incarnation's
            // recordless survivors. Fire-and-forget; single-flight; swallows its own faults.
            _ = ReapOrphansOnceAsync(ct);

            foreach (var (id, reason) in FindReviewersToReap()) {
                if (_agents.TryGetValue(id, out var reviewer)) {
                    _logger.LogInformation(
                        "Reaping review-flow reviewer {AgentId} ({Reason}); age {AgeHours:F1}h, idle {IdleHours:F1}h",
                        id, reason, (ClockUtc() - reviewer.CreatedAt).TotalHours, (ClockUtc() - reviewer.LastOutputAt).TotalHours);
                    reviewer.PendingEndReason = reason;
                    _ = HandleStopAgent(id);
                }
            }

            // PrivateLocal agents get no heartbeats and no stuck-Starting auto-stop (deny-all;
            // the local user is present and drives them directly).
            foreach (var agent in _agents.Values.Where(a => (a.Status is "Starting" or "Running") && !a.IsPrivate)) {
                // Detect agents stuck in "Starting" with no output
                if (agent.Status                         == "Starting" &&
                    DateTime.UtcNow - agent.LastOutputAt > StartupTimeout) {
                    LogAgentStuck(agent.Id, (DateTime.UtcNow - agent.LastOutputAt).TotalSeconds, agent.Runtime.Pid, agent.Runtime.HasExited);
                    _ = HandleStopAgent(agent.Id);

                    continue;
                }

                _ = _server.AppendAgentRunEventAsync(
                    agent.Id,
                    new AgentRunHeartbeat(agent.SessionId)
                );
            }
        }
    }

    async Task RunDaemonHeartbeatLoopAsync(CancellationToken ct) {
        var loop = new DaemonHeartbeatLoop(_server, PingDeadline, _logger);

        while (await _daemonHeartbeat.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but we
            // run as an unobserved background Task — guarding here keeps
            // the loop alive even if a future change accidentally lets an
            // exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Heartbeat tick faulted — continuing loop");
            }
        }
    }

    async Task RunTokenRefreshLoopAsync(CancellationToken ct) {
        var loop = new TokenRefreshLoop(new TokenStoreRefreshPort(ProactiveRefreshWindow), _logger, ProactiveRefreshMinInterval);

        while (await _tokenRefresh.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but this runs as an
            // unobserved background Task — guard here so the loop survives even if a
            // future change lets an exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Token refresh tick faulted — continuing loop");
            }
        }
    }

    async Task RunSpoolDrainLoopAsync(CancellationToken ct) {
        var loop = new SpoolDrainLoop(
            _config.ServerUrl,
            new HookSpool(PathHelpers.ConfigPath("spool")),
            new TranscriptSpool(PathHelpers.ConfigPath("transcript-spool")),
            _logger,
            onWhatsDoneRequested: SpawnWhatsDoneGenerator);

        while (await _spoolDrain.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but this runs as an
            // unobserved background Task — guard here so the loop survives even if a
            // future change lets an exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Spool-drain tick faulted — continuing loop");
            }
        }
    }

    async Task CleanupAgentAsync(string agentId) {
        // Phase B (D1): claim the single-flight teardown BEFORE removing the agent from _agents.
        // TryGetValue (not TryRemove) keeps the agent COUNTED in ActiveCount for the whole teardown, so a
        // concurrent launch can't observe an under-counted EffectiveCount mid-teardown and over-admit
        // (the P2 admission race). The CompareExchange latch on the SAME instance guarantees exactly one
        // teardown even if the launch-catch and the read-loop's finally race here.
        if (!_agents.TryGetValue(agentId, out var agent)) return;
        if (Interlocked.CompareExchange(ref agent.CleanupStarted, 1, 0) != 0) return;

        // The reviewer process has exited by the time we get here (this runs off the read-loop's exit
        // path), so revoke its bridge token now — after any final submit_review_result was served.
        if (agent.ReviewerBridgeToken is { } reviewerToken) {
            try { _permissionBridge.RevokeReviewerToken(reviewerToken); } catch (Exception ex) { LogCleanupStepFailed(ex, "revoking reviewer bridge token", agentId); }
        }

        // Wake any attached local clients blocked on the user's stdin so they can flush the
        // last output and send Exited (the agent is going away). The exit code is already
        // captured on agent.Runtime, so disposing it below doesn't lose it.
        try { await agent.ExitedCts.CancelAsync(); } catch { /* best-effort */ }

        // Each cleanup step is best-effort so later steps still run
        try { await agent.Runtime.DisposeAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "disposing process", agentId); }

        if (_launchers.TryGetValue(agent.Vendor, out var launcher)) {
            try { launcher.Cleanup(agent); } catch (Exception ex) { LogCleanupStepFailed(ex, "launcher.Cleanup", agentId); }
        }

        // Owned worktrees are daemon-created and safe to remove. A borrowed cwd is the
        // user's own checkout (local in-place launch) — NEVER delete it or its branch:
        // RemoveAsync would Directory.Delete / `git worktree remove --force` + `branch -D`.
        // This is the spec's top safety invariant.
        if (agent.Work == WorkLocation.OwnedWorktree) {
            try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing worktree", agentId); }
        }

        // Phase B (D4 §6.4(2)/(2a)): confirm the process is actually gone before dropping its PID
        // record. Prove "still ours" with the STORED spawn identity — NEVER a freshly-recaptured token:
        // if the child exited and its pid was recycled, a re-capture would adopt the unrelated process's
        // identity and the heartbeat would later kill it. Quarantine ONLY when the pid is alive AND still
        // matches the stored identity (a stuck child of ours) — retain the record + count it against
        // admission (fail closed) so the heartbeat retries the kill to confirmed death. A dead pid, a
        // recycled pid (proven mismatch), or an agent with no captured identity → confirmed gone, delete
        // the record. Add to quarantine BEFORE removing from _agents so EffectiveCount never dips
        // (Activeized→quarantined is count-preserving).
        var pid = agent.Runtime.Pid;
        // Quarantine (retain + count) when the child may still be OURS-and-alive: a stored identity that
        // still matches (true) OR is uncomparable (null — unreadable/foreign token). Delete only on a
        // proven-gone pid: dead, a conclusive recycle (MatchesTri == false), or an agent that was never
        // identified. Collapsing the uncomparable case to "gone" would drop a live child's record.
        if (agent.StartIdentity is { } startIdentity
            && ProcessIdentity.IsAlive(pid)
            && ProcessIdentity.MatchesTri(pid, startIdentity) != false) {
            _quarantine?.Add(new AgentKillQuarantine.Entry(
                agent.Id, pid, startIdentity, agent.Kind.ToString(), agent.CreatedAt, agent.FlowRunId, agent.FlowRole));
            _logger.LogWarning(
                "Agent {AgentId} (pid {Pid}) still alive after teardown — quarantined for heartbeat kill-retry", agent.Id, pid);
        } else {
            DeletePidRecord(agentId); // confirmed dead / conclusively recycled pid / never-identified
        }

        // Now drop the agent from the live registry — after a surviving child is already in quarantine,
        // so a concurrent launch never sees EffectiveCount transiently under-count this agent.
        _agents.TryRemove(agentId, out _);

        // Skip server unregister during shutdown — _ct is cancelled and the call
        // would throw TaskCanceledException. The server detects the daemon
        // disconnection through SignalR's transport-level signals. Filtered
        // catch covers the residual race where shutdown fires mid-call.
        // PrivateLocal agents were never registered, so never unregister them (deny-all).
        if (!agent.IsPrivate && !_shutdownCts.IsCancellationRequested) {
            try { await _server.AgentUnregisteredAsync(agentId); } catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) { } catch (Exception ex) {
                LogCleanupStepFailed(ex, "unregistering", agentId);
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await _shutdownCts.CancelAsync();

        foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
            try {
                await agent.ReadCts.CancelAsync();
                await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5));
            } catch {
                /* best-effort */
            }
        }

        foreach (var agentId in _agents.Keys.ToList()) {
            await CleanupAgentAsync(agentId);
        }

        _heartbeatTimer.Dispose();
        _daemonHeartbeat.Dispose();
        _tokenRefresh.Dispose();
        _spoolDrain.Dispose();

        // Phase B2-b (sequenced-settlement design §4.2.2): complete the processor's serial lane so an
        // in-flight execution drains before the daemon exits.
        if (_processor is not null) await _processor.DisposeAsync();
    }

    /// <summary>
    /// Extracts readable text from the terminal output buffer by decoding UTF-8
    /// and stripping ANSI escape sequences. Returns the last ~500 chars to keep
    /// the error message reasonable for the UI snackbar.
    /// </summary>
    static string ExtractTerminalText(TerminalOutputBuffer buffer) {
        var chunks = buffer.GetAll();

        if (chunks.Count == 0) {
            return "";
        }

        var combined = new byte[chunks.Sum(c => c.Length)];
        var offset   = 0;

        foreach (var chunk in chunks) {
            Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
            offset += chunk.Length;
        }

        var raw     = Encoding.UTF8.GetString(combined);
        var cleaned = StripAnsiRegex().Replace(raw, "").Trim();

        return cleaned.Length > 500 ? cleaned[^500..] : cleaned;
    }

    // ── LoggerMessage source-generated methods ────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Launching agent {AgentId} for {Repo} (effort={Effort}, model={Model})")]
    partial void LogLaunching(string agentId, string repo, string effort, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} spawned (PID={Pid}, worktree={Worktree}, vendor={Vendor})")]
    partial void LogAgentSpawned(string agentId, int pid, string worktree, string vendor);

    [LoggerMessage(
        Level   = LogLevel.Warning,
        Message = "Interactive Codex agent {AgentId} launched with sandbox={Sandbox} approval={Approval}: "
                + "kcap approval prompts will never appear for this agent and/or it can reach outside its worktree")]
    partial void LogBridgeDefeatingPosture(string agentId, string sandbox, string approval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} exited with code {ExitCode}")]
    partial void LogAgentExited(string agentId, int? exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping agent {AgentId}")]
    partial void LogStopping(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not build the explicit reviewer-model resolved report for agent {AgentId} (vendor {Vendor}, launch model {LaunchModel}) — no resolver or model no longer resolves; skipping the report (the server will fail the attempt closed)")]
    partial void LogExplicitReviewerModelUnreportable(string agentId, string vendor, string launchModel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download launch attachments for agent {AgentId} (continuing)")]
    partial void LogAttachmentDownloadFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh borrowed-checkout snapshot for agent {AgentId}; rejecting the round and terminating the reviewer")]
    partial void LogBorrowedSnapshotRefreshFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendInput received for agent {AgentId} (vendor={Vendor}, chars={Chars}, attachments={Attachments})")]
    partial void LogSendInputReceived(string agentId, string vendor, int chars, int attachments);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendInput delivered to agent {AgentId}'s {Vendor} runtime ({Chars} chars)")]
    partial void LogSendInputDelivered(string agentId, string vendor, int chars);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendInput dropped: agent {AgentId} not found on this daemon ({KnownAgents} agents registered)")]
    partial void LogSendInputUnknownAgent(string agentId, int knownAgents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendInput dropped: agent {AgentId} is private — server-origin input is ignored")]
    partial void LogSendInputPrivateAgent(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendSpecialKey '{Key}' dropped: agent {AgentId} not found on this daemon ({KnownAgents} agents registered)")]
    partial void LogSendSpecialKeyUnknownAgent(string agentId, string key, int knownAgents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading output for agent {AgentId}")]
    partial void LogOutputReadError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} failed during startup (exit code {ExitCode}): {Reason}")]
    partial void LogStartupFailed(string agentId, int? exitCode, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent {AgentId} wedged on an unattended consent/trust dialog — failing the launch fast: {Reason}")]
    partial void LogConsentDialogWedge(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured failed-launch terminal tail for agent {AgentId} at {Path}")]
    partial void LogFailedLaunchCaptured(string agentId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download attachment {Id}: {Status}")]
    partial void LogAttachmentNotFound(string id, System.Net.HttpStatusCode status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attachment filename would escape directory: {FileName}")]
    partial void LogAttachmentPathEscape(string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error downloading attachment {Id}")]
    partial void LogAttachmentError(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to re-register agent {AgentId}")]
    partial void LogReRegisterFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send terminal dimensions for agent {AgentId} (read-only viewers may render garbled until the next reconnect)")]
    partial void LogTerminalDimsSendFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} stuck in Starting for {Seconds:F1}s with no output (PID={Pid}, exited={Exited}), terminating")]
    partial void LogAgentStuck(string agentId, double seconds, int pid, bool exited);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error {Step} for agent {AgentId}")]
    partial void LogCleanupStepFailed(Exception ex, string step, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to report resolved model for agent {AgentId} (continuing)")]
    partial void LogReportResolvedModelFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to launch agent {AgentId}")]
    partial void LogLaunchFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during cleanup of agent {AgentId}")]
    partial void LogCleanupError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error stopping agent {AgentId}")]
    partial void LogStopError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graceful /exit failed for agent {AgentId}; falling back to SIGTERM")]
    partial void LogGracefulExitFailed(Exception ex, string agentId);

    // The vendor is a parameter, not the literal "claude": this path stops EVERY hosted vendor, and
    // naming the wrong CLI in a stop diagnostic misdirects whoever is reading the daemon log to work
    // out which reviewer failed to exit.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Graceful /exit window of {Seconds}s elapsed for agent {AgentId} without {Vendor} exiting; falling back to SIGTERM")]
    partial void LogGracefulExitTimedOut(string agentId, double seconds, string vendor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to end session for agent {AgentId} (server may not record SessionEnded)")]
    partial void LogEndSessionFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to register local agent {AgentId} with the server (continuing; terminal stays usable)")]
    partial void LogLocalRegisterFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EndAgentSession for agent {AgentId} did not complete within {Seconds}s; proceeding with cleanup while the retry continues in the background (server reconciles on daemon disconnect)")]
    partial void LogEndSessionTimedOut(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Spawned what's-done generator for session {SessionId} (PID {Pid})")]
    partial void LogWhatsDoneSpawned(string sessionId, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to spawn what's-done generator for session {SessionId}")]
    partial void LogWhatsDoneSpawnFailed(Exception? ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to persist repo path for agent {AgentId}")]
    partial void LogRepoPathPersistFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP bind/forwarder setup failed for agent {AgentId} — proceeding with no live transcript for this session")]
    partial void LogAcpBindFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP bind for agent {AgentId} resolved after the agent had already finalized — aborting setup without registering a binding")]
    partial void LogAcpBindAbortedAgentGone(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP transcript forwarder faulted for agent {AgentId}")]
    partial void LogAcpForwarderFaulted(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP final transcript drain for agent {AgentId} exceeded its {Seconds}s budget — proceeding to end the session; any undrained transcript is lost")]
    partial void LogAcpFinalDrainTimedOut(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} linked to session {SessionId} via its transcript file (session-start hook fallback)")]
    partial void LogSessionIdDetected(string agentId, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "No transcript matched agent {AgentId}'s worktree within {Seconds:F0}s; session id stays hook-provided (or unknown if the hook failed)")]
    partial void LogSessionIdNotDetected(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcript-based session-id detection failed for agent {AgentId} (continuing; the session-start hook remains the primary link)")]
    partial void LogSessionIdDetectFailed(Exception ex, string agentId);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B\[[\?]?[0-9;]*[hlm]")]
    private static partial Regex StripAnsiRegex();

    /// <summary>
    /// Method exposed for unit tests so they can drive <see cref="HandleLaunchAgent"/>
    /// without going through SignalR. Keeps the private handler private to everyone else.
    /// </summary>
    internal Task HandleLaunchAgentForTest(LaunchAgentCommand cmd) => HandleLaunchAgent(cmd);

    /// <summary>Test-only: drive the per-agent PTY read loop directly (mirrors the fire-and-forget
    /// <c>_ = ReadAgentOutputAsync(agent)</c> in HandleLaunchAgent) so a seeded agent's consent-dialog
    /// fail-fast + tail-capture can be exercised without the full ReviewFlow launch gauntlet.</summary>
    internal Task ReadAgentOutputForTest(AgentInstance agent) => ReadAgentOutputAsync(agent);

    /// <summary>Test-only: the retained failed-launch log root, for asserting a capture landed.</summary>
    internal FailedLaunchLog? FailedLaunchLogForTest => _failedLaunchLog;

    /// <summary>Test-only: register a pre-built agent so cleanup/lifecycle can be driven directly.</summary>
    internal void RegisterAgentForTest(AgentInstance agent) => _agents[agent.Id] = agent;

    /// <summary>Test-only: look up a tracked agent by id (null if absent), so a launch test can
    /// assert the resolved <see cref="AgentInstance.Work"/> / <see cref="AgentInstance.Worktree"/>.</summary>
    internal AgentInstance? GetAgentForTest(string agentId) => _agents.GetValueOrDefault(agentId);

    /// <summary>Test-only entry point to the private cleanup path.</summary>
    internal Task CleanupAgentForTest(string agentId) => CleanupAgentAsync(agentId);

    /// <summary>Phase B (D4 §6.4(2a)): drive one quarantine-retry sweep (drains confirmed-dead
    /// entries and deletes their durable records) so a test needn't wait for a heartbeat tick.</summary>
    internal Task RetryQuarantineForTest() => RetryQuarantineOnceAsync(_shutdownCts.Token);

    /// <summary>Test-only: number of agents currently tracked (for awaiting cleanup).</summary>
    internal int ActiveAgentCountForTest => _agents.Count;

    /// <summary>Test-only entry point to the private stop handler (mirrors <see cref="HandleLaunchAgentForTest"/>).</summary>
    internal Task HandleStopAgentForTest(string agentId) => HandleStopAgent(agentId);

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.6): test-only entry point to the sequenced
    /// stop handler so a heal-barrier test can drive a Seq'd <see cref="StopAgentV2"/> through the
    /// processor's serial lane (advances the watermark; the confirmed-dead id then falls out of both
    /// LiveAgents and Quarantined). Mirrors <see cref="HandleStopAgentForTest"/>.</summary>
    internal Task HandleStopAgentV2ForTest(StopAgentV2 cmd) => HandleStopAgentV2(cmd);

    /// <summary>Test-only entry point to the private send-input handler (bracketed-paste submit).</summary>
    internal Task HandleSendInputForTest(SendInputCommand cmd) => HandleSendInput(cmd);

    /// <summary>Test-only entry point to the private probe-borrow-source handler.</summary>
    internal Task<BorrowProbeResult> HandleProbeBorrowSourceForTest(string path) => HandleProbeBorrowSource(path);

    /// <summary>Task 8: test-only entry point to the private reviewer-model preflight handler.</summary>
    internal ReviewerModelResolveResponseV1 HandleResolveReviewerModelForTest(ReviewerModelResolveRequestV1 req) =>
        HandleResolveReviewerModel(req);

    /// <summary>Task 8: test-only entry point to the pure explicit-model resolved-report builder.</summary>
    internal static ExplicitReviewerModelResolvedV1? BuildExplicitReviewerModelReportForTest(
            string agentId, string vendor, ExplicitReviewerModelLaunch block, IReviewerModelResolver? resolver) =>
        BuildExplicitReviewerModelReport(agentId, vendor, block, resolver);

    internal Task RegisterAgentForTestAsync(AgentInstance agent) => RegisterAgentAsync(agent);
    internal Task ReRegisterAgentsForTestAsync() => ReRegisterAgentsAsync();
    internal void HandleResizeTerminalForTest(ResizeTerminalCommand cmd) => _ = HandleResizeTerminal(cmd);
    internal LocalPermissionBridge PermissionBridgeForTest => _permissionBridge;
}

/// <summary>
/// Stand-in <see cref="IPtyProcess"/> for failed-launch cleanup paths where we
/// need an <see cref="AgentInstance"/> to satisfy <c>launcher.Cleanup</c> but no
/// live PTY ever existed. All members are no-ops; the launcher only reads
/// <see cref="AgentInstance.Worktree"/> and <see cref="AgentInstance.McpConfigPath"/>.
/// </summary>
internal sealed class NoopPtyProcess : IPtyProcess {
    public static readonly NoopPtyProcess Instance = new();
    NoopPtyProcess() { }

    public int  Pid       => 0;
    public bool HasExited => true;
    public int? ExitCode  => 0;

    public ValueTask DisposeAsync() => default;

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
    public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        yield break;
    }
#pragma warning restore CS1998

    public Task WriteAsync(string input) => Task.CompletedTask;
    public Task WriteAsync(byte[] data) => Task.CompletedTask;
    public void Resize(ushort     cols, ushort rows) { }
    public void SendInterrupt() { }
}
