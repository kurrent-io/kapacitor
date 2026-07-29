using System.Collections.Immutable;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Supplies a borrowed reviewer's vendor credential from the daemon's OWN environment, so the
/// sandbox never has to hand the reviewer the user's keychain.
///
/// <para><b>Why this exists.</b> The reviewer has to authenticate before it can review anything, and
/// the vendor's default answer is the login keychain — which the sandbox could only satisfy by
/// granting <c>~/Library/Keychains</c>. That grant is recursive and credential-bearing, and it is
/// reachable with <b>no</b> ACP interaction frame, so the <c>Fail</c> interaction policy never fires
/// and the OS boundary permits the read. It was the single largest hole left in the profile.</para>
///
/// <para><b>What was decided, and what was deliberately NOT.</b> The daemon does not acquire
/// credentials. It does not read a keychain, shell out to <c>gh auth token</c>, prompt, cache, or
/// persist anything — any of which would make it a credential-handling component, which is a
/// materially different thing to review and was not worth taking as a side effect of closing a
/// sandbox grant. It only forwards a token the operator has ALREADY placed in its environment. If
/// none is there, borrowed review is simply not offered (see
/// <see cref="CopilotBorrowedReviewPolicy.Resolve"/>) and the server answers the honest
/// <c>vendor_containment_unreadable</c> with the <c>context-only</c> remedy.</para>
///
/// <para>Deliberately env-only, with no <c>DaemonConfig</c> field: a config key invites writing a
/// long-lived token into a config file on disk, which is worse than the keychain grant it replaces.</para>
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
