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

    /// <summary>Binds the two environment variables that decide this daemon's Codex transport onto
    /// <paramref name="config"/>. Both are read from the environment and nowhere else — no profile or
    /// config-file binding — so the variable NAMES and the present-but-empty guard are part of the
    /// contract an operator relies on, not incidental. Takes the lookup as a delegate so that contract is
    /// testable without a process environment.
    ///
    /// <para>An absent or empty value leaves the existing setting alone rather than resetting it, matching
    /// every other env binding in the daemon's startup.</para></summary>
    public static void BindFromEnvironment(Capacitor.Cli.Daemon.DaemonConfig config, Func<string, string?> getEnv) {
        if (getEnv("KCAP_CODEX_TRANSPORT") is { Length: > 0 } transport)
            config.CodexTransport = transport;

        if (getEnv("KCAP_CODEX_APPSERVER_INTERACTIVE") is { Length: > 0 } interactive)
            config.CodexAppServerInteractive = IsInteractiveOptIn(interactive);
    }

    /// <summary>Resolves the effective transport: app-server only when selected AND the installed
    /// build meets <see cref="VersionFloor"/>. An unknown/unparseable version fails toward PTY.</summary>
    public static bool UsesAppServer(string? transport, string? cliVersion) =>
        IsAppServerSelected(transport) && MeetsFloor(cliVersion);

    /// <summary>Reads the per-daemon interactive opt-in from its environment value. Trimmed and
    /// case-insensitive, matching the daemon's other environment booleans
    /// (<c>DaemonRunner.ParseDebugFramesFlag</c>), over a superset of their vocabulary. Anything else —
    /// including "0", "false" and any typo — leaves it OFF. Off is the safe direction: a mistyped value
    /// keeps the daemon on the transport it already had rather than moving interactive hosting onto an
    /// operator who did not ask for it.</summary>
    public static bool IsInteractiveOptIn(string? value) =>
        value?.Trim() is { Length: > 0 } v
     && (v == "1"
      || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
      || string.Equals(v, "yes",  StringComparison.OrdinalIgnoreCase)
      || string.Equals(v, "on",   StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves the daemon-wide <c>CodexAppServerActive</c> at startup. The version probe is
    /// deferred behind the selection check so a PTY daemon (the default) never pays for it — which is
    /// why this owns the composition rather than the caller evaluating the probe eagerly.</summary>
    public static bool ResolveActive(string? transport, Func<string?> probeCliVersion) =>
        IsAppServerSelected(transport) && MeetsFloor(probeCliVersion());

    static bool IsAppServerSelected(string? transport) =>
        string.Equals(transport?.Trim(), AppServer, StringComparison.OrdinalIgnoreCase);

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

        // Accept ONLY a clean numeric release token `<digits>.<digits>[.<digits>]` (a `codex-cli`
        // prefix word is split off on whitespace and ignored). A prerelease or build-metadata tail
        // (`0.146.0-rc.1`, `0.146.0+meta`) or a non-numeric part makes that token non-clean, so it
        // is rejected and the caller fails toward PTY — this gate enables the containment-sensitive
        // transport, so an RC that is BELOW the verified release must never be normalized upward to it.
        foreach (var token in version.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            var t = token.TrimStart('v', 'V');
            var parts = t.Split('.');
            if (parts.Length is < 2 or > 3) continue;
            if (!AllDigits(parts[0]) || !AllDigits(parts[1])) continue;
            if (parts.Length == 3 && !AllDigits(parts[2])) continue;

            return (int.Parse(parts[0]), int.Parse(parts[1]), parts.Length == 3 ? int.Parse(parts[2]) : 0);
        }
        return null;
    }

    static bool AllDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
