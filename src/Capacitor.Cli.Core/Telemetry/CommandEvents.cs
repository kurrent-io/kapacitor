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
    //
    // `uninstall` is denylisted too, but for a different reason (see below): reporting it would
    // resurrect the directory it just deleted.
    static readonly HashSet<string> Denylisted = new(StringComparer.Ordinal) {
        "hook", "watch", "mcp", "permission-request", "generate-whats-done",
        "set-title", "copilot-finalize", "cursor-verify-appendonly",

        // `Program.cs` already skips the post-command update check for `uninstall`, with a
        // comment explaining why: any write into the config directory after `Directory.Delete`
        // races the just-completed removal and recreates it. The ProcessExit telemetry flush has
        // the identical shape — a failed POST spills to `telemetry-spool.jsonl` via
        // `TelemetrySpool.Append`, which `Directory.CreateDirectory`s the config dir right back
        // into existence. We lose the uninstall count; leaving residue after someone asked for
        // total removal is not a trade worth making.
        "uninstall",
    };

    // The command dispatch's known verbs — every `case "…"` label in Program.cs's top-level
    // switch, plus its `offlineCommands` array, plus the Denylisted set above. Allow-by-exception:
    // a token that isn't a real verb (a stray absolute path, a session GUID fat-fingered as
    // args[0], a URL) never becomes the `command` property — it's reported as "unknown" instead.
    // Denylisted verbs are included here too, even though IsReportable already drops them from
    // ever being reported at all — the two lists are conceptually independent, and a denylisted
    // verb is still a KNOWN one.
    static readonly HashSet<string> KnownVerbs = new(StringComparer.Ordinal) {
        "--help", "-h", "help", "--version", "-v",
        "errors", "recap", "validate-plan", "eval", "login", "logout", "whoami",
        "daemon", "agent", "setup", "plugin", "profile", "use", "status", "config",
        "ignore", "remap", "repos", "projects", "project", "update", "review", "mcp",
        "curate", "cleanup", "uninstall", "disable", "hide", "import", "watch",
        "copilot-finalize", "set-title", "hook", "cursor", "cursor-verify-appendonly",
        "generate-whats-done", "permission-request", "feedback",
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

    // Flags whose NEXT argv token is a VALUE, never a candidate flag name — skipped outright by
    // Flags() below, even when that value happens to itself look like a flag (e.g. a --message
    // value that starts with "--"). Without this, `kcap feedback --bug -m --looks-like-a-flag`
    // would report the message text "--looks-like-a-flag" as a captured flag name, leaking
    // free-form user prose into telemetry.
    //
    // Seeded with just -m/--message: the one value-taking flag across the CLI whose value is
    // ever free-form text a person types, rather than a fixed-vocabulary token (--model,
    // --visibility, …) or something already excluded by shape (a URL, a path, a session id).
    // Extend this set if another free-text value flag is ever added.
    static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal) {
        "-m", "--message",
    };

    public static bool IsReportable(string command) => !Denylisted.Contains(command);

    /// <summary>
    /// The value to put in the `command` property: the verb itself if it's a known one, or
    /// "unknown" otherwise. `Program.cs`'s dispatch falls through to `Unknown command: {command}`
    /// (exit 1) for anything not in its switch, and `args[0]` can be arbitrary user input — a
    /// misplaced absolute path, a session GUID typed a token too early, a pasted repo URL. Without
    /// this allowlist those land in PostHog verbatim the moment someone fumbles a command line.
    /// </summary>
    public static string ReportableCommand(string command) => KnownVerbs.Contains(command) ? command : "unknown";

    public static string? Subcommand(string command, string[] args) {
        if (args.Length < 2) return null;
        if (!Subcommands.TryGetValue(command, out var known)) return null;

        return known.Contains(args[1]) ? args[1] : null;
    }

    /// <summary>
    /// Flag NAMES only, sorted and deduplicated. Admitted by shape, not by a name allowlist.
    /// Maximum length 37 characters is load-bearing, pinned by the usable window [31, 38):
    /// floor is `--skip-antigravity-instructions` (31 chars, longest real flag);
    /// ceiling is a GUID token (38 chars: `--` + 36 UUID). Headroom prevents breakage from
    /// minor flag additions; relaxing re-admits GUIDs; tightening silently drops real flags.
    /// </summary>
    public static string[] Flags(string[] args) {
        var candidates = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            var token = args[i];

            if (token.StartsWith("--", StringComparison.Ordinal)) {
                candidates.Add(token.Split('=', 2)[0]);
            }

            // The token bound to a value-taking flag is DATA, not a candidate flag name — skip
            // it outright so it never reaches the shape/dedup pipeline below, whatever it looks
            // like. This is a plain array skip, not a lookahead into an already-consumed token:
            // the loop's own `i++` advances past `token` as usual, and this second `i++` advances
            // past the value bound to it.
            if (ValueFlags.Contains(token)) {
                i++;
            }
        }

        return candidates
            .Where(a => FlagShape().IsMatch(a))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaxFlags)
            .ToArray();
    }

    [GeneratedRegex(@"^--[a-z][a-z0-9-]{0,34}$")]
    private static partial Regex FlagShape();
}
