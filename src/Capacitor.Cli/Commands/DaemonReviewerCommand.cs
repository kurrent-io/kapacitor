using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap daemon reviewer affirm --vendor &lt;kiro|gemini&gt;</c> — the operator's explicit
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

        Console.WriteLine(
            previous is null
                ? $"Affirmed {reviewer.DefaultBinary} {installed} for daemon '{name}' (no previous affirmation)."
                : previous == installed
                    ? $"{reviewer.DefaultBinary} {installed} was already affirmed for daemon '{name}'."
                    : $"Affirmed {reviewer.DefaultBinary} {installed} for daemon '{name}' (was {previous}).");

        Console.WriteLine("Restart the daemon for a running instance to pick this up.");

        return Task.FromResult(0);
    }

    /// <summary>
    /// A reviewer whose build an operator can affirm. Both gated reviewers use the same model, so the
    /// verb is a table rather than a per-vendor branch — a third one is a row here, not a new arm.
    /// </summary>
    /// <param name="Vendor">Canonical vendor token, and the key the daemon's store is written under.</param>
    /// <param name="DefaultBinary">Binary probed when the path env var is unset.</param>
    /// <param name="PathEnvVar">Env var the daemon itself honours for this vendor's binary — read here
    /// so the verb affirms the build the DAEMON would launch, not whatever happens to be first on PATH.</param>
    /// <param name="EnableEnvVar">The consent flag, named in usage text because affirming is not enabling.</param>
    internal sealed record AffirmableReviewer(
            string Vendor, string DefaultBinary, string PathEnvVar, string EnableEnvVar) {
        internal static readonly AffirmableReviewer[] All = [
            new("kiro",   "kiro-cli", "KCAP_KIRO_PATH",   "KCAP_KIRO_UNATTENDED_REVIEWER"),
            new("gemini", "gemini",   "KCAP_GEMINI_PATH", "KCAP_GEMINI_UNATTENDED_REVIEWER")
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

              Records the installed vendor version as reviewed by you, clearing the fail-closed gate
              that a version change raises. Does NOT enable the unattended reviewer — set
              {string.Join(" / ", AffirmableReviewer.All.Select(r => r.EnableEnvVar))}
              for that, and read what it grants first.
            """);

        return 1;
    }
}
