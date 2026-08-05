using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

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
    public async Task AdmitsTheLaunchsOwnTool() =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall("Running: @kcap-flow-result-abc/submit_review_result"),
            Admitted("@kcap-flow-result-abc/submit_review_result"))).IsTrue();

    /// <summary>The whole point: a tool that is not in the injected set is refused, which under the
    /// policy reaps the reviewer exactly as Fail would.</summary>
    [Test]
    public async Task RefusesAToolThisLaunchDidNotInject() =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall("Running: @kcap-flows/start_flow"),
            Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    /// <summary>
    /// A DIFFERENT launch's alias must not be admitted. This is what the per-launch GUID buys: the
    /// admitted names are unguessable, so matching a display string is not the string classification
    /// it would otherwise be.
    /// </summary>
    [Test]
    public async Task RefusesAnotherLaunchsAliasForTheSameTool() =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall("Running: @kcap-flow-result-zzz/submit_review_result"),
            Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    /// <summary>
    /// No token at all is a DENIAL, not a pass. Otherwise the cheapest way past this gate would be to
    /// raise a frame that names no tool — a bare shell string, say.
    /// </summary>
    [Test]
    [Arguments("Running: rm -rf /")]
    [Arguments("")]
    [Arguments("submit_review_result")]
    [Arguments("@no-slash-here")]
    public async Task RefusesAFrameThatNamesNoTool(string title) =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall(title), Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

    /// <summary>Every token must be admitted, not merely one of them — otherwise smuggling an extra
    /// tool alongside an admitted one would pass.</summary>
    [Test]
    public async Task RefusesWhenAnyTokenIsNotAdmitted() =>
        await Assert.That(UnattendedToolAdmission.IsAdmitted(
            ToolCall("Running: @kcap-flow-result-abc/submit_review_result and @kcap-flows/start_flow"),
            Admitted("@kcap-flow-result-abc/submit_review_result"))).IsFalse();

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

    /// <summary>
    /// The admitted set and the trust list are ONE derivation. If they diverged, the reviewer could be
    /// trusted to call a tool the policy then refuses to approve — a round that dies on its own
    /// result call, which is the failure this whole policy exists to remove.
    /// </summary>
    [Test]
    public async Task TheAdmittedSetIsExactlyTheTrustListsNamespacedHalf() {
        var identity = LaunchIdentity.ForLaunch(aliasResultChannel: true);
        var specs = new List<AcpMcpServerSpec> {
            new(identity.ResultChannelWireName, "kcap", ["mcp", "flow-result"], []),
            new(identity.AllowlistWireName("kcap-review"), "kcap", ["mcp", "review"], []),
        };

        var admitted = UnattendedToolAdmission.AdmittedFor(specs, identity);
        var trusted  = KiroReviewerTrustList.Build(specs, identity)
                                            .Split(',')
                                            .Where(e => e.StartsWith('@'))
                                            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(admitted.SetEquals(trusted)).IsTrue();

        // And it really does cover both injected servers, so the equality above is not vacuous.
        await Assert.That(admitted).Contains(
            $"@{identity.ResultChannelWireName}/{KcapMcpRegistry.ReservedResultChannelTools[0].Name}");
        await Assert.That(admitted.Any(e => e.Contains("kcap-review", StringComparison.Ordinal))).IsTrue();
    }
}
