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
    /// Filter drivers permitted to run while branch content is materialised. Everything else defined in the
    /// operator's config is disabled for those commands.
    ///
    /// <para><c>lfs</c> is here because git-lfs is ubiquitous and disabling it does not fail loudly — it
    /// silently yields pointer files instead of content. Membership is necessary but NOT sufficient: the
    /// binding is authenticated too, see <see cref="IsAuthenticAllowedDriverAsync"/>.</para>
    /// </summary>
    internal static readonly string[] AllowedFilterDrivers = ["lfs"];

    /// <summary>The executable an allowlisted driver is rebound to.</summary>
    const string AllowedFilterBinary = "git-lfs";

    /// <summary>The absolute path substituted into an allowlisted driver's command, or null when the binary
    /// cannot be resolved — in which case the driver is disabled rather than left on the operator's own
    /// command. Exposed so tests can skip when the host has no git-lfs.</summary>
    internal static string? ResolvedAllowedFilterBinary =>
        CliResolver.ResolveExecutable(AllowedFilterBinary) is { } p && Path.IsPathRooted(p) ? p : null;

    /// <summary>
    /// Config overrides disabling every filter driver except an authenticated
    /// <see cref="AllowedFilterDrivers"/>, for the git commands that materialise or ingest branch content.
    ///
    /// <para><b>The vector.</b> <c>.gitattributes</c> is branch content and SELECTS which driver applies to
    /// a path. The command comes from the operator's config, but a relative one resolves against the
    /// worktree, so the branch supplies the executable. Measured: <c>filter.x.smudge=./tools/f</c> with a
    /// branch-committed <c>tools/f</c> runs during <c>worktree add</c>. <c>core.hooksPath</c> does not
    /// affect filters.</para>
    ///
    /// <para><b>Why not classify the command.</b> The first version did, and review defeated it four ways:
    /// <c>sh tools</c> and <c>python filter.py</c> execute a branch-supplied file with no path separator;
    /// <c>/bin/true;./tools/f</c> is one rooted token whose shell runs the relative half; and <c>%f</c> is
    /// substituted by git AFTER any inspection. A command string is a shell program, and deciding what a
    /// shell program will execute is not something a tokeniser can do.</para>
    ///
    /// <para><b>Enumerated by NAME ONLY</b> (<c>--name-only -z</c>), so config VALUES never enter this
    /// parse: a value of <c>cat\n./tools/f</c> would read to a line-splitting inventory as a safe record
    /// plus an ignored line while git executes the whole thing.</para>
    ///
    /// <para><b>Enumerated in the SAME context the guarded command runs in</b>, with the same config
    /// visibility — not <c>sourceReadOnly</c>, which sets <c>GIT_CONFIG_NOSYSTEM</c> and would hide a
    /// system-scoped driver that is live during materialisation, and against the same directory, so a
    /// conditional <c>includeIf.gitdir</c> resolves identically for both.</para>
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

        var disable = new SortedSet<string>(StringComparer.Ordinal);
        var allowed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = key.Trim().Split('.');
            if (parts.Length < 3) continue;

            var driver = string.Join('.', parts[1..^1]);       // a driver name may itself contain dots

            // `-c key=value` splits at the FIRST '='. A driver legally named `evil=x` would be written as
            // key `filter.evil`, leaving `filter.evil=x.smudge` live while the override LOOKED applied.
            // Git permits arbitrary subsection characters, so refuse rather than mis-encode: a guard that
            // silently does nothing is worse than a launch that fails.
            if (driver.Contains('=') || driver.Contains('\n') || driver.Contains('\0'))
                throw new BranchFilterInventoryException(gitContextPath,
                    $"Filter driver '{driver}' has a name that cannot be safely expressed as a command-line "
                  + "override, so it cannot be contained.");

            if (AllowedFilterDrivers.Contains(driver, StringComparer.Ordinal)) allowed.Add(driver);
            else disable.Add(driver);
        }

        // An allowlisted driver whose canonical form cannot be built (no resolvable git-lfs) falls through
        // to the disable set — never left live on the operator's own command.
        foreach (var driver in allowed)
            if (CanonicalAllowedDriverOverrides(driver).Length == 0) disable.Add(driver);

        return [.. allowed.SelectMany(CanonicalAllowedDriverOverrides),
                .. disable.SelectMany(static driver => new[] {
            "-c", $"filter.{driver}.clean=",
            "-c", $"filter.{driver}.smudge=",
            "-c", $"filter.{driver}.process=",
            // Measured: with `required=true`, an empty command is FATAL and the checkout fails outright.
            // Clearing the command alone would turn this guard into a denial of service.
            "-c", $"filter.{driver}.required=false"
        })];
    }

    /// <summary>
    /// The overrides that keep an allowlisted driver working — by REPLACING its command with our own,
    /// built from a <c>git-lfs</c> we resolved ourselves, rather than by vetting the operator's string.
    ///
    /// <para>Authenticating the operator's command was the previous attempt and review showed it unsound:
    /// git runs the whole value through a shell, so <c>git-lfs smudge -- %f; ./tools/f</c> passes any
    /// first-token check and then executes branch content; a branch-owned <c>/repo/tools/git-lfs</c> passes
    /// by basename; and a bare <c>git-lfs</c> can be shadowed if the inherited PATH has a relative
    /// component. Every one of those is a way for a string to look like the binary without being it.</para>
    ///
    /// <para>So nothing the operator wrote is executed. The command is ours, the path is absolute and
    /// resolved by us, and the operator's value is simply overwritten for the guarded commands. If
    /// <c>git-lfs</c> cannot be resolved there is nothing trustworthy to substitute, so the driver is
    /// disabled like any other — pointer files rather than an unvetted execution.</para>
    ///
    /// <para>Cost, deliberately accepted: an operator who wraps git-lfs behind their own script loses that
    /// wrapper inside agent worktrees. Their wrapper is exactly the branch-reachable indirection this
    /// exists to remove.</para>
    /// </summary>
    static string[] CanonicalAllowedDriverOverrides(string driver) {
        if (!AllowedFilterDrivers.Contains(driver, StringComparer.Ordinal)) return [];

        var resolved = CliResolver.ResolveExecutable(AllowedFilterBinary);
        if (resolved is null || !Path.IsPathRooted(resolved)) return [];

        return [
            "-c", $"filter.{driver}.clean={resolved} clean -- %f",
            "-c", $"filter.{driver}.smudge={resolved} smudge -- %f",
            "-c", $"filter.{driver}.process={resolved} filter-process",
            "-c", $"filter.{driver}.required=true"
        ];
    }
}
