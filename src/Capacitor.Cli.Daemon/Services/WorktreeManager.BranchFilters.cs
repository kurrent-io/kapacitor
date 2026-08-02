namespace Capacitor.Cli.Daemon.Services;

/// <summary>Thrown when the filter drivers active for a repository cannot be enumerated, so it is not known
/// which of them a branch could reach. Fail-closed: the alternative is materialising branch content with
/// every filter live.</summary>
public sealed class BranchFilterInventoryException(string repoPath, string detail)
    : Exception($"Refusing to build a worktree for '{repoPath}': the active git filter drivers could not be "
              + $"enumerated, so branch-selected filters cannot be contained. {detail}") {
    public string RepoPath { get; } = repoPath;
}

public partial class WorktreeManager {
    /// <summary>
    /// Filter drivers permitted to run while branch content is materialised. Everything else defined in the
    /// operator's config is disabled for those commands.
    ///
    /// <para><c>lfs</c> is here because git-lfs is ubiquitous and its command is a fixed, PATH-resolved
    /// binary the branch cannot influence — and because disabling it does not fail loudly, it silently
    /// yields pointer files instead of content.</para>
    /// </summary>
    internal static readonly string[] AllowedFilterDrivers = ["lfs"];

    /// <summary>
    /// Config overrides disabling every filter driver except <see cref="AllowedFilterDrivers"/>, for the
    /// git commands that materialise or ingest branch content.
    ///
    /// <para><b>The vector.</b> <c>.gitattributes</c> is branch content and SELECTS which driver applies to
    /// a path. The command comes from the operator's config, but a relative one resolves against the
    /// worktree, so the branch supplies the executable. Measured: <c>filter.x.smudge=./tools/f</c> with a
    /// branch-committed <c>tools/f</c> runs during <c>worktree add</c>. <c>core.hooksPath</c> does not
    /// affect filters.</para>
    ///
    /// <para><b>Why an allowlist of NAMES and not an analysis of commands.</b> The first version classified
    /// the command string — relative paths were disabled, PATH-resolved ones kept. Review took it apart:
    /// <c>sh tools</c> and <c>python filter.py</c> execute a branch-supplied file with no path separator at
    /// all; <c>/bin/true;./tools/f</c> is one rooted token whose shell runs the relative half; and <c>%f</c>
    /// is substituted by git AFTER any inspection we could do. A command string is a shell program, and
    /// deciding what a shell program will execute is not something a tokeniser can do. The name is not a
    /// program — a branch can only select from drivers the operator already defined, and whether one of
    /// those is trusted is the operator's answer to give, not ours to infer.</para>
    ///
    /// <para><b>Enumerated by NAME ONLY.</b> <c>--name-only -z</c> means values never enter the parse, which
    /// removes the newline-in-a-config-value bypass a line-split inventory has: a value of
    /// <c>cat\n./tools/f</c> reads as a safe record plus an ignored line while git executes the whole
    /// thing.</para>
    ///
    /// <para><b>Enumerated in the SAME context the command will run in</b>, with the same config visibility
    /// — not <c>sourceReadOnly</c>, which sets <c>GIT_CONFIG_NOSYSTEM</c> and would hide a system-scoped
    /// driver that is then live during materialisation, and against the directory the command uses, so a
    /// conditional <c>includeIf.gitdir</c> resolves the same way for both.</para>
    /// </summary>
    /// <exception cref="BranchFilterInventoryException">Enumeration failed for a reason other than "no
    /// drivers defined".</exception>
    internal static async Task<string[]> BranchFilterOverridesAsync(string gitContextPath) {
        var listed = await RunGitCaptureResult(gitContextPath, GitTimeout, sourceReadOnly: false,
            "config", "--name-only", "-z", "--get-regexp", "^filter\\..*\\.(clean|smudge|process)$");

        // Exit 1 is git's "no key matched" — the common case of a repo with no filters at all. Anything
        // else means we do not KNOW what is defined, and proceeding with an empty override set would run
        // the materialisation with every driver live.
        if (listed.ExitCode is not (0 or 1))
            throw new BranchFilterInventoryException(gitContextPath,
                $"`git config --get-regexp` exited {listed.ExitCode}: {listed.Stderr.Trim()}");

        var drivers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = key.Trim().Split('.');
            if (parts.Length < 3) continue;

            var driver = string.Join('.', parts[1..^1]);       // a driver name may itself contain dots
            if (!AllowedFilterDrivers.Contains(driver, StringComparer.Ordinal)) drivers.Add(driver);
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
