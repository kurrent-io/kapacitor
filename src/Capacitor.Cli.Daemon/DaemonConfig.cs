using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon;

public class DaemonConfig {
    public string   Name                { get; set; } = "";
    public string   ServerUrl           { get; set; } = "";
    public string[] AllowedRepoPaths    { get; set; } = [];
    public int      MaxConcurrentAgents { get; set; } = 5;

    /// <summary>
    /// Absolute path of the native <c>kcap</c> CLI binary, resolved as the daemon's sibling
    /// (<see cref="Capacitor.Cli.Core.Mcp.KcapBinaryCommand.ResolveCliSibling"/>) — inside the
    /// daemon, <see cref="Environment.ProcessPath"/> is <c>kcap-daemon</c>, NOT the binary that
    /// generated MCP registrations point at. Used to recognize a canonical absolute-path kcap
    /// entry (e.g. <see cref="ClaudeLauncher"/>'s worktree merge-skip). Null when no
    /// sibling resolves; consumers then recognize only the literal <c>"kcap"</c>.
    /// </summary>
    public string? KcapCliPath { get; set; } = Core.Mcp.KcapBinaryCommand.ResolveCliSibling();

    /// <summary>
    /// Phase B (D3): backstop lifetime/idle bounds for a hosted review-flow reviewer, enforced
    /// in the daemon heartbeat. A reviewer whose run went terminal on the server without the daemon
    /// hearing about it (or whose driver vanished) is reaped here so it can't hold a slot forever.
    /// Defaults 6h lifetime / 2h idle; <see cref="TimeSpan.Zero"/> disables that bound. Overridden at
    /// startup from env <c>KCAP_REVIEWER_MAX_LIFETIME</c>/<c>KCAP_REVIEWER_IDLE_TIMEOUT</c> (seconds);
    /// a profile config-key form is reserved but not yet wired. Interactive agents are never touched
    /// by these.
    /// </summary>
    public TimeSpan ReviewerMaxLifetime { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan ReviewerIdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>§2.7 B6 arm-A: how long a RESUMABLE hosted reviewer (app-server Codex) may sit idle
    /// between rounds before the daemon PARKS it (freeing the slot, keeping the thread for resume) —
    /// distinct from and shorter than <see cref="ReviewerIdleTimeout"/> (arm-B, the 2h hard reap).</summary>
    public TimeSpan ReviewerResumableIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The daemon-local ceiling on a held ACP turn with a frozen activity seq: once <c>TurnInFlight</c>
    /// has stayed true with no further <c>Advance()</c> for longer than this, the reviewer is reaped as
    /// <c>turn_wedged</c>. Applies to EVERY review-flow reviewer, alongside
    /// <see cref="ReviewerMaxLifetime"/>/<see cref="ReviewerIdleTimeout"/> — the server-sent inactivity
    /// bound gates nothing here (see <c>AgentOrchestrator.FindReviewersToReap</c>).
    ///
    /// <para>Deliberately NOT sent by the server (spec decision 6): its equivalent
    /// <c>Flows:TurnWedgeCeilingSeconds</c> is independently defaulted on the same 60m (the
    /// <c>kcap watch</c> long-tool envelope) rather than plumbed onto the wire — this is a local safety
    /// net against an evidence-free held turn. <see cref="TimeSpan.Zero"/> disables it. Overridden at
    /// startup from env <c>KCAP_REVIEWER_TURN_WEDGE_CEILING</c> (seconds).</para>
    /// </summary>
    public TimeSpan ReviewerTurnWedgeCeiling { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Where this daemon's files live.</summary>
    public DaemonStore Store {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Store was never set; pass a {nameof(DaemonStore)} in from the entry point.");
        set;
    }

    /// <summary>Where this daemon's kcap configuration lives — a different anchor from
    /// <see cref="Store"/>, which ignores <c>KCAP_CONFIG_DIR</c> by design.</summary>
    public ConfigRoot ConfigRoot {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.ConfigRoot was never set; pass a {nameof(Core.ConfigRoot)} in from the entry point.");
        set;
    }

    /// <summary>The profile this daemon resolved at boot. A separate process from the CLI, so it
    /// resolves its own; nothing re-resolves later, which is what keeps its token reads and its
    /// daemon identity naming one profile for the daemon's whole life.</summary>
    public ProfileContext Profiles {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Profiles was never set; pass a {nameof(ProfileContext)} in from the entry point.");
        set;
    }

    /// <summary>Phase B (D4): a fresh per-boot epoch (GUID). Written into each spawned child's
    /// <c>KCAP_DAEMON_EPOCH</c> env marker; the startup env-marker scan kills same-daemon children
    /// whose epoch differs from the current one (i.e. survivors of a prior incarnation). Null → the
    /// orchestrator generates one at construction. <c>DaemonRunner</c> pins this before building
    /// services (Phase B2-b) so the advertised connect epoch and the orchestrator's own boot epoch
    /// agree.</summary>
    public string? DaemonEpoch { get; set; }

    /// <summary>
    /// Phase B2-b (sequenced-settlement design §4.2.3): the durable coverage boot-chain verdict,
    /// folded by <c>CoverageJournal.RecordBoot</c> in <c>DaemonRunner</c> BEFORE any Connect/spawn and
    /// advertised on <c>DaemonConnect</c>. True only where OS containment leaves genuinely no recordless
    /// survivor class (the Windows Job Object); absent/false ⇒ "has a recordless class" (the server
    /// requires per-id death proof). The server consumes it only on Windows, so a Linux/macOS value is
    /// inert. Fail-closed to false when the fold/persist fails.
    /// </summary>
    public bool RecordlessSurvivorsImpossible { get; set; }

    /// <summary>
    /// Per-process GUID generated at startup, also written to the daemon's
    /// flock-file content. Sent over <c>DaemonConnect</c> so the server
    /// can tell "same daemon reconnecting" from "different daemon
    /// claiming the same name". Set in <c>DaemonRunner.RunAsync</c> once
    /// the lock has been acquired; <c>null</c> in tests that bypass lock
    /// acquisition.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Daemon binary version (<c>AssemblyInformationalVersion</c>). Sent
    /// over <c>DaemonConnect</c> and surfaced on the server's
    /// <c>Daemon connected:</c> log line + <c>DaemonInfo</c>. Set in
    /// <c>DaemonRunner.RunAsync</c>.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Vendor tokens this daemon can actually spawn — populated in
    /// <c>DaemonRunner.RunAsync</c> by probing each registered
    /// <c>IHostedAgentLauncher.IsAvailable()</c>. Sent over
    /// <c>DaemonConnect</c> so the server's launch dialog only
    /// offers vendors this daemon has installed. <c>null</c> when the
    /// host hasn't been built yet or in tests that bypass the runner.
    /// </summary>
    public string[]? SupportedVendors { get; set; }

    /// <summary>
    /// Vendor tokens this daemon can run fully unattended (a subset of
    /// <see cref="SupportedVendors"/>) — populated in
    /// <c>DaemonRunner.RunAsync</c> by probing each registered
    /// <c>IHostedAgentRuntimeFactory.IsAvailable()</c> and
    /// <c>.SupportsUnattended</c>. Sent over <c>DaemonConnect</c> so the
    /// server can gate a reviewer-vendor override on unattended capability,
    /// not merely installation. <c>null</c> when the host hasn't been built
    /// yet or in tests that bypass the runner.
    /// </summary>
    public string[]? UnattendedVendors { get; set; }
    public IReadOnlyList<UnattendedVendorCapability>? UnattendedVendorCapabilities { get; set; }

    /// <summary>Per-vendor fingerprint of the binary <see cref="UnattendedVendorCapabilities"/> was
    /// probed from, taken before that probe; the vendor CLI watcher's starting point.</summary>
    public IReadOnlyDictionary<string, Services.CliBinaryStat?>? UnattendedVendorBaselines { get; set; }

    /// <summary>
    /// Vendor tokens this daemon accepts a launch-time ACP permission preset for — the installed
    /// hostable vendors (a subset of <see cref="SupportedVendors"/>) that route permissions through
    /// the ACP bridge, computed INDEPENDENTLY of unattended certification (a preset is an
    /// interactive-launch feature). Sent over <c>DaemonConnect</c> so the server enables the launch
    /// dialog's preset selector and refuses a preset toward a non-advertising daemon. <c>null</c> when
    /// the host hasn't been built yet or in tests that bypass the runner.
    /// </summary>
    public string[]? AcpPresetVendors { get; set; }

    /// <summary>Vendor tokens this daemon accepts a launch-time permission mode for — Claude when it
    /// is hosted. Sent over <c>DaemonConnect</c> so the server refuses a mode toward a daemon that
    /// would ignore it. <c>null</c> until the host is built.</summary>
    public string[]? PermissionModeVendors { get; set; }

    /// <summary>The home directory this daemon resolved at boot. Every home-derived daemon path
    /// reads it, so a descendant can't be handed a different one than the entry point chose.</summary>
    public UserHome Home {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Home was never set; pass a {nameof(UserHome)} in from the entry point.");
        set;
    }

    public string WorktreeRoot { get; set; } = "";

    public string ClaudePath { get; set; } = "claude";
    public string CodexPath  { get; set; } = "codex";

    /// <summary>Transport for hosted Codex reviewers: <c>pty</c> (default) or <c>app-server</c>.
    /// Set via <c>KCAP_CODEX_TRANSPORT</c>. Governs NEW review-flow launches only; interactive
    /// launches always take the PTY path in this phase. The effective decision (this selection AND
    /// the version floor) is resolved once into <see cref="CodexAppServerActive"/>.</summary>
    public string CodexTransport { get; set; } = "pty";

    /// <summary>Resolved once at startup: true when <see cref="CodexTransport"/> is <c>app-server</c>
    /// AND the installed Codex meets the app-server version floor. Read by BOTH the launch router and
    /// the certification advertisement so the advertised policy and the transport used are one fact.
    /// Never set from config directly.</summary>
    public bool CodexAppServerActive { get; set; }

    /// <summary>Per-daemon opt-in: also host INTERACTIVE Codex agents over app-server, not just
    /// unattended reviewers. Requires <see cref="CodexAppServerActive"/> — this widens which launches
    /// take that transport, it does not select it. Off unless an operator sets
    /// <c>KCAP_CODEX_APPSERVER_INTERACTIVE</c> on this daemon, so turning it on moves one host.
    ///
    /// <para>Scope: INTERACTIVE launches the SERVER dispatches (the web launch dialog). PR review and
    /// review flows are untouched by this switch — review flows already take app-server wherever it is
    /// selected, and PR review stays on PTY. `kcap agent start` spawns an attachable terminal through the
    /// local control socket, which never reaches the runtime factory, so it stays PTY regardless.</para></summary>
    public bool CodexAppServerInteractive { get; set; }

    /// <summary>Seconds an interactive hosted Codex approval (<c>*/requestApproval</c>) waits for the
    /// user before failing closed (deny). Overridable via <c>KCAP_CODEX_APPSERVER_APPROVAL_TIMEOUT_SECONDS</c>.
    /// Consumed as <c>TimeSpan.FromSeconds(Math.Max(1, …))</c>.</summary>
    public int CodexAppServerApprovalTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Path or bare command for the Cursor CLI's ACP entry point, spawned as
    /// <c>{CursorPath} acp</c> by <c>AcpHostedAgentRuntimeFactory</c>. Overridable
    /// via <c>KCAP_CURSOR_PATH</c>, mirroring <see cref="ClaudePath"/>/<see cref="CodexPath"/>.
    /// </summary>
    public string CursorPath { get; set; } = "cursor-agent";

    /// <summary>
    /// Family-prefix default model for Cursor ACP sessions, e.g.
    /// <c>"claude-sonnet-4-5"</c>. Cursor's wire protocol requires the exact, parameterized
    /// <c>modelId</c> from <c>session/new</c>'s <c>availableModels</c> (e.g.
    /// <c>claude-sonnet-4-5[thinking=true,context=200k]</c>), so this bare family name is resolved
    /// against that list at launch time by <c>AcpModelResolver.Resolve</c> — not sent verbatim.
    /// Overridable via <c>KCAP_CURSOR_MODEL</c>, mirroring <see cref="CursorPath"/>. A per-launch
    /// model override (<c>RuntimeStartContext.Model</c>, when the launch specifies one) takes
    /// precedence over this daemon-wide default — see <c>AcpHostedAgentRuntimeFactory</c>.
    /// </summary>
    public string CursorModel { get; set; } = "claude-sonnet-4-5";

    /// <summary>Reserved for a future AcpVendorDescriptor (this change adds the plumbing; no
    /// descriptor consumes this yet). Overridable via KCAP_COPILOT_PATH, mirroring CursorPath.</summary>
    public string CopilotPath { get; set; } = "copilot";

    /// <summary>
    /// Path or bare command for AWS Kiro CLI's ACP entry point, spawned as <c>{KiroPath} acp</c> by
    /// <c>AcpHostedAgentRuntimeFactory</c>. Overridable via <c>KCAP_KIRO_PATH</c>, mirroring
    /// <see cref="CursorPath"/>.
    ///
    /// <para><b>The default is <c>kiro-cli</c>, not <c>kiro</c>.</b> This field predates the
    /// descriptor that now consumes it and was originally defaulted to <c>"kiro"</c> while unused.
    /// The shipped binary is <c>kiro-cli</c> — it is what <c>PluginCommand.KiroBinary</c> resolves and
    /// what a standard install puts on PATH; <c>kiro</c> is not present. Because availability is
    /// <c>CliResolver.Exists(KiroPath)</c>, leaving the old default would have meant Kiro was never
    /// advertised as a hosted-agent vendor on a correct install until the operator discovered
    /// <c>KCAP_KIRO_PATH</c> — a silent no-op rather than a visible failure. Only one name is probed;
    /// operators with a differently-named binary set the env var.</para>
    /// </summary>
    public string KiroPath { get; set; } = "kiro-cli";

    /// <summary>
    /// Daemon-wide default model for Kiro ACP sessions, e.g. <c>"claude-haiku-4.5"</c>, resolved
    /// against <c>session/new</c>'s <c>availableModels</c> at launch time by
    /// <c>AcpModelResolver.Resolve</c> (Kiro's ids are bare, unlike Cursor's parameterized ones,
    /// but the resolution path is shared) and applied via <c>session/set_model</c> —
    /// probe-verified at effect level (<c>docs/probes/2026-08-05-kiro-model-override/</c>).
    /// Overridable via <c>KCAP_KIRO_MODEL</c>, mirroring <see cref="CursorModel"/>.
    ///
    /// <para>Unlike <see cref="CursorModel"/> the default is NULL, deliberately: zero-configuration
    /// Kiro hosting keeps the vendor's own default model, with nothing requested and nothing
    /// reported — the behaviour Kiro hosting shipped with. A per-launch model override
    /// (<c>RuntimeStartContext.Model</c>) takes precedence over this daemon-wide default — see
    /// <c>AcpHostedAgentRuntimeFactory</c>.</para>
    /// </summary>
    public string? KiroModel { get; set; }

    /// <summary>
    /// Whether THIS daemon may run Gemini as an unattended review-flow reviewer. **Default TRUE — the
    /// variable is an opt-OUT.**
    ///
    /// <para>An unattended reviewer runs in a daemon-owned worktree with this daemon's own HOME, so
    /// repository content that steers the model into tool use gets code execution with this user's full
    /// authority — including the credentials in the token store, writes that reach other worktrees and the
    /// installed CLI, and processes that outlive the review. That is a genuine risk and it is unchanged.
    /// What changed is the recognition that a PER-VENDOR gate never addressed it: the reviewer vendor is
    /// a caller-chosen parameter, and Claude, Codex, Cursor and Copilot have the same authority with no
    /// gate at all, so anyone the gate excluded just asked for one of those.</para>
    ///
    /// <para>The build affirmation still applies: the reviewer's containment is the vendor's exact-name
    /// MCP allowlist, a behaviour of the installed build, so a build BELOW this daemon's recorded floor
    /// is refused. The floor is seeded automatically at startup so it never blocks a first launch —
    /// see <c>GeminiReviewerCapability</c>.</para>
    ///
    /// <para>Set <c>KCAP_GEMINI_UNATTENDED_REVIEWER=0</c> in the DAEMON's environment to disable, which
    /// for a supervised daemon means the service unit — <c>ServiceEnvironment</c> carries it there,
    /// since a unit inherits nothing from the installing shell.</para>
    /// </summary>
    public bool GeminiUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// Whether THIS daemon may run Kiro as an unattended review-flow reviewer. **Default TRUE — the
    /// variable is an opt-OUT** (<c>KCAP_KIRO_UNATTENDED_REVIEWER=0</c> disables). A trusted read tool
    /// is not path-scoped, so a review can read every file this daemon user can and return it to the
    /// requester — true here and equally true of the never-gated Claude/Codex/Cursor/Copilot reviewers,
    /// which can additionally write and execute. See <c>KiroReviewerCapability</c>.
    /// </summary>
    public bool KiroUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// One absolute budget, in seconds, for a Kiro reviewer launch: spawn through the first prompt
    /// completing. On expiry the child is terminated, its isolated home removed, and the launch fails
    /// with a coded error.
    ///
    /// <para>Not a per-stage timeout — a fresh one per stage lets a slow sequence approach a multiple
    /// of the budget. The failure it exists for is measured: an unauthenticated kiro-cli does not
    /// error, it opens a browser prompt and stays alive indefinitely.</para>
    /// </summary>
    public int KiroReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Path or bare command for the Antigravity CLI, spawned per turn by
    /// <c>AntigravityHostedAgentRuntimeFactory</c>. Overridable via <c>KCAP_ANTIGRAVITY_PATH</c>.
    ///
    /// <para><b>The default is <c>agy</c></b> — the name a standard install puts on PATH. Because
    /// availability is <c>CliResolver.Exists(AntigravityPath)</c>, a wrong default would mean the
    /// vendor is never advertised on a correct install: a silent no-op, not a visible failure.</para>
    /// </summary>
    public string AntigravityPath { get; set; } = "agy";

    /// <summary>Daemon-wide default model for Antigravity reviewer launches, passed as
    /// <c>--model</c>. Null leaves agy on its own default. An unknown slug makes agy hard-fail,
    /// which is a clean audit signal rather than a silent downgrade.</summary>
    public string? AntigravityModel { get; set; }

    /// <summary>Whether THIS daemon may run unattended Antigravity reviews. **Default TRUE — the
    /// variable is an opt-OUT** (<c>KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=0</c> disables), matching the
    /// never-gated Claude/Codex/Cursor/Copilot reviewers that carry the same authority.
    ///
    /// <para>The minimum <c>agy</c> build is deliberately NOT config: it is a daemon-owned record
    /// (<c>ReviewerVersionStore</c>, moved by <c>kcap daemon reviewer affirm --vendor antigravity</c>),
    /// exactly as for Kiro and Gemini. A floor an operator could set from a shell profile would be
    /// re-affirmed by their dotfiles rather than by them.</para></summary>
    public bool AntigravityUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>Absolute ceiling on the FIRST turn — spawn, NDJSON handshake and auth. An
    /// unauthenticated agy can sit on an interactive OAuth wait, so this is what turns that into a
    /// bounded, coded failure.</summary>
    public int AntigravityReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>Ceiling on every SUBSEQUENT turn. Bounding only the launch would leave turn 2+
    /// unbounded, and with ReadOutputAsync parked by design nothing else would ever complete.</summary>
    public int AntigravityReviewerTurnTimeoutSeconds { get; set; } = 600;

    /// <summary>Path or bare command for SST OpenCode's ACP entry point, spawned as
    /// <c>{OpenCodePath} acp</c> by <c>AcpHostedAgentRuntimeFactory</c>. No longer reserved: it drives
    /// interactive hosting, and availability is <c>CliResolver.Exists(OpenCodePath)</c>. Overridable
    /// via <c>KCAP_OPENCODE_PATH</c>.</summary>
    public string OpenCodePath { get; set; } = "opencode";

    /// <summary>
    /// Optional daemon-wide default model for hosted OpenCode agents, resolved against
    /// <c>session/new</c>'s selectable-model list at launch time by <c>AcpModelResolver.Resolve</c>
    /// and applied via <c>session/set_config_option</c> — probe-verified at effect level
    /// (<c>docs/probes/2026-08-07-opencode-acp/</c> §2: the model self-identified as the requested id).
    /// Overridable via <c>KCAP_OPENCODE_MODEL</c>, mirroring <see cref="KiroModel"/>.
    ///
    /// <para>Ids are <c>provider/model</c> (e.g. <c>opencode/big-pickle</c>), and OpenCode publishes
    /// its list as <c>configOptions</c> rather than a <c>models</c> object — see
    /// <c>AcpSessionModelList</c>. A display label ("DeepSeek V4 Flash Free") also resolves, via the
    /// resolver's name arm.</para>
    ///
    /// <para>Like <see cref="KiroModel"/> and unlike <see cref="CursorModel"/> the default is NULL,
    /// deliberately: a zero-configuration launch keeps OpenCode's own configured default and reports
    /// no model. A per-launch <c>RuntimeStartContext.Model</c> takes precedence over this
    /// daemon-wide default.</para>
    /// </summary>
    public string? OpenCodeModel { get; set; }

    /// <summary>
    /// Whether THIS daemon may run OpenCode as an unattended review-flow reviewer. **Default TRUE — the
    /// variable is an opt-OUT** (<c>KCAP_OPENCODE_UNATTENDED_REVIEWER=0</c> disables). This is the most
    /// contained reviewer of the eight: no shell, no write, no network. Its read tools are still not
    /// path-scoped, as with every other reviewer. See <c>OpenCodeReviewerCapability</c>.
    /// </summary>
    public bool OpenCodeUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// One absolute budget, in seconds, for an OpenCode reviewer launch: spawn through the first prompt
    /// completing. On expiry the child is terminated, its isolated config dir removed, and the launch
    /// fails with a coded error.
    ///
    /// <para>Not a per-stage timeout — a fresh one per stage lets a slow sequence approach a multiple of
    /// the budget. The failure it exists for is the same one Kiro's budget covers: an unauthenticated
    /// vendor CLI that does not error but waits on an interactive login.</para>
    /// </summary>
    public int OpenCodeReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>Path or bare command for Pi's RPC entry point, spawned as
    /// <c>{PiPath} --mode rpc</c> by <c>PiRpcHostedAgentRuntimeFactory</c>. Interactive hosting only
    /// in PR-1 — the reviewer lane is not implemented yet. Availability is
    /// <c>CliResolver.Exists(PiPath)</c>. Overridable via <c>KCAP_PI_PATH</c>.</summary>
    public string PiPath { get; set; } = "pi";

    /// <summary>
    /// Optional daemon-wide default model for hosted Pi agents, passed as <c>--model</c> on the
    /// spawned <c>pi --mode rpc</c> child. Overridable via <c>KCAP_PI_MODEL</c>, mirroring
    /// <see cref="OpenCodeModel"/>.
    ///
    /// <para>Like <see cref="OpenCodeModel"/> and <see cref="KiroModel"/> the default is NULL,
    /// deliberately: a zero-configuration launch keeps Pi's own configured default and reports no
    /// model. A per-launch <c>RuntimeStartContext.Model</c> takes precedence over this daemon-wide
    /// default (the <c>"default"</c> sentinel falls through to it, same convention as every other
    /// vendor's <c>ResolveModel</c>).</para>
    /// </summary>
    public string? PiModel { get; set; }

    /// <summary>Path or bare command for Google Gemini CLI's ACP entry point, spawned as
    /// <c>{GeminiPath} --experimental-acp …</c> by <c>AcpHostedAgentRuntimeFactory</c>. No longer
    /// reserved: it drives interactive hosting AND the gated unattended reviewer, whose build-affirmation
    /// probe reads whichever binary this names. Overridable via <c>KCAP_GEMINI_PATH</c>.</summary>
    public string GeminiPath { get; set; } = "gemini";

    /// <summary>
    /// Opt-in, off-by-default ACP wire/content debug logging (<c>KCAP_ACP_DEBUG_FRAMES</c>). When
    /// <see langword="false"/> (the default), the ACP layers (<c>AcpEventTranslator</c>,
    /// <c>AcpChildProcess</c>, <c>AcpConnection</c>) log shape/length only for the traffic that would
    /// otherwise carry prompt/tool/file content — an unrecognized <c>session/update</c> kind, raw
    /// <c>cursor-agent</c> stderr lines, and full inbound/outbound JSON-RPC frames. When
    /// <see langword="true"/>, those same call sites log full (length-capped) content at Debug for
    /// local troubleshooting — never sent to the server, never written to the transcript. Read from
    /// the env var in <c>DaemonRunner.RunAsync</c>, which also emits a one-time startup Warning when
    /// this is on, since the logged content may include sensitive payloads.
    /// </summary>
    public bool DebugFrames { get; set; }

    /// <summary>
    /// Kill switch for ACP hosted-agent crash reconnect/resume (<c>KCAP_ACP_RECONNECT</c>). Default
    /// ON; set <c>0</c>/<c>false</c> to disable globally. When off — or for a vendor whose
    /// descriptor is not probe-verified reconnect-capable, a launch that is a review flow, or a
    /// session whose handshake didn't advertise <c>loadSession</c> — a child-process death keeps
    /// today's behavior byte-for-byte: the read loop ends and the agent finalizes. Deliberately the
    /// only knob: attempts, backoff, and the per-session resume cap are fixed constants
    /// (reconnect spec §4).
    /// </summary>
    public bool AcpReconnectEnabled { get; set; } = true;

    /// <summary>
    /// Path to the kcap CLI binary. Used by the daemon to spawn auxiliary
    /// processes (e.g. <c>generate-whats-done</c>) when claude didn't fire its
    /// own session-end hook. Defaults to "kcap" — resolved via PATH, which
    /// works for npm installs that place both <c>kcap</c> and
    /// <c>kcap-daemon</c> in <c>node_modules/.bin</c>.
    /// </summary>
    public string CapacitorPath { get; set; } = "kcap";

    /// <summary>The argv the daemon was launched with, captured for self-respawn (detached restart).</summary>
    public IReadOnlyList<string> OriginalArgs { get; set; } = [];

    /// <summary>
    /// Raw value of <c>KCAP_CONSENT_SEED_DEFAULT</c>, captured off ambient env at boot by
    /// <see cref="DaemonRunner.CaptureBootCarriers"/> and immediately removed from the process
    /// environment so no descendant (PTY-spawned agent, ACP child) can observe it by inheritance.
    /// Unvalidated here — <see cref="DaemonRunner.RunBootChecksAsync"/> classifies/validates this
    /// directive via <c>LaunchConsentStore.BootSeed</c>.
    /// </summary>
    public string? ConsentSeedDirective { get; set; }

    /// <summary>
    /// Raw value of <c>KCAP_EXPECT_SERVER_URL</c>, captured/removed the same way as
    /// <see cref="ConsentSeedDirective"/>. Unvalidated here — <see cref="DaemonRunner.RunBootChecksAsync"/>
    /// checks it against the resolved <see cref="ServerUrl"/>.
    /// </summary>
    public string? ExpectedServerUrl { get; set; }

    /// <summary>
    /// Raw value of <c>KCAP_BOOT_ATTEMPT</c>, captured/removed the same way as
    /// <see cref="ConsentSeedDirective"/>. Per-launch-action: NOT re-injected into a self-respawned
    /// successor (see <c>DetachedRespawnStrategy.SuccessorEnvOverlay</c>) — a self-respawn is not
    /// the app's own action.
    /// </summary>
    public string? BootAttemptId { get; set; }

    public List<string> Validate() {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerUrl)) {
            errors.Add("ServerUrl is required");
        } else if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) {
            errors.Add($"ServerUrl must be a valid http/https URL, got: {UnusableUrlDiagnostic.Sanitize(ServerUrl)}");
        }

        if (MaxConcurrentAgents < 1) {
            errors.Add("MaxConcurrentAgents must be at least 1");
        }

        if (string.IsNullOrWhiteSpace(WorktreeRoot)) {
            errors.Add("WorktreeRoot is required");
        }

        return errors;
    }

    public bool IsRepoAllowed(string repoPath) {
        if (AllowedRepoPaths.Length == 0) {
            return true;
        }

        // Compare with forward slashes so an operator's "/*" wildcard and prefix matching work
        // regardless of the host's native separator (Windows canonical paths use '\'). No-op on POSIX.
        var path = repoPath.Replace('\\', '/');

        return AllowedRepoPaths.Any(raw => {
                var pattern = raw.Replace('\\', '/');

                if (pattern.EndsWith("/*")) {
                    var prefix = pattern[..^1]; // keep trailing slash: "/allowed/"

                    return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(path, pattern[..^2], StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase);
            }
        );
    }
}
