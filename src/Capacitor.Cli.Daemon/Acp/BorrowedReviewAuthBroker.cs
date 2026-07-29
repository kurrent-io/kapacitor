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

    /// <summary>Whether this daemon can broker a token at all. Resolved ONCE from the daemon's own
    /// environment — a process's environment does not change under it — and consumed at policy
    /// resolution so an unbrokerable daemon advertises no borrowed review, rather than advertising it
    /// and failing at spawn. Failing at advertisement is what lets the server reject the start with a
    /// coded, actionable reason instead of the flow dying mid-launch.</summary>
    internal static bool Available { get; } = TryResolve(Environment.GetEnvironmentVariable) is not null;

    /// <summary>The token, or null when none is configured.</summary>
    /// <param name="readVariable">Environment reader. Production passes
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>; tests pass a fake so the resolution
    /// order is assertable without mutating the test process's own environment.</param>
    internal static string? TryResolve(Func<string, string?> readVariable) {
        foreach (var name in SourceVariables) {
            var value = readVariable(name);

            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}
