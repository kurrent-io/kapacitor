// src/Capacitor.Cli.Daemon/Services/PiRpcHostedAgentRuntimeFactory.cs
using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for Pi's CLI (<c>pi --mode rpc</c>) as an interactive
/// hosted agent over the LONG-LIVED <see cref="PiRpcHostedAgentRuntime"/>.
///
/// <para><b>PR-1 scope: interactive hosting only.</b> <see cref="SupportsUnattended"/> is
/// unconditionally <see langword="false"/> — the reviewer lane is a later PR. Pi never claimed
/// unattended support at all, so the default <see cref="IHostedAgentRuntimeFactory.DescribeUnattendedSupport"/>
/// (a null <see cref="UnattendedSupport.WithheldReason"/>) is correct, and this factory never needs
/// to spawn <c>pi</c> to answer either question.</para>
///
/// <para><b>Not an ACP factory.</b> Pi speaks its own LF-framed JSONL-RPC over stdio for ONE
/// long-lived child that backs the whole hosted session (see <see cref="IPiRpcProcess"/>'s class doc
/// for the contrast with Antigravity's exec-per-turn shape), so this factory owns its own PSI builder
/// and process seam rather than reusing <see cref="AcpHostedAgentRuntimeFactory"/>.</para>
///
/// <para><b>The dual-capture gate.</b> Every launch this factory serves carries
/// <c>KCAP_PI_PURE=1</c> via <see cref="PiLaunchEnvironment.Apply"/> — see its class doc for why an
/// unconditional gate (not a reviewer-only one) is required: kcap's global Pi extension
/// (<c>~/.pi/agent/extensions/kcap.ts</c>) loads inside every <c>pi</c> process on the machine,
/// hosted or not, and would otherwise start a SECOND capture of a session this runtime already
/// records over the RPC wire.</para>
///
/// <para><b>No isolated <c>HOME</c>, unlike Antigravity/Kiro/Copilot.</b> Dual capture here is closed
/// by the PURE env var alone, so this launch inherits the daemon's own <c>HOME</c> — there is no
/// per-launch state directory to create or sweep.</para>
///
/// <para><b>Refusal ordering mirrors <see cref="AntigravityHostedAgentRuntimeFactory.StartAsync"/>:</b>
/// a PR review (<c>ctx.IsReview</c>) first, since no config or consent could make that shape work;
/// then the review-flow refusal (PR-1 has no reviewer at all); then the borrowed-worktree refusal —
/// this runtime has no containment strategy for a workspace it does not own.</para>
/// </summary>
/// <param name="processSource">Test seam ONLY. Production passes null, which spawns the real
/// <see cref="PiRpcProcess"/> from the <see cref="ProcessStartInfo"/> <see cref="BuildPsi"/> built —
/// so the seam changes nothing about production behaviour, and the argv/env assertions run against
/// the same builder a real launch uses.</param>
/// <param name="binaryExists">Test seam ONLY, for <see cref="IsAvailable"/>. Production passes null,
/// which resolves the real binary through <c>PATH</c> via <see cref="CliResolver.Exists"/>.</param>
/// <param name="readyDeadline">Test seam ONLY, threaded verbatim into every
/// <see cref="PiRpcHostedAgentRuntime"/> this factory constructs. Production passes null, which
/// falls through to <see cref="PiRpcHostedAgentRuntime.DefaultReadyDeadline"/> — so a test can bound
/// a silent-child launch to milliseconds instead of burning the real 30s default.</param>
internal sealed partial class PiRpcHostedAgentRuntimeFactory(
        DaemonConfig                                                 config,
        ILoggerFactory                                                loggerFactory,
        Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>>? processSource = null,
        Func<string, bool>?                                           binaryExists = null,
        TimeSpan?                                                     readyDeadline = null
    ) : IHostedAgentRuntimeFactory {
    readonly ILogger _logger = loggerFactory.CreateLogger<PiRpcHostedAgentRuntimeFactory>();

    readonly Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>> _processSource =
        processSource ?? ((psi, _) => Task.FromResult<IPiRpcProcess>(
            new PiRpcProcess(psi, loggerFactory.CreateLogger<PiRpcProcess>())));

    readonly Func<string, bool> _binaryExists = binaryExists ?? CliResolver.Exists;

    public string Vendor => "pi";

    public bool IsAvailable() => _binaryExists(config.PiPath);

    /// <summary>PR-1 only — the reviewer lane is not implemented yet. Pi never claimed unattended
    /// support in the first place, so the default <see cref="IHostedAgentRuntimeFactory.DescribeUnattendedSupport"/>
    /// (<c>new(SupportsUnattended, null)</c>) is correct as-is: <see cref="UnattendedSupport.WithheldReason"/>
    /// is reserved for a vendor this daemon's OWN configuration is refusing to offer, not for one that
    /// simply doesn't support it yet. An earlier revision overrode this with a non-null reason, which
    /// made every daemon with <c>pi</c> installed log a false "restart to enable" operator
    /// instruction at boot.</summary>
    public bool SupportsUnattended => false;

    /// <summary>Pi's model rides argv (<c>--model</c>), applied on every launch that resolves one —
    /// unlike a vendor whose model-selection hook is unverified.</summary>
    public bool SupportsModelSelection => true;

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        // FIRST, ahead of every other refusal: no install or config makes a PR review work here — this
        // runtime hosts interactive agents only, and a PR review needs the `kcap mcp review` tool
        // surface only the PTY launchers build.
        if (ctx.IsReview)
            throw new InvalidOperationException(
                "pi_pr_review_unsupported: this runtime hosts interactive agents only. A PR review "
              + "needs the `kcap mcp review` tool surface and review prompt, which only the PTY "
              + "launchers build — launch the PR review with Claude.");

        // PR-1 has no reviewer lane at all — SupportsUnattended is unconditionally false, so the
        // orchestrator's own gate should already have refused this before reaching a factory. This is
        // defence in depth for an explicit `vendor: "pi"` review-flow request that reaches a factory
        // without consulting advertisement.
        if (ctx.IsReviewFlow)
            throw new InvalidOperationException(
                "pi_reviewer_not_implemented: the Pi reviewer lane is not implemented yet — this "
              + "runtime hosts interactive agents only.");

        // A property of this runtime, not of reviews: there is no sandbox substrate here, so nothing
        // bounds what a launch could read out of a checkout it does not own.
        if (ctx.Work != WorkLocation.OwnedWorktree)
            throw new InvalidOperationException(
                "pi_requires_owned_worktree: this runtime has no containment strategy for a borrowed "
              + "workspace, so it runs only in a daemon-owned worktree.");

        var psi = BuildPsi(config, ctx);

        LogLaunching(ctx.AgentId, ctx.Worktree.Path);

        var process = await _processSource(psi, ct).ConfigureAwait(false);

        PiRpcHostedAgentRuntime runtime;

        try {
            runtime = new PiRpcHostedAgentRuntime(
                process,
                loggerFactory.CreateLogger<PiRpcHostedAgentRuntime>(),
                ctx.AgentId,
                ResolveModel(config, ctx),
                ctx.Worktree.Path,
                readyDeadline: readyDeadline);
        } catch {
            // Construction itself failing (it should not, in practice) still leaves a spawned child —
            // no orphan pi processes.
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // MUST precede the first input: a clock assigned later makes every stamp inside the launch a
        // silent no-op, and a liveness sweep would then judge this agent from an empty record.
        //
        // Deferred, not fixed: the runtime's constructor starts its read pump and handshake tasks
        // immediately (before this line runs), so a response that arrives between construction and
        // this assignment would stamp the clock's SetLaunchStage against a still-null ActivityClock —
        // the identical race AntigravityHostedAgentRuntimeFactory's own post-construction
        // `runtime.ActivityClock = ctx.ActivityClock;` assignment carries, unfixed, today. Threading
        // the clock through the runtime's constructor instead (set before RunPumpAsync/
        // RunHandshakeAsync start) would close it, but would also touch every direct-construction
        // test site (PiRpcRuntimeFakes.NewRuntime, PiRpcHostedAgentRuntimeTests) for a window that
        // requires the real child to answer get_state before this single assignment statement runs —
        // sub-millisecond in practice. Left as-is, matching the sibling factory's accepted risk;
        // revisit both together if this is ever observed rather than theorized.
        runtime.ActivityClock = ctx.ActivityClock;

        try {
            if (!string.IsNullOrEmpty(ctx.Prompt))
                await runtime.SendUserInputAsync(ctx.Prompt).ConfigureAwait(false);

            // The ordering guarantee: the orchestrator reads transcript.AcpSessionId synchronously the
            // moment this method returns, so the barrier must resolve BEFORE that — see
            // PiRpcHostedAgentRuntime's rule (a).
            await runtime.WaitForSessionReadyAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) {
            // The launch is over either way; leaving the child alive would leave an unreapable agent
            // holding a daemon slot. DisposeAsync terminates the child and joins the pump/handshake.
            await runtime.DisposeAsync().ConfigureAwait(false);

            // A genuine shutdown propagates AS a cancellation — do not dress it up as a launch failure.
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;

            // An unauthenticated or misconfigured pi should surface its real stderr rather than just
            // the generic handshake-failed message the runtime already throws — mirrors
            // AntigravityHostedAgentRuntimeFactory.DescribeLaunchFailure in spirit (this vendor has no
            // auth-specific cases to distinguish yet, so there is no case ladder, just the append).
            //
            // Bare `throw;` when there's nothing to add: it preserves ex's original stack trace,
            // where re-throwing ex itself (`throw ex;`) would reset it to here. Wrapping only
            // happens in the branch that actually adds information, and ex's own stack trace
            // survives intact as the wrapper's InnerException.
            if (DescribeLaunchFailure(ex, process.Diagnostics) is { } wrapped) throw wrapped;
            throw;
        }

        // The runtime IS the transcript source, so the orchestrator binds and forwards without
        // downcasting Runtime. McpConfigPath stays null: this launch writes no temp mcp-config file.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>The effective model — the launch's own override, else the daemon-wide default. Same
    /// <c>"default"</c>-sentinel convention as <see cref="AntigravityHostedAgentRuntimeFactory.ResolveModel"/>
    /// and every other vendor's resolver.</summary>
    internal static string? ResolveModel(DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : config.PiModel;

    /// <summary>
    /// PURE builder for the whole launch — no process, no filesystem side effects. The real spawn
    /// path and the launch tests both go through it, so an assertion here certifies the vector the OS
    /// actually receives.
    ///
    /// <para>Argv is exactly <c>--mode rpc</c> plus <c>--model &lt;m&gt;</c> when
    /// <see cref="ResolveModel"/> yields one. Env carries <see cref="PiLaunchEnvironment.Apply"/>'s
    /// <c>KCAP_PI_PURE=1</c> (never omitted — see that type's doc) plus the same daemon-identity
    /// stamps <see cref="AntigravityHostedAgentRuntimeFactory.BuildTurnPsi"/> carries:
    /// <c>KCAP_URL</c>, <c>KCAP_AGENT_ID</c>, <c>KCAP_DAEMON_ID</c>, <c>KCAP_DAEMON_EPOCH</c> — the
    /// last two are what makes a surviving child visible to <c>OrphanReaper</c>'s env-marker pass
    /// after a daemon restart, and are omitted (never stamped empty) when the context does not carry
    /// them, matching Antigravity's convention.</para>
    /// </summary>
    internal static ProcessStartInfo BuildPsi(DaemonConfig config, RuntimeStartContext ctx) {
        var argv = new List<string> { "--mode", "rpc" };

        if (ResolveModel(config, ctx) is { Length: > 0 } model) {
            argv.Add("--model");
            argv.Add(model);
        }

        // No-BOM UTF-8, matching AcpConnection's wire encoding: a BOM emitted before the first JSON
        // line would break pi's line parser, and the platform-default encoding these three
        // properties would otherwise fall back to is not guaranteed to be UTF-8 (nor BOM-free) on
        // every OS this daemon runs on.
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var psi = new ProcessStartInfo(config.PiPath, argv) {
            WorkingDirectory       = ctx.Worktree.Path,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = noBomUtf8,
            StandardOutputEncoding = noBomUtf8,
            StandardErrorEncoding  = noBomUtf8,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        // The dual-capture gate — unconditional, see PiLaunchEnvironment's doc.
        PiLaunchEnvironment.Apply(psi.Environment);

        if (!string.IsNullOrEmpty(ctx.ServerUrl)) psi.Environment["KCAP_URL"] = ctx.ServerUrl;

        psi.Environment["KCAP_AGENT_ID"] = ctx.AgentId;
        if (!string.IsNullOrEmpty(ctx.DaemonId))    psi.Environment["KCAP_DAEMON_ID"]    = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) psi.Environment["KCAP_DAEMON_EPOCH"] = ctx.DaemonEpoch;

        return psi;
    }

    /// <summary>
    /// Wraps <paramref name="cause"/> with its child's captured stderr appended, or
    /// <see langword="null"/> when there is none to add — in spirit with
    /// <c>AntigravityHostedAgentRuntimeFactory.DescribeLaunchFailure</c>, but without that method's
    /// auth/timeout case ladder: this vendor has no equivalent well-known failure shapes to
    /// distinguish yet, so there is nothing to branch on beyond "did the child say anything on the
    /// way out". <paramref name="cause"/> is preserved as <see cref="Exception.InnerException"/>,
    /// stack trace and all, when this returns non-null; the caller falls back to a bare
    /// <c>throw;</c> of <paramref name="cause"/> itself when this returns <see langword="null"/>, so
    /// that unadorned case never has its stack trace reset.
    /// </summary>
    static Exception? DescribeLaunchFailure(Exception cause, string? diagnostics) =>
        diagnostics is { Length: > 0 }
            ? new InvalidOperationException($"{cause.Message} (pi stderr: {diagnostics})", cause)
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Pi launch: agentId={AgentId} cwd={Cwd}")]
    partial void LogLaunching(string agentId, string cwd);
}
