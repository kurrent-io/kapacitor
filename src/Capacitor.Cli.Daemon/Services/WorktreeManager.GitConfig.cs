using System.Globalization;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Thrown when git config overrides cannot be carried to a git process, so it is not known that the
/// overrides a command was given are the overrides it will honour. Fail-closed: the alternative is running a
/// containment command that silently has no containment.</summary>
public sealed class GitConfigTransportException(string detail)
    : Exception("Refusing to run git with config overrides that may not apply. " + detail) { }

/// <summary>
/// One git config override, as a key and a value that stay SEPARATE all the way to git.
///
/// <para>A driver, section or variable name may legally contain <c>=</c>, and <c>-c key=value</c> is parsed
/// at the FIRST one: <c>-c filter.evil=x.smudge=</c> reaches git as key <c>filter.evil</c> with value
/// <c>x.smudge=</c>, so <c>filter.evil=x.smudge</c> stays live while the override looks applied. That is why
/// this is a pair and not a string — there is no boundary left to guess at.</para>
/// </summary>
internal readonly record struct GitConfigOverride(string Key, string Value);

public partial class WorktreeManager {
    /// <summary>Config git applies with the same precedence as <c>-c</c>, taken from the environment rather
    /// than argv. Available since git 2.31; <see cref="ProveConfigTransportAsync"/> is what makes an older
    /// git — which would ignore these entirely — a refusal instead of silent non-containment.</summary>
    const string ConfigCountVariable = "GIT_CONFIG_COUNT";

