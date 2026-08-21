using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user systemd unit (one file per id).</summary>
static class SystemdUnit {
    const string Prefix = "kcap-daemon-";

    public static string UnitName(string id) => $"{Prefix}{id}.service";

    public static string UserUnitDir() =>
        Path.Combine(PathHelpers.HomeDirectory, ".config", "systemd", "user");

    public static string UnitPath(string id) => Path.Combine(UserUnitDir(), UnitName(id));

    public static string Unit(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("[Unit]\n");
        sb.Append($"Description=kcap daemon ({ServiceText.SystemdValue(spec.ServiceId)})\n");
        sb.Append("After=network-online.target\n");
        sb.Append("Wants=network-online.target\n\n");

        sb.Append("[Service]\n");
        foreach (var (k, v) in spec.Environment) {
            ServiceText.RequireValidEnvName(k);
            ServiceText.RequireNoControlCharacters(k, v);
            sb.Append($"Environment={EnvAssignment(k, ServiceText.SystemdValue(v))}\n");
        }

        var parts = new[] { spec.DaemonBinaryPath, "--name", spec.ServiceId, "--log-file", spec.LogPath }
            .Concat(spec.ExtraArgs)
            .Select(QuoteArg);
        sb.Append($"ExecStart={string.Join(' ', parts)}\n");
        sb.Append("Restart=on-failure\n");
        sb.Append("RestartSec=5\n");
        sb.Append("StartLimitIntervalSec=60\n");
        sb.Append("StartLimitBurst=5\n\n");

        sb.Append("[Install]\n");
        sb.Append("WantedBy=default.target\n");
        return sb.ToString();
    }

    public static string? IdFromUnitFileName(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal) && fileName.EndsWith(".service", StringComparison.Ordinal)
            ? fileName[Prefix.Length..^".service".Length]
            : null;

    // ── command vectors ──
    public static string[] DaemonReloadArgs()       => ["--user", "daemon-reload"];
    public static string[] EnableArgs(string id)    => ["--user", "enable", UnitName(id)];
    public static string[] DisableNowArgs(string id)=> ["--user", "disable", "--now", UnitName(id)];
    public static string[] StartArgs(string id)     => ["--user", "start", UnitName(id)];
    public static string[] RestartArgs(string id)   => ["--user", "restart", UnitName(id)];
    public static string[] StopArgs(string id)      => ["--user", "stop", UnitName(id)];
    public static string[] IsActiveArgs(string id)  => ["--user", "is-active", UnitName(id)];
    public static string[] IsEnabledArgs(string id) => ["--user", "is-enabled", UnitName(id)];

    public static ServiceState StatusFrom(string activeOut, int enabledExit) {
        if (activeOut.Trim().Equals("active", StringComparison.OrdinalIgnoreCase)) return ServiceState.Running;
        return enabledExit == 0 ? ServiceState.Installed : ServiceState.NotInstalled;
    }

