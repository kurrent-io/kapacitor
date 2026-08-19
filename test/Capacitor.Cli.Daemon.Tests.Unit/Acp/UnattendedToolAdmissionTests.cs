using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Kiro;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// The admission decision behind <c>AllowlistedAutoApprove</c>. It keys on a DISPLAY STRING, because
/// Kiro's permission frame carries no structured tool identity — so what is asserted here is that it
/// fails closed on every shape that is not unambiguously one of this launch's own tools.
/// </summary>
public class UnattendedToolAdmissionTests {
    static JsonElement ToolCall(string title) =>
        JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { toolCallId = "tc-1", title }));

    static IReadOnlySet<string> Admitted(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Test]
    [Arguments("Running: @kcap-flow-result-abc/submit_review_result")]
    [Arguments("@kcap-flow-result-abc/submit_review_result")]
    public async Task AdmitsTheLaunchsOwnTool(string title) =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall(title), Admitted("@kcap-flow-result-abc/submit_review_result"))).IsTrue();

    /// <summary>The whole point: a tool this launch did not inject is refused, which under the policy
    /// reaps the reviewer exactly as Fail would.</summary>
    [Test]
    public async Task RefusesAToolThisLaunchDidNotInject() =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall("Running: @kcap-flows/start_flow"),
            Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    /// <summary>
    /// THE case that killed the previous implementation. It scanned the title for an admitted token
    /// and argued the unguessable alias made that safe — but the MODEL knows its own alias, so
    /// prompt-injected content only has to make it echo one alongside anything else. Every string here
    /// contains a perfectly valid admitted token and must still be refused.
    /// </summary>
    [Test]
    [Arguments("Running: execute_bash echo @kcap-flow-result-abc/submit_review_result")]
    [Arguments("@kcap-flow-result-abc/submit_review_result && rm -rf /")]
    [Arguments("evil@kcap-flow-result-abc/submit_review_result")]
    [Arguments("Running: @kcap-flow-result-abc/submit_review_result @kcap-flows/start_flow")]
    [Arguments("Running:  @kcap-flow-result-abc/submit_review_result")]
    [Arguments("Running: @kcap-flow-result-abc/submit_review_result ")]
    public async Task RefusesAnAdmittedTokenEmbEddedInAnythingElse(string title) =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall(title), Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    /// <summary>
    /// A DIFFERENT launch's alias is refused. Aliasing does not make the match safe on its own (see
    /// above), but it does keep one launch's admitted set from admitting another's.
    /// </summary>
    [Test]
    public async Task RefusesAnotherLaunchsAliasForTheSameTool() =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall("Running: @kcap-flow-result-zzz/submit_review_result"),
            Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    /// <summary>No identifiable tool is a DENIAL, not a pass.</summary>
    [Test]
    [Arguments("Running: rm -rf /")]
    [Arguments("")]
    [Arguments("submit_review_result")]
    [Arguments("@no-slash-here")]
    [Arguments("Running: ")]
    public async Task RefusesAFrameThatNamesNoAdmittedTool(string title) =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall(title), Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    [Test]
    public async Task RefusesEverythingWhenNothingIsAdmitted() =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall("Running: @kcap-flow-result-abc/submit_review_result"), Admitted())).IsFalse();

    [Test]
    [Arguments("""{"toolCallId":"tc-1"}""")]
    [Arguments("""{"toolCallId":"tc-1","title":null}""")]
    [Arguments("""{"toolCallId":"tc-1","title":42}""")]
    [Arguments("""["not","an","object"]""")]
    public async Task RefusesAMalformedToolCall(string json) =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            JsonSerializer.Deserialize<JsonElement>(json),
            Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    /// <summary>Admission must equal the WHOLE trust list, native tools included — if it were narrower,
    /// a leaked prompt for a trusted-but-unadmitted tool (e.g. native <c>fs_read</c>) reaps the reviewer.</summary>
    [Test]
    public async Task TheAdmittedSetIsExactlyTheTrustList() {
        var identity = LaunchIdentity.ForLaunch(aliasResultChannel: true);
        var specs = new List<AcpMcpServerSpec> {
            new(identity.ResultChannelWireName, "kcap", ["mcp", "flow-result"], []),
            new(identity.AllowlistWireName("kcap-review"), "kcap", ["mcp", "review"], []),
        };

        var admitted = UnattendedToolAdmission.AdmittedFor(specs, identity);
        var trusted  = KiroReviewerTrustList.Build(specs, identity)
                                            .Split(',')
                                            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(admitted.SetEquals(trusted)).IsTrue();

        // Not vacuous: it covers the native trusted tools AND both injected servers.
        await Assert.That(admitted).Contains("fs_read");
        await Assert.That(admitted).Contains("thinking");
        await Assert.That(admitted).Contains(
            $"@{identity.ResultChannelWireName}/{KcapMcpRegistry.ReservedResultChannelTools[0].Name}");
        await Assert.That(admitted.Any(e => e.Contains("kcap-review", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>A frame naming a native trusted tool (<c>fs_read</c>/<c>thinking</c>) is admitted, so a
    /// leaked prompt for it is auto-approved instead of reaping.</summary>
    [Test]
    [Arguments("Running: fs_read")]
    [Arguments("fs_read")]
    [Arguments("Running: thinking")]
    public async Task AdmitsANativeTrustedToolFrame(string title) {
        var identity = LaunchIdentity.ForLaunch(aliasResultChannel: true);
        var specs = new List<AcpMcpServerSpec> {
            new(identity.ResultChannelWireName, "kcap", ["mcp", "flow-result"], []),
        };

        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall(title), UnattendedToolAdmission.AdmittedFor(specs, identity))).IsTrue();
    }

    /// <summary>The containment floor holds: <c>fs_write</c> and <c>execute_bash</c> are in neither the
    /// trust argv nor the admitted set, so a leaked prompt for either still reaps.</summary>
    [Test]
    [Arguments("Running: fs_write")]
    [Arguments("Running: execute_bash")]
    [Arguments("execute_bash")]
    public async Task RefusesAnUntrustedNativeToolFrame(string title) {
        var identity = LaunchIdentity.ForLaunch(aliasResultChannel: true);
        var specs = new List<AcpMcpServerSpec> {
            new(identity.ResultChannelWireName, "kcap", ["mcp", "flow-result"], []),
        };

        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall(title), UnattendedToolAdmission.AdmittedFor(specs, identity))).IsFalse();
    }
}
