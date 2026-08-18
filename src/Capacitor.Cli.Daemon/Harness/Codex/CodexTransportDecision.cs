namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// The ONE rule that decides whether this daemon hosts Codex reviewers over <c>codex app-server</c>
/// instead of the interactive PTY. Both the launch router
/// (<see cref="CodexHostedAgentRuntimeFactory"/>, via the resolved
/// <c>DaemonConfig.CodexAppServerActive</c>) and the certification advertisement
/// (<c>DaemonRunner</c>, which sets that same field) read this one function, so the advertised
/// launcher-policy version and the transport actually used can never diverge.
///
/// <para>App-server is used only when the operator selected it AND the installed Codex meets the
/// spike-pinned floor (0.146.0 — all behavioural probes ran on it). Below the floor with app-server
/// selected, the daemon falls back to PTY and advertises the PTY policy: one fact, computed once.</para>
/// </summary>
internal static class CodexTransportDecision {
    public const string AppServer  = "app-server";
    public const string Pty        = "pty";

    /// <summary>Spike-pinned minimum Codex version whose app-server method set + behavioural
    /// containment probes are verified. A lower build runs PTY even under an app-server
    /// selection.</summary>
    public const string VersionFloor = "0.146.0";

    /// <summary>Resolves the effective transport: app-server only when selected AND the installed
    /// build meets <see cref="VersionFloor"/>. An unknown/unparseable version fails toward PTY.</summary>
    public static bool UsesAppServer(string? transport, string? cliVersion) =>
        string.Equals(transport?.Trim(), AppServer, StringComparison.OrdinalIgnoreCase)
        && MeetsFloor(cliVersion);

    /// <summary>True when <paramref name="cliVersion"/> parses and is >= <see cref="VersionFloor"/>.
    /// Tolerant of surrounding text (e.g. <c>"codex-cli 0.146.0"</c>) — it extracts the first
    /// dotted numeric token.</summary>
    public static bool MeetsFloor(string? cliVersion) {
        var actual = ParseSemver(cliVersion);
        var floor  = ParseSemver(VersionFloor);
        if (actual is null || floor is null) return false;

        var (aMaj, aMin, aPat) = actual.Value;
        var (fMaj, fMin, fPat) = floor.Value;
        if (aMaj != fMaj) return aMaj > fMaj;
        if (aMin != fMin) return aMin > fMin;
        return aPat >= fPat;
    }

    static (int Major, int Minor, int Patch)? ParseSemver(string? version) {
        if (string.IsNullOrWhiteSpace(version)) return null;

        // Find the first token shaped `<digits>.<digits>[.<digits>]` so trailing/leading words
        // (a `codex-cli` prefix, a pre-release suffix) do not defeat the parse.
        foreach (var token in version.Split([' ', '\t', '\r', '\n', '-', '_'], StringSplitOptions.RemoveEmptyEntries)) {
            var t = token.TrimStart('v', 'V');
            var parts = t.Split('.');
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) continue;

            var patch = 0;
            if (parts.Length >= 3) {
                // Patch may carry a pre-release tail ("0+meta" / "0rc1"); take the leading digits.
                var digits = new string(parts[2].TakeWhile(char.IsDigit).ToArray());
                _ = int.TryParse(digits, out patch);
            }
            return (major, minor, patch);
        }
        return null;
    }
}
