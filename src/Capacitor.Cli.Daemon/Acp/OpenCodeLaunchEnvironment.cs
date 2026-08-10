using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The environment every daemon-spawned <c>opencode acp</c> child gets, and why.
///
/// <para>OpenCode's launch controls are ENV-shaped, not argv-shaped, which is why this exists as its
/// own unit rather than as an <c>AcpVendorDescriptor.UnattendedTrustArgv</c> entry: there is no argv
/// for any of it. <c>opencode acp</c>'s own option list carries neither <c>--auto</c> nor the config
/// levers — the global flags are not accepted by the subcommand.</para>
/// </summary>
internal static class OpenCodeLaunchEnvironment {
    /// <summary>Disables OpenCode's EXTERNAL plugins — which is to say, kcap's own.</summary>
    internal const string PureVariable = "OPENCODE_PURE";

    /// <summary>Replaces the global config root, so the operator's own <c>mcp</c> servers are absent.</summary>
    internal const string ConfigDirVariable = "OPENCODE_CONFIG_DIR";

    /// <summary>Suppresses project-scoped config discovery AND the repo instruction-file scan.</summary>
    internal const string ProjectConfigVariable = "OPENCODE_DISABLE_PROJECT_CONFIG";

    /// <summary>The permission table — this vendor's trust vector.</summary>
    internal const string PermissionVariable = "OPENCODE_PERMISSION";

    /// <summary>
    /// Applies the settings that every hosted OpenCode launch needs, interactive included.
    ///
    /// <para><b>Why <c>OPENCODE_PURE=1</c> is not optional.</b> OpenCode is the one vendor where two
    /// kcap capture paths can be live at once: an operator who runs OpenCode normally has kcap's
    /// live-ingest plugin installed at <c>~/.config/opencode/plugins/kcap.ts</c>, and that plugin
    /// loads inside the <c>opencode acp</c> process too. Its <c>session.created</c> handler then
    /// starts a SECOND, top-level capture — a lifecycle POST plus a spawned watcher — for the very
    /// session the ACP mapper is already recording. Measured with a controlled pair
    /// (<c>docs/probes/2026-08-07-opencode-acp/</c> §4): identical 10s dwell, plugin confirmed
    /// installed, default env created <c>~/.cache/kcap/opencode/&lt;sessionId&gt;.jsonl</c> and
    /// <c>OPENCODE_PURE=1</c> did not.</para>
    ///
    /// <para><b>The precedence this pins:</b> for a daemon-hosted session the ACP mapper is the ONLY
    /// capture path. The plugin keeps its whole job for sessions the user starts themselves — this
    /// suppresses it only inside a child the daemon owns and is already recording.</para>
    ///
    /// <para><b>Accepted cost, stated rather than discovered later:</b> <c>OPENCODE_PURE</c> disables
    /// every external plugin, not just kcap's, so a hosted OpenCode agent does not carry the
    /// operator's other plugins. That diverges from the user's own session, which interactive hosting
    /// otherwise tries to reproduce (compare the Gemini descriptor's note on not pinning
    /// <c>--approval-mode</c>). It is accepted because the alternative is a session that is
    /// double-ingested — and TIMING-DEPENDENTLY so, since whether the plugin wins the race varies
    /// run to run, which is worse to operate than a deterministic duplicate. A narrower fix (the
    /// plugin recognising a daemon-owned launch and standing down) is deliberately NOT layered on
    /// top: with the plugin never loaded that code would be unreachable in production, and an
    /// unreachable guard reads as protection while proving nothing.</para>
    /// </summary>
    internal static void Apply(IDictionary<string, string?> environment) {
        environment[PureVariable] = "1";
    }

    /// <summary>
    /// The three settings that additionally make a launch an UNATTENDED REVIEWER, on top of
    /// <see cref="Apply"/>. Review launches only: every one of them would be wrong on an interactive
    /// hosted session, which must behave as the user's own does.
    ///
    /// <para><b><c>OPENCODE_CONFIG_DIR</c> — the recursion guard.</b> OpenCode reads the operator's
    /// global <c>mcp</c> servers into every session, the flows server among them, which would let a
    /// reviewer start nested review flows. An empty per-launch directory removes that whole source while
    /// the result channel injected through <c>session/new.mcpServers</c> still starts. See
    /// <see cref="OpenCodeReviewerConfigDir"/> for why it is per-launch and verified empty.</para>
    ///
    /// <para><b><c>OPENCODE_DISABLE_PROJECT_CONFIG=1</c> — the reviewed BRANCH is not trusted input.</b>
    /// Without it, a contributor-authored <c>opencode.json</c> / <c>.opencode/</c> in the branch under
    /// review is honoured, and the repo's own <c>AGENTS.md</c>/<c>CLAUDE.md</c> is folded into the
    /// reviewer's instructions. Both are channels from the artifact being reviewed into the reviewer
    /// judging it — the shape measured on the Gemini path, where a repository-authored MCP server was
    /// observed starting as the daemon user. It costs the reviewer the repo's legitimate guidance
    /// documents, which is accepted: a review is exactly the situation where the repository's own
    /// instructions are the least trustworthy input available.</para>
    ///
    /// <para><b><c>OPENCODE_PERMISSION</c> — the trust vector.</b> Deny-all plus the read family plus the
    /// injected servers' flattened tool names; see <see cref="OpenCodeReviewerPermissions"/>, including
    /// why the injected-server entry is load-bearing rather than belt-and-braces. Note this variable is
    /// merged OVER any configuration, so it holds even against an operator config saying
    /// <c>"*": "ask"</c> — measured with a positive control, and the reason the lever is not decorative
    /// despite OpenCode's own default already being permissive.</para>
    /// </summary>
    internal static void ApplyReviewer(
            IDictionary<string, string?> environment,
            string configDir,
            IReadOnlyList<AcpMcpServerSpec> injected) {
        environment[ConfigDirVariable]     = configDir;
        environment[ProjectConfigVariable] = "1";
        environment[PermissionVariable]    = OpenCodeReviewerPermissions.Build(injected);
    }
}
