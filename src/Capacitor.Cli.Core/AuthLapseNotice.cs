namespace Capacitor.Cli.Core;

/// <summary>
/// The user-facing "your credential needs attention" wording, in one place so the pre-flight
/// nudge (the token store already knows the credential is dead) and the server-rejection nudge
/// (the store thought it was fine; the server said 401) cannot drift apart.
///
/// <para>Pure text, like <see cref="VersionNudgeEmitter"/>: no I/O, no <c>Console</c>, and no
/// vendor envelope. Claude wraps <see cref="Rejected"/> in a <c>systemMessage</c> JSON object;
/// vendors whose stdout is a handshake contract use <see cref="VendorStderrLine"/> instead.</para>
/// </summary>
public static class AuthLapseNotice {
    /// <summary>A token is stored but expired and could not be refreshed.</summary>
    public const string Expired =
        "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume.";

    /// <summary>No usable token: never logged in, or the token belongs to another server.</summary>
    public const string NotAuthenticated =
        "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording.";

    /// <summary>
    /// The store handed over a locally-valid token and the server rejected it anyway — a revoked
    /// session or an org mismatch. Names the status because the raw <c>HTTP 401</c> is what a user
    /// searching their transcript or an issue tracker will have seen.
    /// </summary>
    public const string Rejected =
        "[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap login' to resume.";

    /// <summary>
    /// The stderr status line for vendors with no user-facing stdout channel, for ANY response
    /// <paramref name="code"/> — not just 401, so the two call sites (<c>AgentHookPoster.PostAsync</c>
    /// and <c>PostOrSpoolAsync</c>) can't drift apart on the choice between them. A 401 keeps the
    /// existing <c>[kcap] {tag} {endpoint}: HTTP 401</c> prefix — it is what the vendors' debug logs
    /// and existing issue reports show — and appends the recovery step; every other code stays the
    /// bare <c>[kcap] {tag} {endpoint}: HTTP {code}</c> line unchanged.
    /// </summary>
    public static string VendorStderrLine(string agentTag, string endpoint, int code) =>
        code == 401
            ? $"[kcap] {agentTag} {endpoint}: HTTP 401 — the server rejected your credentials; run 'kcap login' to resume recording"
            : $"[kcap] {agentTag} {endpoint}: HTTP {code}";
}
