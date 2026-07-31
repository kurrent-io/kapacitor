using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal sealed record LaunchConsentPromptRequest(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds);

/// Implemented by LaunchConsentBroker (Task 6). Null answer = timeout / subscriber vanished.
internal interface ILaunchConsentPrompter {
    bool HasSubscriber { get; }
    Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct);
}

internal sealed class LaunchConsentGate(
    LaunchConsentStore store,
    LaunchConsentDecisionLog log,
    ILaunchConsentPrompter? prompter,
    ILogger<LaunchConsentGate> logger) {

    public const string DeniedReasonPrefix = "launch_denied_by_owner";

    public async Task<LaunchConsentOutcome> DecideAsync(string agentId, LaunchConsentInput input, CancellationToken ct) {
        var policy = store.Current;
        var decision = LaunchConsentEngine.Evaluate(policy, input);

        if (decision.Verdict is LaunchConsentVerdict.Allow)
            return Done(agentId, input, allowed: true, source: decision.Source, detail: "allowed by daemon owner policy");
        if (decision.Verdict is LaunchConsentVerdict.Deny)
            return Done(agentId, input, allowed: false, source: decision.Source, detail: "denied by daemon owner policy");

        if (prompter is not { HasSubscriber: true })
            return Done(agentId, input, allowed: false, source: "prompt_no_ui",
                detail: "owner approval required and no approval UI is attached to this daemon");

        var req = new LaunchConsentPromptRequest(agentId, input.RequesterUserId, input.Kind,
            input.RepoPath, input.Vendor, DateTimeOffset.UtcNow.ToString("O"), policy.PromptTimeoutSeconds);
        logger.LogInformation("Launch {AgentId} awaiting owner consent (timeout {Timeout}s)", agentId, req.TimeoutSeconds);
        var answer = await prompter.PromptAsync(req, TimeSpan.FromSeconds(policy.PromptTimeoutSeconds), ct);
        return answer switch {
            true  => Done(agentId, input, allowed: true,  source: "prompt_user", detail: "approved by daemon owner"),
            false => Done(agentId, input, allowed: false, source: "prompt_user", detail: "declined by daemon owner"),
            null  => Done(agentId, input, allowed: false, source: "prompt_timeout",
                          detail: $"owner did not respond within {policy.PromptTimeoutSeconds}s"),
        };
    }

    LaunchConsentOutcome Done(string agentId, in LaunchConsentInput input, bool allowed, string source, string detail) {
        log.Record(new LaunchConsentRecord(
            DateTimeOffset.UtcNow.ToString("O"), agentId, input.RequesterUserId, input.RequesterIsOwner,
            input.Kind, input.RepoPath, input.Vendor, allowed ? "allowed" : "denied", source));
        return new LaunchConsentOutcome(allowed, source, detail);
    }
}

internal readonly record struct LaunchConsentOutcome(bool Allowed, string Source, string Detail);
