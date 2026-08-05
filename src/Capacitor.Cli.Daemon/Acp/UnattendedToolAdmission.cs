using System.Text.Json;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Decides whether ONE unattended permission frame names exactly one tool this launch injected.
///
/// <para><b>Why this exists.</b> The <c>Fail</c> policy assumes a correctly-configured reviewer emits
/// no interaction frame at all. Measured against kiro-cli 2.16.0 that is false: a frame appears
/// intermittently for <c>@kcap-flow-result/submit_review_result</c> — a tool that IS in the launch's
/// <c>--trust-tools</c> — so <c>Fail</c> reaps an unpredictable share of otherwise-clean rounds on
/// the very call that delivers the result. <c>AutoApprove</c> is not the answer either: it does not
/// inspect the tool, so it would approve exactly the request scoping exists to reject.</para>
///
/// <para><b>The limitation, stated plainly because an earlier revision of this file got it wrong.</b>
/// Kiro's frame carries NO structured tool identity: <c>toolCall</c> is <c>{toolCallId, title}</c>,
/// and title is presentation text. That earlier revision scanned the title for an admitted
/// <c>@server/tool</c> token and argued the per-launch unguessable alias made this safe, since the
/// reviewed repository cannot predict one. <b>That argument is wrong.</b> The MODEL knows its own
/// alias — it is in the tool list Kiro gave it — so prompt-injected repository content does not need
/// to predict the alias, only to get the model to echo it. Under a substring scan,
/// <c>"Running: execute_bash echo @alias/submit_review_result"</c> was admitted.</para>
///
/// <para><b>So the match is on the COMPLETE title, never a substring.</b> After stripping the one
/// measured presentation prefix, the remainder must equal an admitted id exactly. Surrounding text of
/// any kind is a denial. This removes the false-accept class above; what it cannot remove is the
/// residual that we are trusting Kiro's title to faithfully describe the call it is asking about,
/// because the protocol gives us nothing else to key on. A vendor title-format change makes nothing
/// match, which reaps — visibly broken, and the fail-closed direction.</para>
/// </summary>
internal static class UnattendedToolAdmission {
    /// <summary>The one presentation prefix measured on kiro-cli 2.16.0. Stripped, never matched
    /// loosely: everything after it must BE an admitted id.</summary>
    const string TitlePrefix = "Running: ";

    /// <summary>The admitted <c>@server/tool</c> identities for a launch, built from the SAME injected
    /// spec list and identity the trust argv is built from. One derivation: a second would admit a set
    /// that does not match what was actually injected.</summary>
    internal static IReadOnlySet<string> AdmittedFor(
            IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) =>
        KiroReviewerTrustList.NamespacedEntries(injected, identity).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when this frame's title is EXACTLY one admitted tool, modulo the measured prefix.
    /// <paramref name="toolCall"/> is the raw <c>toolCall</c> object from the request.
    /// </summary>
    internal static bool IsAdmitted(JsonElement toolCall, IReadOnlySet<string> admitted) {
        if (admitted.Count == 0) return false;

        var title = toolCall.ValueKind == JsonValueKind.Object
                 && toolCall.TryGetProperty("title", out var t)
                 && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

        if (title is not { Length: > 0 }) return false;

        // Ordinal throughout. A culture-aware comparison can equate strings that are different bytes,
        // which is the wrong behaviour for an identity check.
        var candidate = title.StartsWith(TitlePrefix, StringComparison.Ordinal)
            ? title[TitlePrefix.Length..]
            : title;

        return admitted.Contains(candidate);
    }
}
