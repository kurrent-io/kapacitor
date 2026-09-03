using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

internal sealed record SessionsOptions(string State, string? Repo, string? RepoHash, bool Mine, string? Touching, int Limit, bool Json);

/// <summary>Parses <c>kcap sessions</c> flags. The generic top-level flag helper returns the first
/// value and validates nothing, so exclusivity, value presence and shapes are checked here.</summary>
internal static class SessionsArgs {
    public const string Usage =
        "Usage: kcap sessions [--active | --ended | --all] [--repo <owner/name|hash>] [--mine] [--touching <path>] [--limit <n>] [--json]";

    public static SessionsOptions? Parse(string[] args, out string? error) {
        error = null;

        string? state    = null;
        string? repo     = null;
        string? repoHash = null;
        var     mine     = false;
        string? touching = null;
        var     limit    = 20;
        var     json     = false;

        for (var i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--active" or "--ended" or "--all":
                    if (state is not null) {
                        error = $"choose one of --active, --ended or --all (got {state} and {args[i]})";

                        return null;
                    }

                    state = args[i];

                    break;
                case "--mine": mine = true; break;
                case "--json": json = true; break;
                case "--repo":
                    if (!TryValue(args, ref i, out repo)) { error = "--repo needs a value"; return null; }

                    if (!RepoHashHelper.TryParseRepoRef(repo, out var hash)) {
                        error = "--repo must be <owner>/<name> or a 16-hex repo hash";

                        return null;
                    }

                    repoHash = hash;

                    break;
                case "--touching":
                    if (!TryValue(args, ref i, out touching)) { error = "--touching needs a value"; return null; }

                    break;
                case "--limit":
                    if (!TryValue(args, ref i, out var raw) || !int.TryParse(raw, out limit) || limit is < 1 or > 100) {
                        error = "--limit must be a number from 1 to 100";

                        return null;
                    }

                    break;
                default:
                    error = $"unknown flag {args[i]}";

                    return null;
            }
        }

        return new(state?.TrimStart('-') ?? "active", repo, repoHash, mine, touching, limit, json);
    }

    static bool TryValue(string[] args, ref int i, out string value) {
        value = "";

        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return false;

        value = args[++i];

        return true;
    }
}
