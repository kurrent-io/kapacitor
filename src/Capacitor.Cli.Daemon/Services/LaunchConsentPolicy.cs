namespace Capacitor.Cli.Daemon.Services;

/// The policy the daemon enforces before any SERVER-driven launch. Launches arriving on the
/// daemon's own 0600 local socket (kcap agent start) never consult this — that socket is the
/// owner's by construction (see AgentOrchestrator.LocalIpc trust note).
internal enum LaunchConsentDefault { Allow, Deny, Prompt }

/// Null field = wildcard. Action is "allow" or "deny" (validated at the store boundary).
/// Repo uses DaemonConfig.IsRepoAllowed semantics: exact path or "/prefix/*" glob.
internal sealed record LaunchConsentRule(
    string Action,
    string? Requester,
    string? Kind,
    string? Repo,
    string? Vendor);

internal sealed record LaunchConsentPolicy(
    LaunchConsentDefault Default,
    int PromptTimeoutSeconds,
    IReadOnlyList<LaunchConsentRule> Rules)
{
    // Default allow preserves pre-consent behavior for every existing daemon on upgrade;
    // the desktop app (slice 2) flips managed daemons to Prompt during onboarding.
    public static readonly LaunchConsentPolicy UpgradeSafe = new(LaunchConsentDefault.Allow, 45, []);
}
