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

    /// <summary>First whitespace-delimited token, honoring a leading double-quoted segment (reverses <see cref="Esc"/>).</summary>
    static string? FirstToken(string s) {
        if (s.Length == 0) return null;
        if (s[0] != '"') {
            var sp = s.IndexOf(' ');
            return sp < 0 ? s : s[..sp];
        }

        var sb = new StringBuilder();
        for (var i = 1; i < s.Length; i++) {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; } // \\ -> \ , \" -> "
            if (s[i] == '"') break;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ── systemd value/argument quoting ──
    // systemd splits Environment= and ExecStart on unquoted whitespace, so any
    // value/path with a space must be double-quoted (with \ and " escaped).
    static bool NeedsQuote(string s) =>
        s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\');

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>An ExecStart argument, double-quoted only when it contains whitespace/quotes.</summary>
    static string QuoteArg(string a) => NeedsQuote(a) ? $"\"{Esc(a)}\"" : a;

    /// <summary>An <c>Environment=</c> assignment; the whole <c>KEY=VALUE</c> is quoted when VALUE needs it.</summary>
    static string EnvAssignment(string key, string value) =>
        NeedsQuote(value) ? $"\"{key}={Esc(value)}\"" : $"{key}={value}";
}
