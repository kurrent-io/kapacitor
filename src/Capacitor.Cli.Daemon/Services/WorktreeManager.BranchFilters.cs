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
    /// impersonate. The cost is real and is documented rather than hidden. Be precise about the boundary:
    /// these overrides are PER-COMMAND, covering the git commands kcap uses to create and populate a
    /// worktree — the window in which branch content is first materialised, before an agent is running.
    /// Git the agent runs there afterwards uses the repository's own configuration. What that means for
    /// file contents also differs by path: an owned worktree checks out through git and so holds LFS
    /// pointer text, while standalone and borrowed snapshots carry the source's own bytes (see the README).
    /// Removals are logged so an operator sees it happen instead of wondering why a file looks wrong.</para>
    /// </summary>
    /// <exception cref="BranchFilterInventoryException">Enumeration failed, or a driver name cannot be
    /// safely expressed as an override.</exception>
    /// <exception cref="GitConfigTransportException">The transport that would carry the overrides does not
    /// reach git, so they would be silently dropped.</exception>
    internal static async Task<GitConfigOverride[]> BranchFilterOverridesAsync(string gitContextPath) {
        // Before the inventory, not after: an override set that cannot be delivered is not containment, and
        // the caller's next step is the command that materialises branch content.
        await ProveConfigTransportAsync(gitContextPath);

        // Enumerate EVERY key and match the shape here, rather than asking git to match a regex.
        //
        // Measured: git's `--get-regexp` runs through the platform regex in the ambient locale, where `.`
        // does not match a byte that is not valid in that encoding. A driver named with a raw 0xff byte —
        // `[filter "ev\xffil"]` — is therefore invisible to `^filter\..*\.(clean|smudge|process)$` while
        // `^filter\.` still finds it, so the inventory came back EMPTY, no override was emitted, and a
        // branch selecting `filter=ev\xffil` in .gitattributes ran the driver. That is precisely the
        // bypass this file exists to prevent, reintroduced by trusting git to enumerate.
        //
        // `--list` takes no pattern, so there is no regex, no locale, and nothing to slip past.
        var listed = await RunGitCaptureResult(gitContextPath, GitTimeout, sourceReadOnly: false, [],
            "config", "--list", "--name-only", "-z");

        // No "no keys" exit code to tolerate here: `--list` succeeds on an empty config. A non-zero exit
        // means we do not KNOW what is defined, and an empty override set would run the materialisation
        // with every driver live.
        if (listed.ExitCode != 0)
            throw new BranchFilterInventoryException(gitContextPath,
                $"`git config --list` exited {listed.ExitCode}: {listed.Stderr.Trim()}");

        var drivers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = key.Trim().Split('.');
            if (parts.Length < 3) continue;

            // The shape test git's regex used to perform. Section and variable names are canonicalized to
            // lowercase by git on output (measured), so Ordinal is correct; only the subsection keeps case.
            if (!parts[0].Equals("filter", StringComparison.Ordinal)) continue;
            if (parts[^1] is not ("clean" or "smudge" or "process")) continue;

            var driver = string.Join('.', parts[1..^1]);       // a driver name may itself contain dots

            // A name we cannot reproduce is a name we cannot disable. git config is byte-oriented, so a
            // driver name need not be valid UTF-8; decoding one that is not yields U+FFFD, and an override
            // built from that spelling would target a DIFFERENT driver while looking perfectly applied —
            // the same failure as a mis-encoded `=`, and the reason this refuses rather than guesses. A
            // genuine U+FFFD in a driver name is refused too: rare, and fail-closed is the right side.
            if (driver.Contains('\uFFFD'))
                throw new BranchFilterInventoryException(gitContextPath,
                    "A filter driver name is not valid UTF-8, so an override cannot be expressed for it "
                  + "and the driver cannot be contained.");

            // A name containing `=` needs no special handling: overrides travel as key/value PAIRS in the
            // environment, so `filter.evil=x.smudge` arrives whole. It could not be spelled as
            // `-c key=value`, which splits at the first `=` — key `filter.evil`, the real driver still live,
            // the override apparently applied — and was refused for that reason alone. Measured: plain git
            // runs such a driver, `-c` does not stop it, this does.
            drivers.Add(driver);
        }

        return [.. drivers.SelectMany(static driver => new GitConfigOverride[] {
            new($"filter.{driver}.clean", ""),
            new($"filter.{driver}.smudge", ""),
            new($"filter.{driver}.process", ""),
            // Measured: with `required=true`, an empty command is FATAL and the checkout fails outright.
            // Clearing the command alone would turn this guard into a denial of service.
            new($"filter.{driver}.required", "false")
        })];
    }
}
