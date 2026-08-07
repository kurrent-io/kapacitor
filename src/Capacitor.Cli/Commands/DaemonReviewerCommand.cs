using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap daemon reviewer affirm --vendor &lt;kiro|gemini|antigravity&gt;</c> — the operator's explicit
/// acknowledgement that the installed vendor build may host an unattended reviewer on this daemon.
///
/// <para><b>Why a command and not a config key.</b> The record it writes is what makes a vendor
/// upgrade fail closed. A value the operator could set from a shell profile would be re-affirmed by
/// their dotfiles rather than by them, which is the same "consent that isn't consent" failure the
/// enable flag avoids — so the only way to clear the gate is to run this, deliberately, after
/// looking at what changed.</para>
///
/// <para><b>Not a security boundary.</b> Anyone who can delete the record can also set the enable
/// variable and restart, and both live under the daemon user's own authority — so this stops an
/// unnoticed vendor auto-update from carrying consent forward, not a local attacker.</para>
///
/// <para>Deliberately does NOT enable the reviewer. Affirming a build and consenting to unattended
/// review are separate decisions, and collapsing them would let an upgrade acknowledgement silently
/// turn the feature on.</para>
/// </summary>
public static class DaemonReviewerCommand {
    public static Task<int> HandleAsync(string[] args) {
        if (args.Length == 0 || args[0] != "affirm")
            return Task.FromResult(Usage());

        var requested = ValueOf(args, "--vendor");

        if (AffirmableReviewer.Resolve(requested) is not { } reviewer) {
            Console.Error.WriteLine(
                requested is null
                    ? $"kcap daemon reviewer affirm requires --vendor ({AffirmableReviewer.VendorList})."
                    : $"Unknown reviewer vendor '{requested}'. Affirmable reviewers: "
                    + $"{AffirmableReviewer.VendorList}.");

            return Task.FromResult(1);
        }

        var name     = DaemonNameResolver.Resolve(args, AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);
        // The same {DaemonLockPaths.Directory}/{name} shape the daemon resolves for its own state
        // (consent, decisions). DaemonConfig.StateDir has no profile or env binding, so the default
        // root is the only one a running daemon can be using.
        var stateDir = Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(name));

        var binary   = Environment.GetEnvironmentVariable(reviewer.PathEnvVar) is { Length: > 0 } configured
            ? configured
            : reviewer.DefaultBinary;

        var installed = VendorVersionResolver.Resolve(binary);

        if (installed is null) {
            Console.Error.WriteLine(
                $"Could not determine the installed version of '{binary}'. A build that cannot be "
              + $"identified is not affirmable — check that {reviewer.DefaultBinary} is on PATH (or set "
              + $"{reviewer.PathEnvVar}) and that `{reviewer.DefaultBinary} --version` succeeds.");

            return Task.FromResult(1);
        }

        var store    = new ReviewerVersionStore(stateDir, reviewer.Vendor);
        var previous = store.Affirmed;
        store.Affirm(installed);

        // The recorded value is a MINIMUM, so this verb can move it DOWN as well as up — affirming
        // while an older build is installed deliberately re-admits that build and everything above
        // it. Say which direction it moved rather than a neutral "(was X)": lowering a floor is a
        // security-relevant act and should not read identically to raising one.
        // Three-way, not two: when either side does not order as a version there is no direction to
        // report, and claiming "Raised" would be a statement we did not compute — the same
        // across-domains mistake the Incomparable arm exists to avoid in the gate itself.
        var direction =
            ReviewerVersionAffirmations.TryParseVersion(installed) is { } now
         && ReviewerVersionAffirmations.TryParseVersion(previous) is { } before
                ? now < before ? "lowered" : "raised"
                : "unknown";

        Console.WriteLine(
            previous is null
                ? $"Recorded {reviewer.DefaultBinary} {installed} as the minimum for daemon '{name}' (none was set)."
                : previous == installed
                    ? $"{reviewer.DefaultBinary} {installed} is already the minimum for daemon '{name}'."
                    : direction switch {
                        "lowered" =>
                            $"LOWERED the minimum for daemon '{name}' to {reviewer.DefaultBinary} {installed} "
                          + $"(was {previous}) — builds from {installed} up are now admitted again.",
                        "raised" =>
                            $"Raised the minimum for daemon '{name}' to {reviewer.DefaultBinary} {installed} "
                          + $"(was {previous}).",
                        _ =>
                            $"Set the minimum for daemon '{name}' to {reviewer.DefaultBinary} {installed} "
                          + $"(was {previous}); the two do not order as version numbers, so this may have "
                          + "raised or lowered it."
                    });

        Console.WriteLine("Restart the daemon for a running instance to pick this up.");

        return Task.FromResult(0);
    }

    /// <summary>
    /// A reviewer whose build an operator can affirm. Every gated reviewer uses the same model, so the
    /// verb is a table rather than a per-vendor branch — the next one is a row here, not a new arm.
    /// </summary>
    /// <param name="Vendor">Canonical vendor token, and the key the daemon's store is written under.</param>
    /// <param name="DefaultBinary">Binary probed when the path env var is unset.</param>
    /// <param name="PathEnvVar">Env var the daemon itself honours for this vendor's binary — read here
    /// so the verb affirms the build the DAEMON would launch, not whatever happens to be first on PATH.</param>
    /// <param name="EnableEnvVar">The consent flag, named in usage text because affirming is not enabling.</param>
    internal sealed record AffirmableReviewer(
            string Vendor, string DefaultBinary, string PathEnvVar, string EnableEnvVar) {
        internal static readonly AffirmableReviewer[] All = [
            new("kiro",        "kiro-cli", "KCAP_KIRO_PATH",        "KCAP_KIRO_UNATTENDED_REVIEWER"),
            new("gemini",      "gemini",   "KCAP_GEMINI_PATH",      "KCAP_GEMINI_UNATTENDED_REVIEWER"),
            new("antigravity", "agy",      "KCAP_ANTIGRAVITY_PATH", "KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER")
        ];

        internal static string VendorList => string.Join(" | ", All.Select(r => r.Vendor));

        internal static AffirmableReviewer? Resolve(string? vendor) =>
            All.FirstOrDefault(r => string.Equals(r.Vendor, vendor, StringComparison.OrdinalIgnoreCase));
    }

    static string? ValueOf(string[] args, string flag) {
        var i = Array.IndexOf(args, flag);

        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
    }

    static int Usage() {
        Console.Error.WriteLine($"""
            Usage: kcap daemon reviewer affirm --vendor <{AffirmableReviewer.VendorList}> [--name <daemon>]

              Records the installed vendor version as the MINIMUM this daemon will run. Any build at
              or above it is admitted, so a vendor upgrade needs no action from you; an older one is
              refused. Run this to move the minimum to whatever is installed now — which is how you
              exclude a build you have found to be broken, and, if you run it while an OLDER build is
              installed, how you deliberately lower the bar again.

              Does NOT enable the unattended reviewer — set
              {string.Join(" / ", AffirmableReviewer.All.Select(r => r.EnableEnvVar))}
              for that, and read what it grants first.
            """);

        return 1;
    }
}
