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
/// <para>Agent-spawned commands need <see cref="UrlFailurePolicy.Throw"/> because
/// <c>Environment.Exit</c> is uncatchable: it bypasses the fail-open <c>catch</c> every vendor hook
/// has, so a hook dies before its stdout contract and the harness rejects the session.</para>
///
/// <para>This prevents process death only. Disposition — spool, skip, or protocol error — belongs to
/// the <see cref="HookHttp.IsPostable"/> guards at each seam.</para>
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
