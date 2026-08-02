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

    /// <summary>The executable an allowlisted driver must actually invoke.</summary>
    const string AllowedFilterBinary = "git-lfs";

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

            if (!await IsAuthenticAllowedDriverAsync(gitContextPath, driver)) disable.Add(driver);
        }

        return [.. disable.SelectMany(static driver => new[] {
            "-c", $"filter.{driver}.clean=",
            "-c", $"filter.{driver}.smudge=",
            "-c", $"filter.{driver}.process=",
            // Measured: with `required=true`, an empty command is FATAL and the checkout fails outright.
            // Clearing the command alone would turn this guard into a denial of service.
            "-c", $"filter.{driver}.required=false"
        })];
    }

    /// <summary>
    /// Whether <paramref name="driver"/> is allowlisted AND actually bound to the expected binary.
    ///
    /// <para>Trusting the NAME alone is the same mistake as trusting a command string, reached from the
    /// other side: <c>filter.lfs.smudge=./tools/f</c> is a legal config, and a branch selecting
    /// <c>filter=lfs</c> would ride the allowlist straight to its own file. This is NOT the general command
    /// classification that was removed — it is the far narrower question "does this invoke the one binary
    /// we decided to trust", decided on the first token's filename. Anything unreadable or unexpected fails
    /// closed and the driver is disabled like any other.</para>
    /// </summary>
    static async Task<bool> IsAuthenticAllowedDriverAsync(string gitContextPath, string driver) {
        if (!AllowedFilterDrivers.Contains(driver, StringComparer.Ordinal)) return false;

        foreach (var op in new[] { "clean", "smudge", "process" }) {
            var value = await RunGitCaptureResult(gitContextPath, GitTimeout, sourceReadOnly: false,
                "config", "--get", $"filter.{driver}.{op}");

            if (value.ExitCode == 1) continue;                 // not defined for this operation
            if (value.ExitCode != 0) return false;             // unreadable — do not extend trust

            var first = value.Stdout.Trim()
                .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (first is null) return false;

            var name = Path.GetFileName(first.Trim('"', '\''));
            if (!name.Equals(AllowedFilterBinary, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(AllowedFilterBinary + ".exe", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
