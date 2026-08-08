using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Decides what may be said about a command invocation. Everything here is allow-by-exception:
/// an unrecognised subcommand or a malformed flag is dropped, so new commands and new flags are
/// silent until deliberately added rather than leaking by default.
/// </summary>
public static partial class CommandEvents {
    // Machine-driven surfaces. `hook` runs on every tool use of every recorded session —
    // thousands per user per day, inline in the agent's critical path — and the rest are
    // spawned by agents or long-lived processes rather than typed by a person.
    static readonly HashSet<string> Denylisted = new(StringComparer.Ordinal) {
        "hook", "watch", "mcp", "permission-request", "generate-whats-done",
        "set-title", "copilot-finalize", "cursor-verify-appendonly",
    };

    // Verbs whose args[1] is a known literal rather than user data. Verbs absent from this
    // map report no subcommand at all — which is what keeps `recap <sessionId>`,
    // `ignore <path>`, `hide <sessionId>` and `remap <path>` from ever reporting a positional.
    static readonly Dictionary<string, HashSet<string>> Subcommands = new(StringComparer.Ordinal) {
        ["daemon"]  = new(StringComparer.Ordinal) { "start", "stop", "status", "restart", "logs", "consent", "reviewer" },
        ["plugin"]  = new(StringComparer.Ordinal) { "install", "remove", "status" },
        ["config"]  = new(StringComparer.Ordinal) { "show", "set", "unset" },
        ["profile"] = new(StringComparer.Ordinal) { "list", "add", "remove", "show" },
        ["curate"]  = new(StringComparer.Ordinal) { "apply" },
        ["agent"]   = new(StringComparer.Ordinal) { "start", "stop", "list", "status" },
    };

    const int MaxFlags = 12;

    public static bool IsReportable(string command) => !Denylisted.Contains(command);

    public static string? Subcommand(string command, string[] args) {
        if (args.Length < 2) return null;
        if (!Subcommands.TryGetValue(command, out var known)) return null;

        return known.Contains(args[1]) ? args[1] : null;
    }

    /// <summary>
    /// Flag NAMES only, sorted and deduplicated. Admitted by shape, not by a name allowlist:
    /// the pattern structurally cannot express a path, URL, GUID, or email address, so nothing
    /// identifying survives regardless of what future commands introduce.
    /// </summary>
    public static string[] Flags(string[] args) =>
        args.Where(a => a.StartsWith("--", StringComparison.Ordinal))
            .Select(a => a.Split('=', 2)[0])
            .Where(a => FlagShape().IsMatch(a))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaxFlags)
            .ToArray();

    [GeneratedRegex(@"^--[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex FlagShape();
}
