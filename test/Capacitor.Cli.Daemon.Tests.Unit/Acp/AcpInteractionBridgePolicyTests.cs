using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// The launched agent's own policy, evaluated at the ACP permission seam: a deny answers with the
/// agent's reject option, an allow selects the unambiguous allow_once, an ask is terminal for the
/// bridge's own layers (the preset never runs), and no decision falls through to the preset exactly
/// as before.
/// </summary>
public class AcpInteractionBridgePolicyTests {
    const string AgentId      = "agent-1";
    const string AcpSessionId = "fc2e09cf-f4b0-4463-9dc1-bda11268896b";

    const string Rules = """
        version: 1
        rules:
          - match: { kind: shell, command: "git push --force*" }
            outcome: deny
          - match: { kind: shell, command: "git status *" }
            outcome: allow
          - match: { kind: file_edit, path: "/wt/secrets*" }
            outcome: ask
        """;

    // Evaluation reads the bound document, never the Content string, so the full rules text has to
    // reach Bind — a snapshot carrying only a header would evaluate as an empty rule set.
    static PolicySnapshot Governed => new("snap-1", [
        new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", Rules,
            PolicyDocumentBinder.Bind(Rules, PolicyScope.Repo))], false, []);

    static readonly (string Id, string OptKind)[] Standard =
        [("allow-once", "allow_once"), ("allow-always", "allow_always"), ("reject-once", "reject_once")];

    static JsonElement ShellFrame(string kind, string command, params (string Id, string OptKind)[] options) =>
        Frame($$$"""{"toolCallId":"call-1","title":"Run","kind":"{{{kind}}}","rawInput":{"command":"{{{command}}}"}}""", options);

    static JsonElement PathFrame(string kind, string path, params (string Id, string OptKind)[] options) =>
        Frame($$"""{"toolCallId":"call-1","title":"Touch","kind":"{{kind}}","locations":[{"path":"{{path}}"}]}""", options);

    static JsonElement Frame(string toolCallJson, (string Id, string OptKind)[] options) {
        var optsJson = string.Join(",", options.Select(o => $$"""{"optionId":"{{o.Id}}","name":"{{o.Id}}","kind":"{{o.OptKind}}"}"""));

        return JsonDocument.Parse(
            $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{{toolCallJson}},"options":[{{optsJson}}]}""")
            .RootElement.Clone();
    }

    sealed class Harness {
        public AcpInteractionBridge         Bridge    { get; init; } = null!;
        public List<PolicyDecisionEventV1>  Decisions { get; } = [];
        public List<AcpAutoApprovalNotice>  Notices   { get; } = [];
        public bool                         Forwarded { get; set; }

        public async Task<JsonElement> HandleAsync(JsonElement paramsElement) {
            var result = await Bridge.HandleAsync(
                new AcpRequest(1, "session/request_permission", paramsElement), CancellationToken.None);

            return result!.Value.GetProperty("outcome");
        }

        public static string Outcome(JsonElement outcome)   => outcome.GetProperty("outcome").GetString()!;
        public static string? OptionId(JsonElement outcome) => outcome.Str("optionId");
    }

    static Harness Build(
            PolicySnapshot? snapshot, string? presetToken = null, string vendor = "cursor",
            AcpUnattendedInteractionPolicy policy = AcpUnattendedInteractionPolicy.Disabled) {
        AcpLaunchPermissionPreset? preset = null;
        if (presetToken is not null) AcpPermissionPresets.TryResolve(presetToken, out preset);

        Harness harness = null!;
        harness = new Harness {
            Bridge = new AcpInteractionBridge(
                requestInteraction: (_, _) => {
                    harness.Forwarded = true;

                    return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null));
                },
                agentId: AgentId,
                logger: NullLogger.Instance,
                unattendedPolicy: policy,
                preset: preset,
                notifyAutoApproval: n => harness.Notices.Add(n),
                policySnapshot: snapshot,
                policyVendor: vendor,
                notifyPolicyDecision: e => harness.Decisions.Add(e))
        };

        return harness;
    }

    [Test]
    public async Task Deny_answers_with_the_agents_own_reject_option() {
        var h = Build(Governed);

        var outcome = await h.HandleAsync(ShellFrame("execute", "git push --force", Standard));

        await Assert.That(Harness.Outcome(outcome)).IsEqualTo("selected");
        await Assert.That(Harness.OptionId(outcome)).IsEqualTo("reject-once");
        await Assert.That(h.Forwarded).IsFalse().Because("a policy-answered request never reaches a human");

        var evt = h.Decisions.Single();
        await Assert.That(evt.Seam).IsEqualTo(PolicySeams.AcpRequestPermission);
        await Assert.That(evt.Vendor).IsEqualTo("cursor");
        await Assert.That(evt.AgentId).IsEqualTo(AgentId);
        await Assert.That(evt.SessionId).IsEqualTo(AcpSessionId);
        await Assert.That(evt.SnapshotId).IsEqualTo("snap-1");
        await Assert.That(evt.EvaluationMode).IsEqualTo("full");
        await Assert.That(evt.RequestedOutcome).IsEqualTo("deny");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("deny");
        await Assert.That(evt.CorrelationId).IsEqualTo("call-1");
        await Assert.That(evt.CorrelationAmbiguous).IsFalse();
        await Assert.That(evt.Action.Kind).IsEqualTo("shell");
        await Assert.That(evt.MatchedRules.Single().Outcome).IsEqualTo("deny");
    }

    [Test]
    public async Task Deny_with_no_reject_option_offered_cancels_rather_than_selecting_an_allow() {
        var h = Build(Governed);

        var outcome = await h.HandleAsync(ShellFrame("execute", "git push --force", ("allow-once", "allow_once")));

        await Assert.That(Harness.Outcome(outcome)).IsEqualTo("cancelled");
        await Assert.That(h.Forwarded).IsFalse();
        await Assert.That(h.Decisions.Single().EffectiveOutcome).IsEqualTo("deny");
    }

    [Test]
    public async Task Allow_selects_the_single_allow_once() {
        var h = Build(Governed);

        var outcome = await h.HandleAsync(ShellFrame("execute", "git status", Standard));

        await Assert.That(Harness.Outcome(outcome)).IsEqualTo("selected");
        await Assert.That(Harness.OptionId(outcome)).IsEqualTo("allow-once");
        await Assert.That(h.Forwarded).IsFalse();

        var evt = h.Decisions.Single();
        await Assert.That(evt.RequestedOutcome).IsEqualTo("allow");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("allow");
        await Assert.That(evt.MatchedRules.Single().Outcome).IsEqualTo("allow");
    }

    [Test]
    public async Task Allow_without_an_unambiguous_option_falls_through_rather_than_fabricating_one() {
        var h = Build(Governed);

        var outcome = await h.HandleAsync(ShellFrame(
            "execute", "git status", ("allow-a", "allow_once"), ("allow-b", "allow_once"), ("reject-once", "reject_once")));

        await Assert.That(h.Forwarded).IsTrue();
        await Assert.That(Harness.Outcome(outcome)).IsEqualTo("cancelled");

        var evt = h.Decisions.Single();
        await Assert.That(evt.RequestedOutcome).IsEqualTo("allow");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("pass_through");
    }

    [Test]
    public async Task Ask_skips_a_preset_that_would_otherwise_auto_approve_the_kind() {
        var h = Build(Governed, AcpPermissionPresets.Edit);

        var outcome = await h.HandleAsync(PathFrame("edit", "/wt/secrets.env", Standard));

        await Assert.That(h.Forwarded).IsTrue().Because("an ask is terminal for the bridge's own layers");
        await Assert.That(h.Notices).IsEmpty();
        await Assert.That(Harness.Outcome(outcome)).IsEqualTo("cancelled");

        var evt = h.Decisions.Single();
        await Assert.That(evt.RequestedOutcome).IsEqualTo("ask");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("parked");
        await Assert.That(evt.Action.Kind).IsEqualTo("file_edit");
    }

    [Test]
    public async Task A_call_no_rule_matches_reaches_the_preset_unchanged() {
        var h = Build(Governed, AcpPermissionPresets.Edit);

        var outcome = await h.HandleAsync(PathFrame("read", "/wt/main.cs", Standard));

        await Assert.That(Harness.Outcome(outcome)).IsEqualTo("selected");
        await Assert.That(Harness.OptionId(outcome)).IsEqualTo("allow-once");
        await Assert.That(h.Forwarded).IsFalse();
        await Assert.That(h.Notices).HasSingleItem();
        await Assert.That(h.Decisions).IsEmpty().Because("no rule decided anything");
    }

    [Test]
    public async Task A_null_snapshot_leaves_the_bridge_exactly_as_it_was() {
        var h = Build(snapshot: null, AcpPermissionPresets.Explore);

        var approved = await h.HandleAsync(PathFrame("read", "/wt/main.cs", Standard));
        var parked   = await h.HandleAsync(ShellFrame("execute", "git push --force", Standard));

        await Assert.That(Harness.Outcome(approved)).IsEqualTo("selected");
        await Assert.That(Harness.OptionId(approved)).IsEqualTo("allow-once");
        await Assert.That(Harness.Outcome(parked)).IsEqualTo("cancelled");
        await Assert.That(h.Forwarded).IsTrue();
        await Assert.That(h.Notices).HasSingleItem();
        await Assert.That(h.Decisions).IsEmpty();
    }

    [Test]
    public async Task An_unattended_launch_is_decided_before_the_policy_layer() {
        var h = Build(Governed, policy: AcpUnattendedInteractionPolicy.AutoApprove);

        var outcome = await h.HandleAsync(ShellFrame("execute", "git push --force", Standard));

        await Assert.That(Harness.Outcome(outcome)).IsEqualTo("selected");
        await Assert.That(Harness.OptionId(outcome)).IsEqualTo("allow-once");
        await Assert.That(h.Decisions).IsEmpty();
    }
}
