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

    /// <summary>A command that PRINTS a token — the supervised-daemon path, since a service unit is a
    /// file on disk and may carry the command but not the credential. See
    /// <see cref="BorrowedReviewTokenCommand"/>.</summary>
    internal const string CommandVariable = "KCAP_COPILOT_TOKEN_CMD";

    /// <summary>Whether a token can be brokered, consumed at policy resolution so an unbrokerable daemon
    /// advertises no borrowed review instead of failing at spawn.
    ///
    /// <para>Resolved once. For a variable that is exact; for a <see cref="CommandVariable"/> it is a
    /// probe — the value is discarded and re-resolved per launch, so a rotated credential stays fresh. A
    /// command that works at boot and breaks later still fails at spawn, which no advertisement-time
    /// check can prevent; this removes the common case, a command that never worked.</para></summary>
    static readonly Lazy<bool> Probe =
        new(() => TryResolve(Environment.GetEnvironmentVariable) is not null);

    internal static bool Available => Probe.Value;

    /// <summary>The token, or null when none is configured. A directly-set variable wins over the
    /// command — unambiguous, free, and unchanged from the pre-command behaviour.</summary>
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