    /// <summary>
    /// Writes <paramref name="overrides"/> into a git process environment as
    /// <c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c> pairs, APPENDING to whatever entries the
    /// environment already carries.
    ///
    /// <para><b>Appending is the whole contract.</b> The environment a git child inherits may already hold
    /// entries — this daemon's own <c>sourceReadOnly</c> suppressions compose through here, and an operator
    /// may have launched the daemon with entries of their own. Writing from index 0 would overwrite theirs,
    /// and a count that does not cover every index makes git ignore the tail: both lose overrides while the
    /// call site believes they were applied, which is the same silent failure as the <c>=</c> mis-encoding
    /// this transport exists to remove.</para>
    /// </summary>
    /// <exception cref="GitConfigTransportException">An inherited count cannot be parsed, so the entries
    /// cannot be appended without displacing it; or a key or value contains NUL, which cannot survive an
    /// environment block and would be silently truncated.</exception>
    internal static void ApplyConfigOverrides(
            IDictionary<string, string?> environment, IReadOnlyList<GitConfigOverride> overrides) {
        if (overrides.Count == 0) return;

        var first = InheritedConfigCount(environment);

        for (var i = 0; i < overrides.Count; i++) {
            var (key, value) = overrides[i];

            // An environment entry is NUL-terminated by construction, so a NUL inside one truncates it
            // there: the key that reached git would be a PREFIX of the key we checked and meant to
            // neutralize. Unreachable from the filter inventory, whose records are NUL-separated to begin
            // with, and refused here rather than assumed, because this is the layer whose invariant it is.
            if (key.Contains('\0') || value.Contains('\0'))
                throw new GitConfigTransportException(
                    "A config override contains NUL, which an environment entry cannot carry.");

            environment[$"GIT_CONFIG_KEY_{first + i}"] = key;
            environment[$"GIT_CONFIG_VALUE_{first + i}"] = value;
        }

        environment[ConfigCountVariable] =
            (first + overrides.Count).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>How many config entries the environment already claims. Absent or empty is none — git reads
    /// an empty count as zero (measured), so agreeing with it keeps our indices where git looks for
    /// them.</summary>
    static int InheritedConfigCount(IDictionary<string, string?> environment) {
        if (!environment.TryGetValue(ConfigCountVariable, out var raw) || string.IsNullOrEmpty(raw))
            return 0;

        // NumberStyles.None: no sign, no whitespace, no separators — the spelling git itself accepts.
        // Anything else and we do not know which indices are already taken. Git dies on such a value
        // anyway; refusing here says why, and never silently renumbers an operator's entries.
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new GitConfigTransportException(
                $"{ConfigCountVariable} is set to '{raw}', which is not a count, so config overrides "
              + "cannot be appended to it.");

        return count;
    }

    /// <summary>The <c>sourceReadOnly</c> suppressions, composed through the same transport as everything
    /// else so their indices are allocated in one place. <c>maintenance.auto</c> stops a read from launching
    /// background maintenance that would touch <c>.git/worktrees</c> outside the metadata gate.</summary>
    static readonly GitConfigOverride[] SourceReadOnlyConfig = [
        new("maintenance.auto", "false"),
        new("core.fsmonitor", "false")
    ];

    static volatile bool _configTransportProven;
    static readonly SemaphoreSlim ConfigTransportGate = new(1, 1);

    /// <summary>
    /// Proves, by running git, that config overrides carried in the environment actually reach it — once per
    /// process, before any containment override is handed out.
    ///
    /// <para><b>Why a runtime proof and not a version check.</b> A git older than 2.31 does not know
    /// <c>GIT_CONFIG_COUNT</c> and IGNORES it: every filter and hook override would be dropped and every
    /// command would still succeed, so the guard would report containment it never had. That is strictly
    /// worse than the mis-encoding this transport replaced, which at least only affected names containing
    /// <c>=</c>. Parsing <c>git --version</c> would answer a narrower question — distributions ship suffixed
    /// versions, and the property we need is not "which git" but "do these entries apply here", which also
    /// covers an environment that cannot carry them at all.</para>
    ///
    /// <para>The probe asserts both value shapes the overrides use: an EMPTY value, which is how a filter
    /// command is disabled and the one an environment block is least likely to carry, and a non-empty one at
    /// a non-zero index, which fails if the count is miscomputed. Its key contains <c>=</c>, so the boundary
    /// this transport exists for is measured rather than assumed. The key is unguessable per probe, so no
    /// config a repository or branch can write could supply the record that would satisfy it in the
    /// transport's place.</para>
    ///
    /// <para>Only success is remembered: a spawn that failed for an unrelated reason must not wedge the
    /// daemon until restart. Whether the entries apply is a property of the git binary and the platform, not
    /// of the repository, so one measurement holds for the process.</para>
    /// </summary>
    /// <exception cref="GitConfigTransportException">The entries did not reach git.</exception>
    internal static async Task ProveConfigTransportAsync(string gitContextPath) {
        if (_configTransportProven) return;

        await ConfigTransportGate.WaitAsync();
        try {
            if (_configTransportProven) return;

            await ProbeConfigTransportAsync(gitContextPath);
            _configTransportProven = true;
        } finally {
            ConfigTransportGate.Release();
        }
    }

    /// <summary>
    /// The measurement itself, separate from the memoization above so it can be run on demand — a test of a
    /// cached proof would otherwise pass by returning early, testing nothing.
    ///
    /// <para>Run in the caller's own git context rather than a directory of our own: this is a context the
    /// caller is about to run git in regardless, so the probe cannot invent a failure the real command would
    /// not have had — a repository git refuses to touch (dubious ownership, say) fails either way, and no
    /// directory has to be created, permissioned or cleaned up to hold it.</para>
    /// </summary>
    internal static async Task ProbeConfigTransportAsync(string gitContextPath) {
        var driver = $"kcap-transport-probe={Guid.NewGuid():N}";
        GitConfigOverride[] probe = [
            new($"filter.{driver}.smudge", ""),
            new($"filter.{driver}.required", "false")
        ];

        var listing = await RunGitCaptureResult(
            gitContextPath, GitTimeout, sourceReadOnly: false, probe, "config", "--list", "-z");

        if (listing.ExitCode != 0)
            throw new GitConfigTransportException(
                $"`git config --list` exited {listing.ExitCode} while reading probe overrides from the "
              + $"environment: {listing.Stderr.Trim()}");

        RequireProbeApplied(listing.Stdout, probe);
    }

    /// <summary>Checks a <c>config --list -z</c> listing reports every probe entry with the value it was
    /// given. Records are <c>key\nvalue\0</c>; an entry with an empty value is <c>key\n</c>, which is why
    /// the comparison is against the whole record and not a prefix of it. Presence, not exclusivity: the
    /// probe directory may sit inside a repository, and the probe key is unguessable, so a real config
    /// cannot supply the record that would pass this.</summary>
    internal static void RequireProbeApplied(string listing, IReadOnlyList<GitConfigOverride> probe) {
        var records = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        foreach (var (key, value) in probe) {
            if (!records.Contains($"{key}\n{value}", StringComparer.Ordinal))
                throw new GitConfigTransportException(
                    $"git did not report the config override '{key}' this process passed in "
                  + $"{ConfigCountVariable}/GIT_CONFIG_KEY_n. Overrides carried that way would be silently "
                  + "dropped — git 2.31 or newer is required to create an agent worktree.");
        }
    }
}
