namespace Capacitor.Cli.Core;

/// <summary>What a process should do when it meets a server URL it cannot use.</summary>
public enum UrlFailurePolicy {
    /// <summary>Print an actionable hint and exit 2. Correct for interactive commands.</summary>
    FailFast,

    /// <summary>Raise <see cref="UnusableServerUrlException"/>. Correct for agent-spawned commands.</summary>
    Throw,
}

/// <summary>
/// Process-wide selector for <see cref="UrlFailurePolicy"/>, set once at entry.
///
/// <para>Defaults to <see cref="UrlFailurePolicy.FailFast"/> so interactive commands keep exiting 2
/// with an actionable hint — the right UX when a user is present. Agent-spawned commands switch to
/// <see cref="UrlFailurePolicy.Throw"/> because they owe an output contract (a hook whose host blocks
/// on stdout) or must leave no orphaned child, and <c>Environment.Exit</c> is uncatchable: it bypasses
/// the fail-open <c>catch</c> every vendor hook already has, so the harness sees no output at all and
/// rejects the session.</para>
///
/// <para>This selector prevents process <em>death</em>. It does not decide disposition — whether a
/// payload is spooled, a watcher is spawned, or a protocol error is returned is owned by the explicit
/// <see cref="HookHttp.IsPostable"/> guards at each seam. Nor is it a claim that the reachable surface
/// has been enumerated; it has not been, five times over.</para>
/// </summary>
public static class ProcessUrlPolicy {
    public static UrlFailurePolicy Current { get; set; } = UrlFailurePolicy.FailFast;
}

/// <summary>
/// Raised instead of <c>Environment.Exit(2)</c> under <see cref="UrlFailurePolicy.Throw"/>.
///
/// <para>Deliberately a distinct type so an audit can find every <c>catch</c> that must re-throw it
/// rather than swallow it and continue as though the server were reachable. A bare <c>catch</c> whose
/// fallback branch writes output or persists state would turn a loud failure into a silent wrong
/// one.</para>
/// </summary>
public sealed class UnusableServerUrlException(string hint) : Exception(hint);
