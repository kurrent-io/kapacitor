using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// The launch-time permission-preset arm of <see cref="AcpInteractionBridge"/>: auto-approve a
/// request whose ACP tool kind the preset covers with a single unambiguous <c>allow_once</c>, and
/// FORWARD (prompt) everything else — an uncovered/absent/unknown kind, zero/multiple
/// <c>allow_once</c>, or a sole <c>allow_always</c>. The preset can only ever replace a prompt with an
/// allow; it never cancels. Fires the fire-and-forget audit exactly on auto-approve, never on forward.
/// </summary>
public class AcpInteractionBridgePresetTests {
    const string AgentId      = "agent-1";
    const string AcpSessionId = "fc2e09cf-f4b0-4463-9dc1-bda11268896b";

    static JsonElement Frame(string? toolKind, params (string Id, string OptKind)[] options) {
        var optsJson = string.Join(",", options.Select(o => $$"""{"optionId":"{{o.Id}}","name":"{{o.Id}}","kind":"{{o.OptKind}}"}"""));
        var kindProp = toolKind is null ? "" : $",\"kind\":\"{toolKind}\"";
        var json     = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"{{kindProp}}},"options":[{{optsJson}}]}""";

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    static AcpLaunchPermissionPreset Preset(string token) {
        AcpPermissionPresets.TryResolve(token, out var p);

        return p!;
    }

    /// <summary>Builds a bridge whose interactive delegate records that it was invoked (a "forward")
    /// and returns a cancel; the audit sink records notices.</summary>
    static (AcpInteractionBridge Bridge, Func<bool> Forwarded, Func<List<AcpAutoApprovalNotice>> Notices) Build(
            string presetToken, AcpUnattendedInteractionPolicy policy = AcpUnattendedInteractionPolicy.Disabled) {
        var forwarded = false;
        var notices   = new List<AcpAutoApprovalNotice>();

        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { forwarded = true; return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance,
            unattendedPolicy: policy,
            preset: Preset(presetToken),
            notifyAutoApproval: notices.Add);

        return (bridge, () => forwarded, () => notices);
    }

    static async Task<string?> OutcomeOf(AcpInteractionBridge bridge, JsonElement paramsElement) {
        var result = await bridge.HandleAsync(new AcpRequest(1, "session/request_permission", paramsElement), CancellationToken.None);

        return result!.Value.GetProperty("outcome").GetProperty("outcome").GetString();
    }

    [Test]
    [Arguments(AcpPermissionPresets.Explore, "read")]
    [Arguments(AcpPermissionPresets.Explore, "search")]
    [Arguments(AcpPermissionPresets.Edit, "read")]
    [Arguments(AcpPermissionPresets.Edit, "edit")]
    [Arguments(AcpPermissionPresets.Edit, "move")]
    [Arguments(AcpPermissionPresets.Edit, "delete")]
    public async Task Covered_kind_with_single_allow_once_is_auto_approved(string preset, string kind) {
        var (bridge, forwarded, notices) = Build(preset);

        var outcome = await OutcomeOf(bridge, Frame(kind, ("allow-once", "allow_once"), ("deny", "deny")));

        await Assert.That(outcome).IsEqualTo("selected");
        await Assert.That(forwarded()).IsFalse();               // never reached the human
        await Assert.That(notices()).HasSingleItem();           // audit fired exactly once
        await Assert.That(notices()[0].Preset).IsEqualTo(preset);
        await Assert.That(notices()[0].ToolKind).IsEqualTo(kind);
    }

    [Test]
    [Arguments("execute")]
    [Arguments("fetch")]
    [Arguments("think")]
    [Arguments("switch_mode")]
    [Arguments("other")]
    [Arguments("wat")] // unrecognised kind
    public async Task Uncovered_or_unknown_kind_is_forwarded_not_approved(string kind) {
        var (bridge, forwarded, notices) = Build(AcpPermissionPresets.Edit);

        var outcome = await OutcomeOf(bridge, Frame(kind, ("allow-once", "allow_once"), ("deny", "deny")));

        await Assert.That(forwarded()).IsTrue();                // reached the human
        await Assert.That(notices()).IsEmpty();                 // no auto-approval audit
        await Assert.That(outcome).IsEqualTo("cancelled");      // the forwarded decision mapped through
    }

    [Test]
    public async Task Kind_less_frame_is_forwarded() {
        // A kiro-cli-shaped frame carries {toolCallId, title} and no kind — it must never match a preset.
        var (bridge, forwarded, notices) = Build(AcpPermissionPresets.Explore);

        await OutcomeOf(bridge, Frame(toolKind: null, ("allow-once", "allow_once"), ("deny", "deny")));

        await Assert.That(forwarded()).IsTrue();
        await Assert.That(notices()).IsEmpty();
    }

    [Test]
    public async Task Sole_allow_always_is_forwarded_never_a_standing_grant() {
        var (bridge, forwarded, notices) = Build(AcpPermissionPresets.Explore);

        await OutcomeOf(bridge, Frame("read", ("allow-always", "allow_always"), ("deny", "deny")));

        await Assert.That(forwarded()).IsTrue();                // fell through to the human
        await Assert.That(notices()).IsEmpty();                 // never auto-approved a standing grant
    }

    [Test]
    public async Task Two_allow_once_options_are_ambiguous_and_forwarded() {
        var (bridge, forwarded, notices) = Build(AcpPermissionPresets.Explore);

        await OutcomeOf(bridge, Frame("read", ("allow-a", "allow_once"), ("allow-b", "allow_once")));

        await Assert.That(forwarded()).IsTrue();
        await Assert.That(notices()).IsEmpty();
    }

    [Test]
    public async Task Preset_is_ignored_outside_the_interactive_disabled_policy() {
        // Under a review-flow unattended policy the preset arm must never run (it would double up with
        // the unattended auto-approve) — the audit sink stays empty.
        var (bridge, _, notices) = Build(AcpPermissionPresets.Explore, AcpUnattendedInteractionPolicy.AutoApprove);

        await OutcomeOf(bridge, Frame("read", ("allow-once", "allow_once"), ("deny", "deny")));

        await Assert.That(notices()).IsEmpty();
    }

    [Test]
    public async Task A_throwing_audit_sink_does_not_affect_the_returned_selection() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance,
            preset: Preset(AcpPermissionPresets.Explore),
            notifyAutoApproval: _ => throw new InvalidOperationException("boom"));

        var outcome = await OutcomeOf(bridge, Frame("read", ("allow-once", "allow_once"), ("deny", "deny")));

        await Assert.That(outcome).IsEqualTo("selected"); // the approval stands despite the throwing sink
    }
}
