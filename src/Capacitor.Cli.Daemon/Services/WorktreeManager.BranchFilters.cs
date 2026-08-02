namespace Capacitor.Cli.Daemon.Services;

/// <summary>Thrown when the filter drivers active for a repository cannot be enumerated or cannot be safely
/// overridden, so it is not known which of them a branch could reach. Fail-closed: the alternative is
/// materialising branch content with every filter live.</summary>
public sealed class BranchFilterInventoryException(string repoPath, string detail)
    : Exception($"Refusing to build a worktree for '{repoPath}': git filter drivers could not be contained. "
              + detail) {
    public string RepoPath { get; } = repoPath;
}

public partial class WorktreeManager {
    /// <summary>
    /// Config overrides disabling EVERY defined clean/smudge/process filter, for the git commands that
    /// materialise or ingest branch content.
    ///
    /// <para><b>The vector.</b> <c>.gitattributes</c> is branch content and SELECTS which driver applies to
    /// a path. The command comes from the operator's config, but a relative one resolves against the
    /// worktree, so the branch supplies the executable. Measured: <c>filter.x.smudge=./tools/f</c> with a
    /// branch-committed <c>tools/f</c> runs during <c>worktree add</c>. <c>core.hooksPath</c> does not
    /// affect filters.</para>
    ///
    /// <para><b>Why there is no exemption, not even for git-lfs.</b> Four successive designs tried to keep
    /// LFS working and each was defeated in review, always in the same place — the exemption:</para>
    /// <list type="number">
    /// <item>Classify the command, disable relative ones. <c>sh tools</c> and <c>python filter.py</c> run a
    /// branch file with no separator; <c>/bin/true;./tools/f</c> is one rooted token whose shell runs the
    /// relative half; <c>%f</c> is substituted after any inspection.</item>
    /// <item>Allowlist the NAME <c>lfs</c>. A name is a convention — <c>filter.lfs.smudge=./tools/f</c> is
    /// legal config, and a branch selecting <c>filter=lfs</c> rides the allowlist to its own file.</item>
    /// <item>Authenticate the binding by the command's first token. Git runs the whole value through a
    /// shell, so <c>git-lfs smudge -- %f; ./tools/f</c> passes and then executes branch content.</item>
    /// <item>Rebind to a <c>git-lfs</c> we resolve ourselves. The daemon's PATH is ambient — it inherits the
    /// launching shell's, which may carry a relative or repo-local entry — and the resolved path was
    /// interpolated unquoted into what is, again, a shell program.</item>
    /// </list>
    ///
    /// <para>Four mechanisms, four holes, all guarding one exception. Disabling every driver has no such
    /// surface: nothing is parsed, nothing is resolved, nothing is authenticated, and there is no name to
    /// impersonate. The cost is real and is documented rather than hidden — LFS-tracked files check out as
    /// pointer text inside agent worktrees, and a custom filter does not run there. Removals are logged so
    /// an operator sees it happen instead of wondering why a file looks wrong.</para>
    /// </summary>
    /// <exception cref="BranchFilterInventoryException">Enumeration failed, or a driver name cannot be
    /// safely expressed as an override.</exception>
    internal static async Task<string[]> BranchFilterOverridesAsync(string gitContextPath) {
        var listed = await RunGitCaptureResult(gitContextPath, GitTimeout, sourceReadOnly: false,
            "config", "--name-only", "-z", "--get-regexp", "^filter\\..*\\.(clean|smudge|process)$");

        // Exit 1 is git's "no key matched" — a repo with no filters, the common case. Anything else means
        // we do not KNOW what is defined, and an empty override set would run the materialisation with
        // every driver live.
        if (listed.ExitCode is not (0 or 1))
            throw new BranchFilterInventoryException(gitContextPath,
                $"`git config --get-regexp` exited {listed.ExitCode}: {listed.Stderr.Trim()}");

        var drivers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = key.Trim().Split('.');
            if (parts.Length < 3) continue;

            var driver = string.Join('.', parts[1..^1]);       // a driver name may itself contain dots

            // `-c key=value` splits at the FIRST '='. A driver legally named `evil=x` would be written as
            // key `filter.evil`, leaving `filter.evil=x.smudge` live while the override LOOKED applied.
            // Git permits arbitrary subsection characters, so refuse rather than mis-encode: a guard that
            // silently does nothing is worse than a launch that fails. The env transport that would carry
            // such a name safely is tracked separately.
            if (driver.Contains('=') || driver.Contains('\n') || driver.Contains('\0'))
                throw new BranchFilterInventoryException(gitContextPath,
                    $"Filter driver '{driver}' has a name that cannot be safely expressed as a command-line "
                  + "override, so it cannot be contained.");

            drivers.Add(driver);
        }

        return [.. drivers.SelectMany(static driver => new[] {
            "-c", $"filter.{driver}.clean=",
            "-c", $"filter.{driver}.smudge=",
            "-c", $"filter.{driver}.process=",
            // Measured: with `required=true`, an empty command is FATAL and the checkout fails outright.
            // Clearing the command alone would turn this guard into a denial of service.
            "-c", $"filter.{driver}.required=false"
        })];
    }
}
