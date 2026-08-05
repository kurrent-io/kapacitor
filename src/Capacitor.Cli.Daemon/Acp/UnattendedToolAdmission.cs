using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Decides whether ONE unattended permission frame names only tools this launch itself injected.
///
/// <para><b>Why this exists.</b> The <c>Fail</c> policy assumes a correctly-configured reviewer emits
/// no interaction frame at all. Measured against kiro-cli 2.16.0 that assumption is false: a frame
/// appears intermittently for <c>@kcap-flow-result/submit_review_result</c> — a tool that IS in the
/// launch's <c>--trust-tools</c> — so <c>Fail</c> reaps an unpredictable share of otherwise-clean
/// rounds on the very call that delivers the result. <c>AutoApprove</c> is not the answer either: it
/// does not inspect the tool, so it would approve exactly the request that scoping exists to reject.
/// This is the third option — approve the launch's own tools, treat everything else as <c>Fail</c>
/// does.</para>
///
/// <para><b>Matching is on a DISPLAY STRING, and that needs justifying rather than hiding.</b> Kiro's
/// permission frame carries no structured tool identity: the whole of <c>toolCall</c> is
/// <c>{toolCallId, title}</c>, where title reads <c>"Running: @server/tool"</c>. Keying a security
/// decision off presentation text would normally be indefensible — a spelling is not an identity.
/// What makes it acceptable HERE, and only here, is that the admitted names are per-launch aliases
/// carrying an unguessable GUID (<see cref="LaunchIdentity"/>). The reviewed repository cannot author
/// content that matches one, because it cannot predict one. Remove the aliasing and this becomes
/// string classification again, so the two are a package.</para>
///
/// <para><b>Fail-closed in every direction.</b> A frame is admitted only when at least one
/// <c>@server/tool</c> token is present AND every token found is admitted. No tokens (a bare shell
/// string, an empty title, a shape we do not recognise) is a denial, not a pass — otherwise the
/// easiest way past this gate would be to name no tool at all.</para>
/// </summary>
internal static class UnattendedToolAdmission {
    /// <summary>Every <c>@server/tool</c> occurrence, wherever it sits in the string. Deliberately not
    /// anchored to a <c>"Running: "</c> prefix: that prefix is vendor presentation and would silently
    /// stop matching if it changed, and an unanchored scan is the stricter reading anyway — it finds
    /// tokens a prefix-anchored one would skip past.</summary>
    static readonly Regex ToolToken = new(@"@([^\s/]+)/([^\s,;)\]]+)", RegexOptions.Compiled);

    /// <summary>The admitted <c>@server/tool</c> identities for a launch, built from the SAME injected
    /// spec list and identity the trust argv is built from. One derivation: a second would admit a set
    /// that does not match what was actually injected.</summary>
    internal static IReadOnlySet<string> AdmittedFor(
            IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) =>
        KiroReviewerTrustList.NamespacedEntries(injected, identity).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when this frame names only admitted tools. <paramref name="toolCall"/> is the raw
    /// <c>toolCall</c> object from the request.
    /// </summary>
    internal static bool IsAdmitted(JsonElement toolCall, IReadOnlySet<string> admitted) {
        if (admitted.Count == 0) return false;

        var title = toolCall.ValueKind == JsonValueKind.Object
                 && toolCall.TryGetProperty("title", out var t)
                 && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

        if (title is not { Length: > 0 }) return false;

        var matches = ToolToken.Matches(title);
        if (matches.Count == 0) return false;

        foreach (Match m in matches) {
            if (!admitted.Contains($"@{m.Groups[1].Value}/{m.Groups[2].Value}"))
                return false;
        }

        return true;
    }
}
