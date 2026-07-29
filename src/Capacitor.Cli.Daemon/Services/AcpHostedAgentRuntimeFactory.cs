using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for ACP-speaking vendors, parameterized over an
/// <see cref="AcpVendorDescriptor"/> — spawns <c>{descriptor.ResolveBinaryPath(config)}
/// {descriptor.Argv}</c> as a child process, wraps its stdio in an <see cref="AcpConnection"/> +
/// <see cref="AcpChildProcess"/>, and drives the ACP handshake via
/// <see cref="AcpHostedAgentRuntime.StartAsync"/>. Cursor and Copilot descriptors share this path;
/// each descriptor declares its own unattended and MCP transport capabilities.
///
/// <b>Spec-review Finding 4:</b> gained a <see cref="ServerConnection"/> constructor
/// dependency so every runtime this factory produces has the real permission/elicitation bridge
/// wired — <see cref="StartAsync"/> passes <c>ctx.AgentId</c> and
/// <see cref="ServerConnection.RequestAcpInteractionAsync"/> into <see cref="AcpHostedAgentRuntime"/>'s
/// optional parameters instead of leaving them at their <c>""</c>/<see langword="null"/> defaults.
///
/// <b>Round-4 Finding 3:</b> process-spawning + stream construction is extracted into
/// <paramref name="connectionSource"/> (defaulting to <see cref="StartRealProcess"/>) purely so
/// <see cref="AcpHostedAgentRuntimeFactoryTests"/> can construct THIS class for real and drive its
/// REAL <see cref="StartAsync"/> against an in-memory <c>FakeAcpAgent</c> peer instead of a real
/// <c>cursor-agent acp</c> child process (unavailable and non-portable in CI) — the seam changes
/// nothing about production behavior, since the default IS the real `Process.Start`-backed path.
/// </summary>
internal sealed partial class AcpHostedAgentRuntimeFactory(
        AcpVendorDescriptor                                                            descriptor,
        DaemonConfig                                                                   config,
        ILoggerFactory                                                                 loggerFactory,
        ServerConnection                                                               connection,
        Func<RuntimeStartContext, (Stream Input, Stream Output, IAcpProcess Process)>? connectionSource = null
    ) : IHostedAgentRuntimeFactory {
    readonly Func<RuntimeStartContext, (Stream Input, Stream Output, IAcpProcess Process)> _connectionSource =
        connectionSource ?? (ctx => StartRealProcess(descriptor, config, ctx, loggerFactory));

    readonly ILogger _logger = loggerFactory.CreateLogger<AcpHostedAgentRuntimeFactory>();

    /// <summary>Resolved ONCE for this daemon's platform. Every borrowed-review consumer below reads
    /// this, never the static descriptor — see <see cref="ResolvedBorrowedReviewPolicy"/> for why
    /// splitting them is how advertisement and spawn drift apart.</summary>
    internal static ResolvedBorrowedReviewPolicy PolicyFor(AcpVendorDescriptor descriptor) =>
        descriptor.Vendor == AcpVendorDescriptors.Copilot.Vendor
            ? CopilotBorrowedReviewPolicy.Current
            : ResolvedBorrowedReviewPolicy.FromDescriptor(descriptor);

    readonly ResolvedBorrowedReviewPolicy _policy = PolicyFor(descriptor);

    public string Vendor             => descriptor.Vendor;
    public bool   SupportsUnattended => descriptor.SupportsUnattended;
    public bool   SupportsBorrowedReviewFlow => _policy.Supported;
    public bool   BorrowedReviewRequiresIndependentSnapshot =>
        _policy.Containment == AcpBorrowedReviewContainment.IndependentSnapshot;
    public string? BorrowedReviewContainment => _policy.Containment switch {
        AcpBorrowedReviewContainment.NativeToolClamp => "native-tool-clamp",
        AcpBorrowedReviewContainment.IndependentSnapshot => CursorBorrowedReviewValidation.Containment,
        _ => null
    };

    /// <summary>ACP / multi-provider vendors have no authoritative reviewer-model resolver yet, so this
    /// stays <see langword="null"/> — the vendor advertises no <c>SupportsReviewerModelResolution</c>
    /// and the server refuses a v3 model override for it, while its existing vendor-only unattended
    /// support is untouched.</summary>
    public IReviewerModelResolver? ReviewerModelResolver => null;

    public bool IsAvailable() => CliResolver.Exists(descriptor.ResolveBinaryPath(config));

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        LogLaunching(ctx.AgentId, Vendor, ctx.Worktree.Path);
        AcpMetrics.Launches.Add(1);

        ValidateBorrowedArtifact(ctx, _policy);

        // Fail closed BEFORE _connectionSource spawns a child (a later gate would leak one). Null for
        // a non-review launch; the built MCP list for a valid review flow.
        var reviewMcp = ValidateAndBuildReviewFlowMcp(ctx, descriptor, _policy);

        var unattendedInteractionPolicy = ctx.IsReviewFlow
            ? descriptor.UnattendedInteractionPolicy
            : AcpUnattendedInteractionPolicy.Disabled;

        var runtimeLogger = loggerFactory.CreateLogger<AcpHostedAgentRuntime>();
        var connLogger    = loggerFactory.CreateLogger<AcpConnection>();

        var (input, output, acpProcess) = _connectionSource(ctx);
        var acpConnection = new AcpConnection(input, output, connLogger, config.DebugFrames);

        // Spec-review Finding 4: real production wiring — every launch now gets the
        // permission/elicitation bridge, not the default MethodNotFound/decline.
        var runtime = new AcpHostedAgentRuntime(
            acpConnection,
            acpProcess,
            runtimeLogger,
            agentId: ctx.AgentId,
            requestInteraction: connection.RequestAcpInteractionAsync,
            debugFrames: config.DebugFrames,
            vendor: descriptor.Vendor,
            modelSelector: descriptor.ModelSelector,
            unattendedInteractionPolicy: unattendedInteractionPolicy
        );

        // Review flow: the injected result channel + allowlist. Otherwise unchanged (null today).
        var mcpServers = ctx.IsReviewFlow
            ? descriptor.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.SessionNew ? reviewMcp : null
            : descriptor.SupportsMcpServers ? ctx.McpServers : null;

        try {
            await runtime.StartAsync(
                ctx.Worktree.Path,
                ctx.Prompt,
                ct,
                ResolveRequestedModel(descriptor, config, ctx),
                mcpServers
            ).ConfigureAwait(false);
        } catch {
            // The runtime owns both the connection and the process; dispose on a failed handshake
            // so a half-started child process is never leaked.
            await runtime.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        // The runtime IS the transcript source (it implements
        // IAcpTranscriptSource directly) — hand it back on HostedRuntimeStart so the orchestrator can
        // bind + forward without downcasting Runtime.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>
    /// Fail-closed validation + build of the review-flow MCP list, run as the FIRST thing in
    /// <see cref="StartAsync"/> — before <c>_connectionSource</c> can spawn a child. Returns
    /// <see langword="null"/> for a non-review launch; for a review flow it throws unless the launch
    /// is safe to run unattended AND has a deliverable result channel AND every allowlist entry is an
    /// auto-approvable read-only server, then returns the built list. Work-location safety is
    /// descriptor-gated: most vendors require a daemon-owned worktree, while a borrowed-review
    /// vendor must provide its own capability clamp. Neither location is itself a filesystem sandbox.
    /// </summary>
    static IReadOnlyList<AcpMcpServerSpec>? ValidateAndBuildReviewFlowMcp(
            RuntimeStartContext ctx, AcpVendorDescriptor descriptor, ResolvedBorrowedReviewPolicy policy) {
        if (!ctx.IsReviewFlow) return null;

        if (!descriptor.SupportsUnattended)
            throw new InvalidOperationException(
                $"Vendor '{descriptor.Vendor}' cannot host an unattended (review-flow) agent.");

        if (ctx.Work != WorkLocation.OwnedWorktree && !policy.Supported)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires an owned worktree, not a borrowed cwd.");
        if (ctx.Work != WorkLocation.OwnedWorktree &&
            policy.Containment == AcpBorrowedReviewContainment.IndependentSnapshot)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires daemon snapshot materialization before spawn.");

        if (descriptor.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.Unsupported)
            throw new InvalidOperationException(
                $"Vendor '{descriptor.Vendor}' cannot host a review-flow reviewer: no supported MCP transport for the kcap-flow-result channel.");

        // A blank agent id would still yield a non-empty server list and slip past a count-only guard,
        // so all three result-channel inputs are checked (a dead channel wedges the round).
        if (string.IsNullOrWhiteSpace(ctx.ServerUrl) || string.IsNullOrWhiteSpace(ctx.CapacitorPath) || string.IsNullOrWhiteSpace(ctx.AgentId))
            throw new InvalidOperationException(
                "Review-flow launch cannot inject the kcap-flow-result channel (missing server url / kcap path / agent id).");

        // The injected MCP set is the reviewer's integration boundary: resolve the allowlist through
        // the SAME authoritative read-only reviewer policy the
        // orchestrator applies to Codex (TryResolveReviewFlowAllowlist — reserved result channel is a
        // no-op, unknown/flow-starting/non-auto-approvable write servers fail the launch fast).
        if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(ctx.McpAllowlist, out var allowlistServerIds, out var rejected))
            throw new InvalidOperationException(
                $"Review-flow reviewer MCP allowlist contains a server that is not auto-approvable: '{rejected}'.");

        return AcpReviewFlowMcp.Build(ctx, allowlistServerIds);
    }

    /// <summary>
    /// Merges the per-launch model override with the daemon-wide default —
    /// <paramref name="ctx"/>'s own <c>Model</c> takes precedence when the launch specifies one,
    /// else falls back to <paramref name="descriptor"/>'s <c>ResolveDefaultModel</c>. Mirrors the
    /// existing <c>"default"</c>-sentinel convention <c>CodexLauncher.AddModelArg</c> already uses
    /// for "no override requested" (the UI dispatches the literal string <c>"default"</c>, not an
    /// empty string, when the user hasn't picked a model). The merged value is still a bare family
    /// prefix or an exact <c>modelId</c> — final resolution against the session's
    /// <c>availableModels</c> happens in <see cref="AcpHostedAgentRuntime"/> via
    /// <see cref="Capacitor.Cli.Core.Acp.AcpModelResolver"/>.
    /// </summary>
    static string? ResolveRequestedModel(AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : descriptor.ResolveDefaultModel(config);

    /// <summary>
    /// PURE builder for a real launch's spawn shape — no process side effects. StartRealProcess is
    /// the only production caller; AcpHostedAgentRuntimeFactoryTests calls this directly (Test plan
    /// items 1, 5) to assert on binary path, argv, cwd, and env without a connectionSource
    /// override, which bypasses process-spawning entirely and so could never prove this method's
    /// own correctness.
    /// </summary>
    /// <param name="policy">Test seam ONLY. Production always passes null, which resolves this
    /// machine's platform entry via <see cref="PolicyFor"/> — the same value the advertised capability
    /// is computed from, so argv and advertisement cannot disagree. Tests pass an explicit entry so
    /// the borrowed-snapshot argv is assertable on a platform whose own entry is unsupported.</param>
    internal static ProcessStartInfo BuildProcessStartInfo(
            AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx,
            ResolvedBorrowedReviewPolicy? policy = null) {
        var resolved = policy ?? PolicyFor(descriptor);
        // Defense-in-depth: the orchestrator's UnattendedLaunchPolicy is expected to reject a
        // review-flow launch for a vendor that doesn't support it before this factory ever runs,
        // but the factory doesn't rely on that alone — it refuses to build review-flow argv for an
        // unsupported vendor rather than trusting an external caller always applied the gate.
        if (ctx.IsReviewFlow && !descriptor.SupportsUnattended)
            throw new InvalidOperationException(
                $"Vendor '{descriptor.Vendor}' does not support unattended (review-flow) launches.");

        // Defense-in-depth for the trust-at-spawn argv appended just below: a borrowed-cwd reviewer
        // would run in the requester's live checkout, so this refuses it here too. StartAsync's
        // pre-spawn validation is the primary gate; this backstops the default spawn path (a
        // non-default connectionSource never reaches this builder).
        if (ctx.IsReviewFlow && ctx.Work != WorkLocation.OwnedWorktree && !resolved.Supported)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires an owned worktree, not a borrowed cwd.");
        if (ctx.IsReviewFlow && ctx.Work != WorkLocation.OwnedWorktree &&
            resolved.Containment == AcpBorrowedReviewContainment.IndependentSnapshot)
            throw new InvalidOperationException(
                $"Unattended review-flow launch for '{descriptor.Vendor}' requires daemon snapshot materialization before spawn.");

        var argv = new List<string>(descriptor.Argv);

        if (ctx.IsReviewFlow) {
            argv.AddRange(descriptor.UnattendedTrustArgv);

            if (descriptor.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.CopilotAdditionalConfig) {
                var reviewMcp = ValidateAndBuildReviewFlowMcp(ctx, descriptor, resolved)!;
                argv.Add("--additional-mcp-config");
                argv.Add(BuildCopilotAdditionalMcpConfig(reviewMcp));

                // The allowlist stays EXCLUSIVE. A borrowed-snapshot launch widens it with the
                // policy's verified read tools so the reviewer can actually read the snapshot; every
                // other launch (owned worktree, context-only) keeps the flow-result-only clamp, which
                // is what makes the server's read-blind rejection correct rather than paranoid.
                var extraToolIds = ctx.IsBorrowedSnapshot ? resolved.ExtraBorrowedToolIds : [];

                foreach (var toolId in CopilotAvailableToolIds(reviewMcp).Concat(extraToolIds))
                    argv.Add($"--available-tools={toolId}");
            }
        }

        // Every vendor — borrowed-snapshot review included — spawns the ordinary configured binary.
        // Resolving a borrowed reviewer through an exact-build record instead made a vendor
        // auto-update hard-fail the launch. See
        // docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.
        var binaryPath = descriptor.ResolveBinaryPath(config);
        var psi = new ProcessStartInfo(binaryPath, argv) {
            WorkingDirectory       = ctx.Worktree.Path,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        if (!string.IsNullOrEmpty(ctx.ServerUrl))
            psi.Environment["KCAP_URL"] = ctx.ServerUrl;

        return psi;
    }

    /// <summary>Pre-spawn check that the descriptor's DECLARED containment agrees with the launch
    /// context it was handed — a code-level invariant (a snapshot-materialized launch reaching a
    /// vendor that does not declare independent-snapshot containment is a wiring bug). It is
    /// deliberately NOT a build-identity check: capability is advertised for whatever build is
    /// installed, so nothing here may consult a version record. See
    /// docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.</summary>
    internal static void ValidateBorrowedArtifact(RuntimeStartContext ctx, ResolvedBorrowedReviewPolicy policy) {
        if (!ctx.IsReviewFlow || !ctx.IsBorrowedSnapshot) return;
        if (policy.Containment != AcpBorrowedReviewContainment.IndependentSnapshot)
            throw new InvalidOperationException("borrowed_snapshot_containment_mismatch");
    }

    /// <summary>Builds Copilot CLI's process-level stdio MCP config. Copilot's ACP capability
    /// advertises only HTTP/SSE for <c>session/new</c>, but the CLI accepts stdio servers in this
    /// alternate config shape before the ACP session starts.</summary>
    static string BuildCopilotAdditionalMcpConfig(IReadOnlyList<AcpMcpServerSpec> servers) {
        var mcpServers = new JsonObject();

        foreach (var server in servers) {
            var args = new JsonArray();
            foreach (var arg in server.Args) args.Add(AotSafeJsonString(arg));

            var env = new JsonObject();
            foreach (var item in server.Env) env[item.Name] = AotSafeJsonString(item.Value);

            mcpServers[server.Name] = new JsonObject {
                ["type"]    = AotSafeJsonString("stdio"),
                ["command"] = AotSafeJsonString(server.Command),
                ["args"]    = args,
                ["env"]     = env
            };
        }

        return new JsonObject { ["mcpServers"] = mcpServers }.ToJsonString();
    }

    /// <summary>NativeAOT has no reflection metadata for JsonValue.Create&lt;string&gt;, which is
    /// reached by JsonObject/JsonArray string assignment even though that code works under JIT.
    /// Parse a correctly escaped JSON string fragment so the published daemon stays on the
    /// reflection-free JsonNode path.</summary>
    static JsonNode AotSafeJsonString(string value) =>
        JsonNode.Parse($"\"{JsonEncodedText.Encode(value)}\"")!;

    /// <summary>Copilot's availability filter uses flattened runtime ids (<c>server-tool</c>),
    /// not its permission-pattern syntax (<c>server(tool)</c>). Keep both flow-channel tools plus only the
    /// reviewed-safe tools belonging to the already-validated server list.</summary>
    static IEnumerable<string> CopilotAvailableToolIds(IReadOnlyList<AcpMcpServerSpec> servers) {
        foreach (var server in servers) {
            if (string.Equals(server.Name, KcapMcpRegistry.ReservedResultChannelId, StringComparison.Ordinal)) {
                yield return $"{server.Name}-submit_review_result";
                yield return $"{server.Name}-send_flow_message";
                continue;
            }

            if (!KcapMcpRegistry.ReviewFlowUnattendedSafeTools.TryGetValue(server.Name, out var tools))
                continue;

            foreach (var tool in tools.Order(StringComparer.Ordinal))
                yield return $"{server.Name}-{tool}";
        }
    }

    /// <summary>
    /// The REAL, production process-spawning path — unchanged in behavior from this factory's
    /// prior Cursor-only shape, just descriptor-parameterized and delegating its argv/ProcessStartInfo
    /// construction to the pure <see cref="BuildProcessStartInfo"/> (spec-review Finding 4). Spawns
    /// the descriptor's binary + argv and returns its stdio streams plus an
    /// <see cref="AcpChildProcess"/> lifecycle wrapper.
    /// </summary>
    static (Stream Input, Stream Output, IAcpProcess Process) StartRealProcess(
            AcpVendorDescriptor descriptor, DaemonConfig config, RuntimeStartContext ctx, ILoggerFactory loggerFactory) {
        var psi = BuildProcessStartInfo(descriptor, config, ctx);

        var process = Process.Start(psi)
         ?? throw new InvalidOperationException($"Failed to start '{psi.FileName} {string.Join(' ', psi.ArgumentList)}' (Process.Start returned null).");

        var processLogger = loggerFactory.CreateLogger<AcpChildProcess>();
        var acpProcess    = new AcpChildProcess(process, processLogger, config.DebugFrames, descriptor.Vendor);

        return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, acpProcess);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP hosted agent launch: agentId={AgentId} vendor={Vendor} cwd={Cwd}")]
    partial void LogLaunching(string agentId, string vendor, string cwd);
}
