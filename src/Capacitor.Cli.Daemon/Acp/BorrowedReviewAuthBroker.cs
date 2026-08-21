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
/// <para>The daemon never goes LOOKING for a credential: no keychain read, no prompt, no cache, no
/// persistence, and no default command. It forwards a token the operator placed in its environment,
/// or — for a supervised daemon, whose unit file must not hold a secret — runs the one command the
/// operator configured in <see cref="CommandVariable"/>, and only when an actual borrowed launch needs
/// it. With neither configured, borrowed review is not offered at all (see
/// <see cref="CopilotBorrowedReviewPolicy.Resolve"/>).</para>
///
/// <para>No <c>DaemonConfig</c> field, because a config key invites a long-lived token on disk — worse
/// than the grant it replaces.</para>
/// </summary>
internal static class BorrowedReviewAuthBroker {
    /// <summary>Where a token is read FROM, in precedence order — the same order the vendor itself
    /// applies, so brokering cannot select a different credential than an unsandboxed run would.</summary>
    internal static readonly ImmutableArray<string> SourceVariables =
        ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    /// <summary>Where the resolved token is written TO on the child. Verified live: this takes
    /// precedence over stored credentials, which is what makes the keychain grant removable.</summary>
    internal const string TargetVariable = "COPILOT_GITHUB_TOKEN";

    /// <summary>A command that PRINTS a token — the supervised-daemon path, since a service unit is a
    /// file on disk and may carry the command but not the credential. See
    /// <see cref="BorrowedReviewTokenCommand"/>.</summary>
    internal const string CommandVariable = "KCAP_COPILOT_TOKEN_CMD";

    /// <summary>Whether a token source is CONFIGURED, consumed at policy resolution so a daemon with no
    /// source advertises no borrowed review instead of failing at spawn.
    ///
    /// <para>Deliberately passive — it does not run the command. An earlier revision probed by executing
    /// it once at startup, to move a misconfigured command from a mid-launch failure to a
    /// non-advertisement. That bought a better diagnostic by having the daemon mint a credential nobody
    /// had asked for, on every start, even where borrowed review was never used — which is precisely the
    /// posture this class exists to avoid. A command that is configured but broken now surfaces at spawn
    /// as <c>borrowed_review_auth_unavailable</c>, the same coded, honest failure an unset variable
    /// already produced.</para></summary>
    internal static bool Available => IsConfigured(Environment.GetEnvironmentVariable);

    /// <summary>Whether any token source is configured, without consulting one.</summary>
    internal static bool IsConfigured(Func<string, string?> readVariable) {
        foreach (var name in SourceVariables)
            if (!string.IsNullOrWhiteSpace(readVariable(name))) return true;

        return !string.IsNullOrWhiteSpace(readVariable(CommandVariable));
    }

    /// <summary>The token, or null when none could be obtained. Runs the configured command if no
    /// variable supplies one, so this is called on a LAUNCH path, never at startup. A directly-set
    /// variable wins — unambiguous, free, and unchanged from the pre-command behaviour.</summary>
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
