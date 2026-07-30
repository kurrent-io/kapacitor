using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon;

public class DaemonConfig {
    public string   Name                { get; set; } = "";
    public string   ServerUrl           { get; set; } = "";
    public string[] AllowedRepoPaths    { get; set; } = [];
    public int      MaxConcurrentAgents { get; set; } = 5;

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

    /// <summary>
    /// Phase B (D4): root directory under which this daemon writes its per-agent PID records
    /// (<c>{StateDir}/{name}/agents/{agentId}.json</c>). Null → the shared daemon state dir
    /// (<c>DaemonLockPaths.Directory</c>); tests point it at a temp dir. The per-name subdir keeps a
    /// daemon's records "its own" so the startup reap only ever touches this daemon's leftovers.
    /// </summary>
    public string? StateDir { get; set; }

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

    public string WorktreeRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".capacitor",
        "worktrees"
    );

    public string ClaudePath { get; set; } = "claude";
    public string CodexPath  { get; set; } = "codex";

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

    /// <summary>Reserved — see CopilotPath. Overridable via KCAP_OPENCODE_PATH.</summary>
    public string OpenCodePath { get; set; } = "opencode";

    /// <summary>Reserved — see CopilotPath. Overridable via KCAP_GEMINI_PATH.</summary>
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
    /// Path to the kcap CLI binary. Used by the daemon to spawn auxiliary
    /// processes (e.g. <c>generate-whats-done</c>) when claude didn't fire its
    /// own session-end hook. Defaults to "kcap" — resolved via PATH, which
    /// works for npm installs that place both <c>kcap</c> and
    /// <c>kcap-daemon</c> in <c>node_modules/.bin</c>.
    /// </summary>
    public string CapacitorPath { get; set; } = "kcap";

    /// <summary>The argv the daemon was launched with, captured for self-respawn (detached restart).</summary>
    public IReadOnlyList<string> OriginalArgs { get; set; } = [];

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
