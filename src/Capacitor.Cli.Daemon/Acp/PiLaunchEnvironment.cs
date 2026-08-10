namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The environment every daemon-spawned <c>pi --mode rpc</c> child gets.
///
/// <para><b>Why this exists.</b> Pi has no shell hooks, so kcap's live-ingest capture for a user's
/// OWN interactive Pi sessions works by installing a TypeScript extension
/// (<c>~/.pi/agent/extensions/kcap.ts</c> — see <c>PiExtensionInstaller</c>) that Pi auto-discovers
/// and loads in-process for EVERY <c>pi</c> invocation on the machine, hosted or not. A daemon-hosted
/// Pi child is spawned under that same operator <c>HOME</c>, so without a way to tell the extension
/// to stand down, its <c>session_start</c>/<c>session_shutdown</c> handlers would start a SECOND,
/// independent capture (a lifecycle POST plus a spawned watcher) for the very session this runtime is
/// already recording over the RPC wire — the same dual-capture failure mode
/// <c>OpenCodeLaunchEnvironment</c> documents at length for OpenCode's own global plugin.</para>
///
/// <para><b>The precedence this pins:</b> for a daemon-hosted session, the runtime's own RPC
/// transcript (<c>PiRpcHostedAgentRuntime</c>) is the ONLY capture path. The extension keeps its whole
/// job for every session the user starts themselves — this suppresses it only inside a child the
/// daemon owns and is already recording.</para>
///
/// <para>Unconditional for EVERY hosted launch (there is no reviewer-only carve-out here, unlike
/// <c>OpenCodeLaunchEnvironment.ApplyReviewer</c>'s three settings): an interactive hosted Pi agent is
/// double-captured exactly as an unattended one would be, so there is no launch shape this should ever
/// be omitted from.</para>
/// </summary>
internal static class PiLaunchEnvironment {
    /// <summary>Read by the kcap.ts extension (see <c>PiExtensionInstaller.ExtensionContent</c>) at
    /// the top of its exported function — when set to <c>"1"</c> the extension returns immediately
    /// and registers no handlers at all.</summary>
    internal const string PureVariable = "KCAP_PI_PURE";

    /// <summary>Applies the one setting every hosted Pi launch needs.</summary>
    internal static void Apply(IDictionary<string, string?> environment) {
        environment[PureVariable] = "1";
    }
}
