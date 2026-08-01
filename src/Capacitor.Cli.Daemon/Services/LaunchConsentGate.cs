using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal sealed record LaunchConsentPromptRequest(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds);

/// Implemented by LaunchConsentBroker (Task 6). Null answer = timeout / subscriber vanished.
internal interface ILaunchConsentPrompter {
    bool HasSubscriber { get; }
    Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct);
    Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct);
}

internal sealed class LaunchConsentGate(
    LaunchConsentStore store,
    LaunchConsentDecisionLog log,
    ILaunchConsentPrompter? prompter,
    TimeProvider time,
    ILogger<LaunchConsentGate> logger) {

    public const string DeniedReasonPrefix = "launch_denied_by_owner";

    public async Task<LaunchConsentOutcome> DecideAsync(string agentId, LaunchConsentInput input, CancellationToken ct) {
        var policy = store.Current;
        var decision = LaunchConsentEngine.Evaluate(policy, input);

        if (decision.Verdict is LaunchConsentVerdict.Allow)
            return Done(agentId, input, allowed: true, source: decision.Source, detail: "allowed by daemon owner policy");
        if (decision.Verdict is LaunchConsentVerdict.Deny)
            return Done(agentId, input, allowed: false, source: decision.Source, detail: "denied by daemon owner policy");

        if (prompter is null)
            return Done(agentId, input, allowed: false, source: "prompt_no_ui",
                detail: "owner approval required and no approval UI is attached to this daemon");

        // Deadline discipline (spec §3.2): one monotonic anchor for the whole prompt path. Every
        // wait duration below is computed immediately before waiting from Remaining() — never
        // from a stored/accumulated elapsed value — so setup/scheduling time between anchoring
        // and waiting can only shrink a wait, never push it past the deadline. `ct` firing below
        // (WaitForSubscriberAsync or PromptAsync) propagates OperationCanceledException uncaught
        // — deliberately not handled here: external cancellation must abort the launch without
        // fabricating a decision, so Done()/the decision log is never reached for that case
        // (spec §3.2 "Cancellation").
        var timeout     = TimeSpan.FromSeconds(policy.PromptTimeoutSeconds);
        var start       = time.GetTimestamp();           // monotonic anchor; the ONE deadline
        var requestedAt = time.GetUtcNow().ToString("O"); // countdown metadata anchors here too
        TimeSpan Remaining() {
            var left = timeout - time.GetElapsedTime(start);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        var grace = TimeSpan.FromSeconds(Math.Min(5, policy.PromptTimeoutSeconds));
        var wait  = grace < Remaining() ? grace : Remaining(); // computed immediately before waiting
        if (!await prompter.WaitForSubscriberAsync(wait, time, ct))
            return Done(agentId, input, allowed: false, source: "prompt_no_ui",
                detail: $"owner approval required and no approval UI attached within {(int)wait.TotalSeconds}s grace");

        var req = new LaunchConsentPromptRequest(agentId, input.RequesterUserId, input.Kind,
            input.RepoPath, input.Vendor, requestedAt, policy.PromptTimeoutSeconds);
        logger.LogInformation("Launch {AgentId} awaiting owner consent (timeout {Timeout}s)", agentId, req.TimeoutSeconds);
        // Recomputed (zero allowed — a subscriber arriving exactly at the deadline still gets a
        // single fail-closed settlement via PromptAsync's own timeout, never a special case here).
        var answer = await prompter.PromptAsync(req, Remaining(), time, ct);
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
