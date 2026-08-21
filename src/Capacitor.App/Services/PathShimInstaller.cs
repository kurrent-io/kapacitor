namespace Capacitor.App.Services;

public enum ShimPreflight { Installable, AlreadyInstalled, Conflict }

public enum ShimOutcome { Installed, InstalledButNotOnPath, Cancelled, Failed }

public sealed record ShimResult(ShimOutcome Outcome, string? Detail, string? SudoFallback);

/// Installs a `/usr/local/bin/kcap` symlink to the resolved CLI so a terminal PATH that omits the
/// app's own resolution still finds `kcap` (spec §5). Mechanics only — the once-ever offer and
/// tray-menu wiring live in ShimOfferCoordinator.
public sealed class PathShimInstaller(IProcessRunner runner, ILoginShellProbe probe) {
    public const string Destination = "/usr/local/bin/kcap";

    const string OsascriptPath = "/usr/bin/osascript";

    public Task<ShimResult> InstallAsync(string target, CancellationToken ct) =>
        InstallAsync(target, Destination, ct);

    // Destination is a parameter (not the Destination constant) so tests drive real filesystem
    // taxonomy against a temp path instead of the actual /usr/local/bin/kcap.
    internal async Task<ShimResult> InstallAsync(string target, string destination, CancellationToken ct) {
        if (!LooksLikeTarget(target))
            return new ShimResult(ShimOutcome.Failed, "CLI path contains a newline or carriage return and cannot be used.", null);

        switch (Preflight(destination, target)) {
            case ShimPreflight.AlreadyInstalled:
                // The symlink resolving to our target is not by itself "on PATH" (spec §5: "never
                // report success on the symlink alone") — a prior install (or a hand-made link) can
                // sit there while the login shell's PATH still omits /usr/local/bin. Same probe, same
                // Installed/InstalledButNotOnPath mapping as the freshly-linked branch below.
                return await ProbeOutcomeAsync(destination, ct).ConfigureAwait(false);
            case ShimPreflight.Conflict:
                return new ShimResult(ShimOutcome.Failed, $"{destination} already exists ({Describe(destination)}) and was left untouched.", null);
        }

        var result = await runner.RunAsync(OsascriptPath, OsascriptArgs(target), new RunOptions(), ct).ConfigureAwait(false);

        if (result.ExitCode == 0) return await ProbeOutcomeAsync(destination, ct).ConfigureAwait(false);

        // The parenthesized form only — a bare "-128" substring can appear unescaped inside a
        // genuine failure's shell error text (e.g. a target path like ".../app-128/kcap"), which
        // would otherwise misclassify a real Failed as Cancelled and silently drop the Detail and
        // SudoFallback recovery command.
        if (result.Stderr.Contains("(-128)"))
            return new ShimResult(ShimOutcome.Cancelled, null, null);

        var sudoFallback = "sudo mkdir -p /usr/local/bin && sudo ln -s " + PosixQuote(target) + " /usr/local/bin/kcap";
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? "osascript failed." : result.Stderr.Trim();
        return new ShimResult(ShimOutcome.Failed, detail, sudoFallback);
    }

    /// Shared post-install/AlreadyInstalled mapping (spec §5: "never report success on the symlink
    /// alone"): re-run the login-shell PATH probe and only call it Installed when `kcap` actually
    /// resolves — otherwise InstalledButNotOnPath with the same actionable Detail. Forces a FRESH
    /// probe (never the pre-install cached answer, which the offer decision itself already
    /// consumed and is now stale — the install just changed the filesystem).
    async Task<ShimResult> ProbeOutcomeAsync(string destination, CancellationToken ct) {
        var onPath = await probe.KcapOnPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
        return onPath == true
            ? new ShimResult(ShimOutcome.Installed, null, null)
            : new ShimResult(ShimOutcome.InstalledButNotOnPath,
                $"kcap was linked at {destination}, but your login shell's PATH doesn't include /usr/local/bin. Add: export PATH=\"/usr/local/bin:$PATH\"",
                null);
    }

    /// lstat taxonomy on `destination`, never following through a foreign link: absent →
    /// Installable; a symlink resolving (through any chain) to `target` → AlreadyInstalled;
    /// anything else — foreign symlink, broken symlink, regular file, directory — → Conflict.
    /// LinkTarget/ResolveLinkTarget never follow past the top-level lstat on their own, so a
    /// regular file's LinkTarget is null (not "not a link" vs "absent" — both null) and only
    /// FileInfo.Exists/Directory.Exists distinguish those two afterwards.
    internal static ShimPreflight Preflight(string destination, string target) {
        var info = new FileInfo(destination);

        if (info.LinkTarget is not null) {
            var resolved = TryResolveFinalTarget(info);
            if (resolved is null || !resolved.Exists) return ShimPreflight.Conflict; // broken link
            return string.Equals(resolved.FullName, Path.GetFullPath(target), StringComparison.Ordinal)
                ? ShimPreflight.AlreadyInstalled
                : ShimPreflight.Conflict;
        }

        if (info.Exists) return ShimPreflight.Conflict; // regular file
        if (Directory.Exists(destination)) return ShimPreflight.Conflict;
        return ShimPreflight.Installable;
    }

    // Broad catch: an unresolvable link (broken, too-deep, permission-denied) all read the same
    // way here — never overwrite something we can't positively identify as the target.
    static FileSystemInfo? TryResolveFinalTarget(FileInfo info) {
        try {
            return info.ResolveLinkTarget(returnFinalTarget: true);
        } catch (Exception) {
            return null;
        }
    }

    static string Describe(string destination) {
        var info = new FileInfo(destination);
        if (info.LinkTarget is { } linkTarget) return $"a symlink to {linkTarget}";
        if (info.Exists) return "a regular file";
        if (Directory.Exists(destination)) return "a directory";
        return "an unexpected filesystem entry";
    }

    /// argv for `osascript`: the target is passed as an <c>-- &lt;target&gt;</c> argv element and read back
    /// via `quoted form of item 1 of argv` — never string-interpolated into the script source, so
    /// spaces/quotes/backslashes/etc. in the path can't break out of the shell command. `ln -s` is
    /// non-forcing: a race lands as a failed creation, never a clobber.
    internal static string[] OsascriptArgs(string target) => [
        "-e", "on run argv",
        "-e", "do shell script \"mkdir -p /usr/local/bin && ln -s \" & quoted form of item 1 of argv & \" /usr/local/bin/kcap\" with administrator privileges",
        "-e", "end run",
        "--", target,
    ];

    /// POSIX single-quote escaping for the copyable sudo fallback shown on failure: close the
    /// quote, emit an escaped literal quote, reopen it.
    internal static string PosixQuote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    /// CR/LF would either break the single-line sudo fallback or (via osascript's argv) desync
    /// script and data; rejected before any prompt.
    internal static bool LooksLikeTarget(string s) => !s.Contains('\n') && !s.Contains('\r');
}
