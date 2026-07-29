using System.Collections.Immutable;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Supplies a borrowed reviewer's vendor credential from the daemon's OWN environment, so the
/// sandbox never has to hand the reviewer the user's keychain.
///
/// <para>The keychain is how the vendor authenticates by default, and granting it meant a recursive,
/// credential-bearing tree reachable with <b>no</b> ACP interaction frame — so the <c>Fail</c> policy
/// never fired and only the OS boundary stood there. It was the largest hole the profile had left.</para>
///
/// <para>The daemon does not ACQUIRE credentials: no keychain read, no shelling out to
/// <c>gh auth token</c>, no prompt, no cache, no persistence — any of which would make it a
/// credential-handling component, which was not worth taking as a side effect of closing a sandbox
/// grant. It forwards a token the operator already placed in its environment; with none there,
/// borrowed review is not offered at all (see <see cref="CopilotBorrowedReviewPolicy.Resolve"/>).
/// Env-only, with no <c>DaemonConfig</c> field, because a config key invites a long-lived token on
/// disk — worse than the grant it replaces.</para>
/// </summary>
internal static class BorrowedReviewAuthBroker {
    /// <summary>Where a token is read FROM, in precedence order — the same order the vendor itself
    /// applies, so brokering cannot select a different credential than an unsandboxed run would.</summary>
    internal static readonly ImmutableArray<string> SourceVariables =
        ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    /// <summary>Where the resolved token is written TO on the child. Verified live: this takes
    /// precedence over stored credentials, which is what makes the keychain grant removable.</summary>
    internal const string TargetVariable = "COPILOT_GITHUB_TOKEN";

    /// <summary>A command that PRINTS a token, for a daemon whose environment cannot safely hold one.
    ///
    /// <para>This is the supervised-daemon path. A service unit is a file on disk, so the token itself
    /// must not live there — the unit carries this command instead, which is not a secret, and the value
    /// is produced when needed and never persisted. See
    /// <see cref="BorrowedReviewTokenCommand"/>.</para></summary>
    internal const string CommandVariable = "KCAP_COPILOT_TOKEN_CMD";

    /// <summary>Whether this daemon can broker a token at all, consumed at policy resolution so an
    /// unbrokerable daemon advertises no borrowed review rather than advertising it and failing at
    /// spawn.
    ///
    /// <para>Probed ONCE, at startup. For a directly-set variable that is exact — a process's
    /// environment does not change under it. For a <see cref="CommandVariable"/> it is a probe: the
    /// command ran successfully once, so the configuration is real, and the value is discarded and
    /// re-resolved per launch. A command that works at boot and breaks later still fails at spawn,
    /// which no advertisement-time check can prevent; what this does remove from the mid-launch path is
    /// the common case, a command that never worked.</para></summary>
    internal static bool Available { get; } = TryResolve(Environment.GetEnvironmentVariable) is not null;

    /// <summary>The token, or null when none is configured.
    ///
    /// <para>Directly-set variables win over the command: they are unambiguous and free, and this keeps
    /// the pre-existing behaviour of a self-run daemon exactly as it was. The command is the fallback
    /// for the case a variable cannot serve.</para></summary>
    /// <param name="readVariable">Environment reader. Production passes
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>; tests pass a fake so the resolution
    /// order is assertable without mutating the test process's own environment.</param>
    /// <param name="runCommand">Token-command runner. Production passes null and gets
    /// <see cref="BorrowedReviewTokenCommand.Run"/>; tests pass a fake so precedence and the
    /// blank/failure cases are assertable without spawning a shell.</param>
    internal static string? TryResolve(
            Func<string, string?> readVariable, Func<string, string?>? runCommand = null) {
        foreach (var name in SourceVariables) {
            var value = readVariable(name);

            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        var commandLine = readVariable(CommandVariable);

        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var produced = (runCommand ?? BorrowedReviewTokenCommand.Run)(commandLine);

        return string.IsNullOrWhiteSpace(produced) ? null : produced;
    }
}
