namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    /// <summary>
    /// Git config that disables any clean/smudge/process filter whose command would be supplied by the
    /// branch, for the commands that materialise branch content.
    ///
    /// <para><b>The vector.</b> <c>.gitattributes</c> is branch content and SELECTS which filter driver
    /// applies to a path. The driver's command comes from the operator's config — but if that command is
    /// relative, it resolves against the worktree, so the branch supplies the executable. Measured:
    /// <c>filter.x.smudge=./tools/f</c> plus a branch-committed <c>tools/f</c> runs during
    /// <c>worktree add</c>, before anything has neutralised the tree. <c>core.hooksPath</c> does not affect
    /// filters at all.</para>
    ///
    /// <para><b>Why not disable every filter.</b> That breaks <c>git-lfs</c>, whose <c>filter.lfs.smudge</c>
    /// is entirely legitimate and whose absence yields pointer files instead of content — silently. The
    /// distinguishing property is whether the command resolves to branch content, so only relative commands
    /// are neutralised and PATH- or absolutely-resolved ones are left alone.</para>
    ///
    /// <para><b>Enumeration is sound here.</b> A filter can only run if it is DEFINED in config, and the
    /// branch can only select from what is defined — so disabling the definitions that are branch-resolvable
    /// covers every driver the branch could reach.</para>
    ///
    /// <para><b><c>required</c> must be cleared too.</b> Measured: with <c>filter.x.required=true</c>, an
    /// empty smudge command is FATAL and <c>worktree add</c> fails outright. Suppressing the command alone
    /// would turn this guard into a launch failure for any repo using a required filter.</para>
    /// </summary>
    internal static async Task<string[]> BranchResolvableFilterOverridesAsync(string repoPath) {
        // Best effort: `config --get-regexp` exits non-zero when nothing matches, which is the common case
        // (no filters defined at all). A failure here must not fail the launch — it simply means no
        // overrides, and the guard is additive.
        var listed = await RunGitCaptureResult(repoPath, GitTimeout, sourceReadOnly: true,
            "config", "--get-regexp", "^filter\\..*\\.(clean|smudge|process)$");
        if (listed.ExitCode != 0 || string.IsNullOrWhiteSpace(listed.Stdout)) return [];

        var drivers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var line in listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var split = line.IndexOf(' ');
            if (split <= 0) continue;

            var name    = line[..split];                       // filter.<driver>.<op>
            var command = line[(split + 1)..].Trim();
            var parts   = name.Split('.');
            if (parts.Length < 3 || !ResolvesToBranchContent(command)) continue;

            drivers.Add(string.Join('.', parts[1..^1]));       // a driver name may itself contain dots
        }

        return [.. drivers.SelectMany(static driver => new[] {
            "-c", $"filter.{driver}.clean=",
            "-c", $"filter.{driver}.smudge=",
            "-c", $"filter.{driver}.process=",
            "-c", $"filter.{driver}.required=false"
        })];
    }

    /// <summary>
    /// Whether a filter command could execute something the branch supplies.
    ///
    /// <para>Every whitespace-separated token is examined, not just the executable: <c>sh -c 'cat
    /// ./tools/x'</c> has an absolute-or-PATH executable and a branch-controlled payload. Any token that
    /// looks like a relative path condemns the whole command, which is the fail-closed direction — the cost
    /// of a false positive is one legitimate filter disabled inside agent worktrees, against branch-supplied
    /// code executing as the daemon user.</para>
    /// </summary>
    internal static bool ResolvesToBranchContent(string command) {
        foreach (var raw in command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)) {
            var token = raw.Trim('"', '\'');
            if (token.Length == 0) continue;

            if (token.StartsWith("./", StringComparison.Ordinal) ||
                token.StartsWith("../", StringComparison.Ordinal) ||
                token.StartsWith(".\\", StringComparison.Ordinal) ||
                token.StartsWith("..\\", StringComparison.Ordinal)) return true;

            // A separator without an absolute root is relative — `tools/f`. A bare name (`git-lfs`) is
            // PATH-resolved and cannot be supplied by the branch, so it is left alone.
            if ((token.Contains('/') || token.Contains('\\')) && !Path.IsPathRooted(token)) return true;
        }

        return false;
    }
}
