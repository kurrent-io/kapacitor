using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap daemon reviewer affirm --vendor kiro</c> — the operator's explicit acknowledgement that
/// the installed vendor build may host an unattended reviewer on this daemon.
///
/// <para><b>Why a command and not a config key.</b> The record it writes is what makes a vendor
/// upgrade fail closed. A value the operator could set from a shell profile would be re-affirmed by
/// their dotfiles rather than by them, which is the same "consent that isn't consent" failure the
/// enable flag avoids — so the only way to clear the gate is to run this, deliberately, after
/// looking at what changed.</para>
///
/// <para>Deliberately does NOT enable the reviewer. Affirming a build and consenting to unattended
/// review are separate decisions, and collapsing them would let an upgrade acknowledgement silently
/// turn the feature on.</para>
/// </summary>
public static class DaemonReviewerCommand {
    public static Task<int> HandleAsync(string[] args) {
        if (args.Length == 0 || args[0] != "affirm")
            return Task.FromResult(Usage());

        var vendor = ValueOf(args, "--vendor");

        if (!string.Equals(vendor, "kiro", StringComparison.OrdinalIgnoreCase)) {
            Console.Error.WriteLine(
                vendor is null
                    ? "kcap daemon reviewer affirm requires --vendor."
                    : $"Unknown reviewer vendor '{vendor}'. Only 'kiro' uses a version affirmation today "
                    + "(Gemini's reviewer is gated on a maintainer-certified version set instead).");

            return Task.FromResult(1);
        }

        var name     = DaemonNameResolver.Resolve(args, AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);
        // The same {DaemonLockPaths.Directory}/{name} shape the daemon resolves for its own state
        // (consent, decisions). DaemonConfig.StateDir has no profile or env binding, so the default
        // root is the only one a running daemon can be using.
        var stateDir = Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(name));

        var binary   = Environment.GetEnvironmentVariable("KCAP_KIRO_PATH") is { Length: > 0 } configured
            ? configured
            : "kiro-cli";

        var installed = VendorVersionResolver.Resolve(binary);

        if (installed is null) {
            Console.Error.WriteLine(
                $"Could not determine the installed version of '{binary}'. A build that cannot be "
              + "identified is not affirmable — check that kiro-cli is on PATH (or set KCAP_KIRO_PATH) "
              + "and that `kiro-cli --version` succeeds.");

            return Task.FromResult(1);
        }

        var store    = new KiroReviewerVersionStore(stateDir);
        var previous = store.Affirmed;
        store.Affirm(installed);

        Console.WriteLine(
            previous is null
                ? $"Affirmed kiro-cli {installed} for daemon '{name}' (no previous affirmation)."
                : previous == installed
                    ? $"kiro-cli {installed} was already affirmed for daemon '{name}'."
                    : $"Affirmed kiro-cli {installed} for daemon '{name}' (was {previous}).");

        Console.WriteLine("Restart the daemon for a running instance to pick this up.");

        return Task.FromResult(0);
    }

    static string? ValueOf(string[] args, string flag) {
        var i = Array.IndexOf(args, flag);

        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
    }

    static int Usage() {
        Console.Error.WriteLine("""
            Usage: kcap daemon reviewer affirm --vendor kiro [--name <daemon>]

              Records the installed kiro-cli version as reviewed by you, clearing the fail-closed
              gate that a version change raises. Does NOT enable the unattended reviewer — set
              KCAP_KIRO_UNATTENDED_REVIEWER for that, and read what it grants first.
            """);

        return 1;
    }
}