    /// <summary>
    /// The daemon binary from a rendered unit's <c>ExecStart=</c> — quote-aware,
    /// since <see cref="Unit"/> may emit <c>ExecStart="/opt/k cap/kcap-daemon" …</c>
    /// for a path with spaces. Used by <c>daemon doctor</c>.
    /// </summary>
    public static string? BinaryFromUnit(string unitText) {
        var line = unitText.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal));
        return line is null ? null : FirstToken(line["ExecStart=".Length..]);
    }

    /// <summary>
    /// First whitespace-delimited token, honoring a leading double-quoted segment — reverses both
    /// <see cref="Esc"/> and the expansion-escaping <see cref="EscapeExpansions"/> applies.
    ///
    /// <para>Undoubling is unambiguous: every literal <c>%</c> and <c>$</c> was written doubled, so an odd
    /// run cannot occur in output this code produced.</para>
    /// </summary>
    static string? FirstToken(string s) {
        if (s.Length == 0) return null;
        if (s[0] != '"') {
            var sp = s.IndexOf(' ');
            return UnescapeExpansions(sp < 0 ? s : s[..sp]);
        }

        var sb = new StringBuilder();
        for (var i = 1; i < s.Length; i++) {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; } // \\ -> \ , \" -> "
            if (s[i] == '"') break;
            sb.Append(s[i]);
        }
        return UnescapeExpansions(sb.ToString());
    }

    static string UnescapeExpansions(string s) => s.Replace("%%", "%").Replace("$$", "$");

    // ── systemd value/argument quoting ──
    // systemd splits Environment= and ExecStart on unquoted whitespace, so any
    // value/path with a space must be double-quoted (with \ and " escaped).
    // Both quote characters are structural to systemd's word lexer, not just the double quote: an unpaired
    // apostrophe in a bare token opens a single-quoted string that never closes, which makes the unit
    // unloadable, and a paired one is stripped, which silently changes the value. `O'Reilly` in a home
    // directory is the ordinary case. Inside the double quotes this selects, an apostrophe is literal, so
    // widening the predicate is the whole fix — Esc needs no new case.
    static bool NeedsQuote(string s) =>
        s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\');

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// An ExecStart argument, double-quoted only when it contains whitespace/quotes, with literal percent
    /// signs doubled.
    ///
    /// <para>specifier expansion applies to <c>ExecStart=</c> just as it does to the
    /// <c>Environment=</c> values <see cref="ServiceText.SystemdValue"/> handles, and neither the binary path
    /// nor the log path is sanitized (<c>ServiceId</c> is, so it cannot carry a percent). <c>%</c> is a legal
    /// filename character on Linux, so a home directory like <c>/home/50%off</c> would otherwise render an
    /// ExecStart systemd either rewrites or refuses to load — a daemon that cannot start.</para>
    ///
    /// <para><see cref="FirstToken"/> reverses this, so <c>daemon doctor</c> still recovers the real path.</para>
    /// </summary>
    static string QuoteArg(string a) {
        // No raw control character survives here, not only the line breaks. A newline is the worst case — the
        // line ends there and systemd reads the remainder as the next directive — but quoting does not make
        // U+0001, a backspace or a vertical tab valid unit syntax either, and this writer has no encoder for
        // them. Refusing is the same call the Windows wrapper makes, and `service install` is interactive so
        // the failure lands in front of a person who can fix it.
        ServiceText.RequireNoControlCharacters("an ExecStart value", a);

        // A standalone `;` is systemd's own command SEPARATOR in an ExecStart line: emitted bare, every
        // argument after it becomes a second command systemd runs. Its documented literal form is `\;`, but
        // rather than rely on this code's reading of that escape (unverifiable on the machines this is built
        // on) the token is refused outright. No legitimate daemon argument is a lone semicolon, so the
        // over-rejection is empty in practice — and unlike an escape, a refusal cannot be subtly wrong.
        if (a == ";")
            throw new InvalidOperationException(
                "Refusing to write a systemd unit: an ExecStart argument is a bare ';', which systemd treats "
              + "as a command separator — the arguments after it would run as a second command. Remove it, "
              + "then re-run `kcap daemon service install`.");

        var esc = EscapeExpansions(a);
        return NeedsQuote(esc) ? $"\"{Esc(esc)}\"" : esc;
    }

    /// <summary>
    /// Neutralise both expansions systemd performs on an <c>ExecStart=</c> command line: <c>%</c> specifiers
    /// and <c>$NAME</c>/<c>${NAME}</c> environment substitution. <c>%%</c> and <c>$$</c> are systemd's own
    /// escapes for one literal character, so this is a faithful round-trip.
    ///
    /// <para>Both are reachable: neither the binary path nor the log path is sanitized (<c>ServiceId</c> is),
    /// and <c>%</c> and <c>$</c> are legal Linux filename characters. Undoubled, a path containing
    /// <c>${HOME}</c> is replaced with the daemon's own environment and <c>%n</c> with the unit name — in the
    /// executable path, which makes the unit unloadable rather than merely wrong.</para>
    ///
    /// <para>Not applied to <c>Environment=</c> values: systemd expands specifiers there but NOT variables,
    /// so doubling <c>$</c> in a value would corrupt it. The two sinks differ, so they get different
    /// escapes — <see cref="ServiceText.SystemdValue"/> handles that one.</para>
    /// </summary>
    static string EscapeExpansions(string s) => s.Replace("%", "%%").Replace("$", "$$");

    /// <summary>An <c>Environment=</c> assignment; the whole <c>KEY=VALUE</c> is quoted when VALUE needs it.</summary>
    static string EnvAssignment(string key, string value) =>
        NeedsQuote(value) ? $"\"{key}={Esc(value)}\"" : $"{key}={value}";
}
