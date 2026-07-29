namespace Capacitor.Cli;

/// <summary>
/// Answers "which session am I serving, and where is it working?" for a long-lived MCP server
/// process, from what the RUNNING harness told this process — not from whatever environment the
/// process happened to inherit.
///
/// <para>The distinction matters because an MCP stdio server is spawned once, at harness startup,
/// from the launching process's environment, and then serves every tool call for the rest of that
/// session:</para>
/// <list type="bullet">
///   <item><description><c>KCAP_SESSION_ID</c> is written by the session-start hook into the
///   harness's shell-env file, which the harness applies to its <b>shell</b> invocations. It is
///   therefore correct inside a session's own shell, but an MCP server never sees that file — it
///   only sees the value that was exported in whichever process launched the harness. When one
///   session launches another (a driver session started from a parent session's shell), the child
///   harness — and so every MCP server it spawns — inherits the <b>parent's</b>
///   <c>KCAP_SESSION_ID</c>, and nothing ever rewrites it. Requester context resolved from it then
///   names the wrong session for the whole life of the driver.</description></item>
///   <item><description>The process's current directory has the same failure mode whenever the MCP
///   registration does not pin a <c>cwd</c>: the server inherits the launching process's directory,
///   which for a session launched from another session's checkout is the <b>parent's</b> checkout,
///   not the driver's.</description></item>
/// </list>
///
/// <para>Claude Code exports <c>CLAUDE_CODE_SESSION_ID</c> and <c>CLAUDE_PROJECT_DIR</c> into every
/// process it spawns, setting both to <b>its own</b> session and project directory — so a child
/// harness overwrites any inherited values for its own children. That makes them per-process
/// evidence of the harness we are actually running under, which is exactly what an inherited
/// variable cannot be. When that evidence is present it wins; when it is absent (any other harness)
/// resolution falls back to the ambient behaviour unchanged, so the relative precedence of the
/// existing signals is untouched.</para>
///
/// <para>Nesting keeps that evidence honest in one direction only: a Claude Code session rewrites
/// these variables for its own children, but a DIFFERENT harness launched from inside a Claude Code
/// session (<c>codex exec</c> from a Bash tool call, say) inherits them and leaves them in place. So
/// a co-present signal from another harness makes ours unprovable, and resolution deliberately falls
/// back to the ambient behaviour rather than relocating the requester into the OUTER harness's
/// checkout — which would be the same class of bug this type exists to prevent. Falling back is
/// never worse than the behaviour that shipped before it.</para>
/// </summary>
static class HarnessRequesterContext {
    /// <summary>Set by the running Claude Code process in every child process's environment.</summary>
    internal const string ClaudeSessionIdVar = "CLAUDE_CODE_SESSION_ID";

    /// <summary>The running Claude Code session's project directory, set alongside the id above.</summary>
    internal const string ClaudeProjectDirVar = "CLAUDE_PROJECT_DIR";

    /// <summary>Another harness's own-session signal. Its presence alongside the Claude variables
    /// means one harness is nested inside the other and neither is provably ours.</summary>
    internal const string CodexThreadIdVar = "CODEX_THREAD_ID";

    /// <param name="SessionId">
    /// The requesting session, dash-stripped to the canonical form the server expects (matching
    /// the session-start hook's own normalization). Null when no harness signal is available.
    /// </param>
    /// <param name="ProjectDir">
    /// The running harness's project directory, or null when the harness did not report one (or
    /// reported one that no longer exists). Null means "no better answer than the process cwd".
    /// </param>
    internal readonly record struct Resolved(string? SessionId, string? ProjectDir);

    internal static Resolved Resolve() => Resolve(Environment.GetEnvironmentVariable, Directory.Exists);

    /// <summary>
    /// Env-injected overload so the precedence is unit-testable without mutating the process
    /// environment: pass a lookup that returns the values a real harness would export.
    /// </summary>
    internal static Resolved Resolve(Func<string, string?> getEnv, Func<string, bool> directoryExists) {
        var harnessSessionId = getEnv(ClaudeSessionIdVar);
        var nestedHarness    = getEnv(CodexThreadIdVar);

        // No usable per-process harness evidence — either none was reported, or another harness's
        // own-session signal is co-present so ours is unprovable (see the nesting note above). Keep
        // the pre-existing ambient resolution exactly as it was (KCAP_SESSION_ID, then
        // CODEX_THREAD_ID) and claim no project directory, so the caller keeps using the process cwd.
        if (string.IsNullOrWhiteSpace(harnessSessionId) || !string.IsNullOrWhiteSpace(nestedHarness))
            return new(ArgParsing.ResolveSessionIdFromEnv(getEnv), ProjectDir: null);

        // A project dir is only usable if it still resolves to a directory — a value pointing at a
        // removed worktree must degrade to the process cwd rather than send the server a path
        // nothing can be checked out at.
        var projectDir = getEnv(ClaudeProjectDirVar)?.Trim();
        var usableProjectDir = !string.IsNullOrEmpty(projectDir) && directoryExists(projectDir)
            ? projectDir
            : null;

        return new(harnessSessionId.Trim().Replace("-", ""), usableProjectDir);
    }
}
