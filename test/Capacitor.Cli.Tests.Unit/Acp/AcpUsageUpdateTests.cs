using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The ACP spec's Session Usage RFD (<c>usage_update</c>, carrying <c>used</c>/<c>size</c>).
/// No vendor implements it yet — probed 2026-07-25, neither cursor-agent 2026.07.23 nor
/// Copilot CLI 1.0.75 emits it — so this path is dormant. It exists so the server's context
/// chip lights up for every ACP-hosted vendor the moment any of them ships it.
///
/// Translator-level tests; the <c>Reduce()</c> validation they depend on is exercised through
/// the runtime harness in <see cref="AcpHostedAgentRuntimeTests"/>.
/// </summary>
public class AcpUsageUpdateTests {
    const string TimestampIso = "2026-07-25T12:00:00Z";

    static AcpSessionUpdate Usage(long used, long window) =>
        new(AcpUpdateKind.UsageUpdate, ContextUsedTokens: used, ContextWindowTokens: window);

    [Test]
    public async Task Usage_update_translates_to_a_usage_envelope() {
        var e = AcpEventTranslator.Translate(Usage(142_000, 200_000), seq: 5, TimestampIso);

        await Assert.That(e).IsNotNull();
        await Assert.That(e!.Value.Kind).IsEqualTo(AcpEventKind.Usage);
        await Assert.That(e.Value.ContextUsedTokens).IsEqualTo(142_000);
        await Assert.That(e.Value.ContextWindowTokens).IsEqualTo(200_000);
        await Assert.That(e.Value.Seq).IsEqualTo(5);
        await Assert.That(e.Value.TimestampIso).IsEqualTo(TimestampIso);
    }

    [Test]
    public async Task Contract_version_stays_1_the_fields_are_additive() {
        // The new fields are optional, so an older server simply ignores them rather than
        // rejecting the envelope on an unknown contract version.
        var e = AcpEventTranslator.Translate(Usage(1, 2), seq: 1, TimestampIso);
        await Assert.That(e!.Value.ContractVersion).IsEqualTo(1);
    }

    [Test]
    public async Task Resolved_model_is_stamped_on_the_envelope() {
        // The server's mapper is a pure per-envelope function with no session-fold access, so
        // model attribution has to travel on the wire rather than be looked up server-side.
        var e = AcpEventTranslator.Translate(
            Usage(1_000, 200_000), seq: 2, TimestampIso, resolvedModel: "claude-sonnet-4-5");

        await Assert.That(e!.Value.Model).IsEqualTo("claude-sonnet-4-5");
    }

    [Test]
    public async Task A_missing_resolved_model_is_tolerated() {
        // Harmless: the chip's denominator is the reading's own window, so no model lookup is
        // needed to render it.
        var e = AcpEventTranslator.Translate(Usage(1_000, 200_000), seq: 3, TimestampIso);
        await Assert.That(e!.Value.Model).IsNull();
    }

    [Test]
    public async Task Usage_envelope_sets_no_field_outside_its_kind_group() {
        // The envelope is flat and Kind-discriminated: exactly one per-kind group is populated.
        var e = AcpEventTranslator.Translate(
            Usage(1_000, 200_000), seq: 4, TimestampIso, resolvedModel: "m")!.Value;

        await Assert.That(e.Text).IsNull();
        await Assert.That(e.ToolCallId).IsNull();
        await Assert.That(e.ToolName).IsNull();
        await Assert.That(e.ToolResult).IsNull();
        await Assert.That(e.Cwd).IsNull();
        await Assert.That(e.RawSessionId).IsNull();
        await Assert.That(e.EndReason).IsNull();
    }
}
