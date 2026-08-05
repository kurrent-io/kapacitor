// test/Capacitor.Cli.Tests.Unit/Acp/AcpInteractionBridgeTests.cs
using System.Diagnostics.Metrics;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// <see cref="AcpInteractionBridge"/> parses an inbound <c>session/request_permission</c>
/// (spec-derived shape, NOT probe-confirmed — see <c>docs/acp-probe-findings.md</c>) or capability-
/// gated <c>elicitation/create</c> server request, forwards it to an injected
/// "ask the server" delegate (standing in for <see cref="Capacitor.Cli.Daemon.Services.ServerConnection.RequestAcpInteractionAsync"/>),
/// and maps the returned <see cref="AcpInteractionDecision"/> back to the ACP JSON-RPC result
/// shape. Unit-tested against the delegate directly — no real SignalR connection involved.
/// </summary>
public class AcpInteractionBridgeTests {
    const string AgentId      = "agent-1";
    const string AcpSessionId = "fc2e09cf-f4b0-4463-9dc1-bda11268896b";

    static JsonElement PermissionRequestParams(string[] optionIds) {
        var optionsJson = string.Join(",", optionIds.Select(id => $$"""{"optionId":"{{id}}","name":"{{id}}","kind":"allow_once"}"""));
        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":[{{optionsJson}}]}""";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Round-7 spec-review Finding 5 (test consistency): the "selected" case must supply a REAL,
    /// matching <see cref="AcpInteractionDecision.SelectedOptionId"/> — asserting `selected(thatOptionId)`
    /// — never a null <c>SelectedOptionId</c> that happens to map to the first offered option. An
    /// earlier draft of this test resolved with `new AcpInteractionDecision("allow", null, ...)` and
    /// still asserted `optionId == "allow-once"` (the first offered option), which contradicted this
    /// same file's own fail-closed tests below (`RequestPermission_AffirmativeOutcomeButNoSelectedOptionId_MapsToCancelled_NeverFirstOption`,
    /// `RequestPermission_SingleOptionOffered_NoSelectedOptionId_StillMapsToCancelled`) — a null
    /// <c>SelectedOptionId</c> must map to <c>cancelled</c>, NEVER <c>selected</c>/first-option. This
    /// test now supplies an explicit, resolvable `SelectedOptionId: "allow-once"` so it proves the
    /// GENUINE "selected" path (an id that matches one of the offered options) without relying on
    /// the removed first-option fallback.
    /// </summary>
    [Test]
    public async Task RequestPermission_SelectedOutcome_ReturnsSelectedResultWithMatchingOptionId() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", "allow-once", "Allow", null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("selected");
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("allow-once");
    }

    [Test]
    public async Task RequestPermission_DenyOutcome_ReturnsCancelledResult() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("deny", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    [Test]
    public async Task RequestPermission_CancelOutcome_ReturnsCancelledResult() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    [Test]
    public async Task RequestPermission_ServerCallThrows_ReturnsCancelledResultDefensively() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => throw new InvalidOperationException("connection dropped"),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Spec-review Finding 3(b): connection-closed / runtime-disposing / CT-cancelled while this
    /// interaction is pending — the SAME path <c>AcpHostedAgentRuntime.DisposeAsync</c> triggers by
    /// cancelling its <c>_cts</c>, which flows through <c>AcpConnection</c>'s read loop into this
    /// bridge's <c>ct</c> parameter and ultimately into <c>PendingAcpInteractionRegistry.AwaitDecisionAsync</c>'s
    /// own cancellation registration (Task B2), which throws <see cref="OperationCanceledException"/>.
    /// PRE-FIX, this exception type was excluded from the bridge's catch clause and propagated
    /// uncaught, letting <c>AcpConnection.HandleServerRequestAsync</c>'s generic catch-all convert
    /// it to a JSON-RPC "Internal error" (code -32603) instead of a well-formed ACP <c>cancelled</c>
    /// outcome. This test proves the bridge itself now produces the well-formed shape.
    /// </summary>
    [Test]
    public async Task RequestPermission_ConnectionCancelled_ReturnsCancelledResultNotUnhandledException() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromException<AcpInteractionDecision>(new OperationCanceledException("connection closing")),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    [Test]
    public async Task RequestPermission_MissingParams_ReturnsCancelledResultWithoutCallingServer() {
        var called = false;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { called = true; return Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", Params: null);

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(called).IsFalse();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Qodo daemon-review Q1 (fail-safe hole): <c>SessionRequestPermissionParams.Options</c> is
    /// typed as a non-nullable <c>PermissionOptionDto[]</c>, but System.Text.Json does NOT enforce
    /// non-nullable-reference annotations at deserialize time — an <c>options</c> field OMITTED
    /// entirely from the wire frame (the ACP spec for this method is spec-derived, NOT
    /// probe-confirmed; see <c>docs/acp-probe-findings.md</c>) yields <c>parsed.Options == null</c>.
    /// PRE-FIX this NRE'd inside <c>.Select(...)</c>/<c>MapPermissionDecision</c>, which
    /// <see cref="HandlePermissionAsync"/>'s own try/catch does NOT cover (it only wraps the
    /// deserialize step and the <c>requestInteraction</c> call) — so the exception propagated all
    /// the way out to <see cref="AcpConnection.HandleServerRequestAsync"/>'s generic catch-all,
    /// which answers with a bare JSON-RPC "Internal error" (-32603) instead of the well-formed ACP
    /// <c>cancelled</c> outcome every other malformed-input path in this bridge produces. This test
    /// proves an omitted <c>options</c> field degrades to <c>cancelled</c> instead.
    /// </summary>
    [Test]
    public async Task RequestPermission_OptionsFieldOmitted_ReturnsCancelledResultNotThrow() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"} }""";
        var request = new AcpRequest(1, "session/request_permission", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Qodo daemon-review Q1: same fail-safe hole as above, but for an explicit JSON <c>null</c>
    /// (rather than an omitted field) for <c>options</c> — also deserializes to
    /// <c>parsed.Options == null</c> since <see cref="Capacitor.Cli.Core.Acp.PermissionOptionDto"/>[]
    /// is a reference type and System.Text.Json happily assigns <c>null</c> to it regardless of the
    /// record's non-nullable C# annotation.
    /// </summary>
    [Test]
    public async Task RequestPermission_OptionsFieldExplicitNull_ReturnsCancelledResultNotThrow() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":null}""";
        var request = new AcpRequest(1, "session/request_permission", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Qodo daemon-review Q1: an <c>options</c> array containing a JSON <c>null</c> ELEMENT (rather
    /// than the whole array being absent/null) must also never throw — the fix's normalization
    /// filters out null elements before building <see cref="AcpInteractionRequest.Options"/> and
    /// before <c>MapPermissionDecision</c> ever sees them.
    /// </summary>
    [Test]
    public async Task RequestPermission_OptionsArrayContainsNullElement_ReturnsCancelledResultNotThrow() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":[null,{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]}""";
        var request = new AcpRequest(1, "session/request_permission", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        // "allow" with no SelectedOptionId still fails closed per the existing fail-closed contract —
        // the point of this test is only that the null element never throws.
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// A pre-stabilization draft frame (no <c>mode</c>) is deliberately NOT interpreted — the
    /// stabilized mode variants each require <c>mode</c>, and "MUST NOT render an unknown mode as
    /// a known elicitation mode" extends to a missing one. Cancelled in the stabilized response
    /// shape without ever routing to a human.
    /// </summary>
    [Test]
    public async Task ElicitationCreate_ModelessDraftFrame_CancelsAsMalformedRequest_WithoutRouting() {
        var routed = 0;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { Interlocked.Increment(ref routed); return Task.FromResult(new AcpInteractionDecision("answered", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","message":"Proceed?"}""";
        var request = new AcpRequest(1, "elicitation/create", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        await Assert.That(routed).IsEqualTo(0);
    }

    /// <summary>
    /// Qodo daemon-review Q2: <see cref="AcpHostedAgentRuntime"/> used to wire
    /// <c>OnServerRequest</c> with <c>_sessionId ?? ""</c> — a server→client request handled before
    /// <c>session/new</c>'s response assigns <c>_sessionId</c> (the read loop can start before that
    /// completes) forwarded an <see cref="AcpInteractionRequest"/> with <c>AcpSessionId == ""</c>,
    /// breaking server-side correlation. The fix drops that runtime-level session id entirely and
    /// has the bridge trust <see cref="SessionRequestPermissionParams.SessionId"/> — the request's
    /// OWN params — as the sole source of truth. This test proves the forwarded
    /// <see cref="AcpInteractionRequest.AcpSessionId"/> comes from the request params, not from any
    /// caller-supplied value, by using a params <c>sessionId</c> distinct from any value the old API
    /// shape would have injected.
    /// </summary>
    [Test]
    public async Task RequestPermission_ForwardsAcpSessionIdFromRequestParams() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { captured = req; return Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        const string sessionIdFromParams = "session-from-params-only";
        var json = $$"""{"sessionId":"{{sessionIdFromParams}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]}""";
        var request = new AcpRequest(1, "session/request_permission", JsonDocument.Parse(json).RootElement.Clone());

        await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.AcpSessionId).IsEqualTo(sessionIdFromParams);
    }

    /// <summary>
    /// Qodo daemon-review Q2: a <c>session/request_permission</c> whose OWN params carry no
    /// resolvable session id (missing/empty <c>sessionId</c>) can't be correlated server-side at
    /// all — this must degrade to the well-formed ACP <c>cancelled</c> result (never a thrown
    /// exception, and never forwarded to the server with an empty/placeholder session id).
    /// </summary>
    [Test]
    public async Task RequestPermission_EmptySessionIdInParams_ReturnsCancelledResultWithoutCallingServer() {
        var called = false;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { called = true; return Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = """{"sessionId":"","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]}""";
        var request = new AcpRequest(1, "session/request_permission", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(called).IsFalse();
        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Fresh-review finding (this revision, closing the last gap in spec-review Finding 2): an
    /// earlier draft of this fix left a "no SelectedOptionId at all → fall back to the first
    /// offered option" defensive path for a RECOGNIZED AFFIRMATIVE outcome — e.g. a UI/decision-submit
    /// path that only knows "the user said yes" without echoing back a specific chosen option. That
    /// is the SAME class of silent-grant bug spec-review Finding 2 targets: an ACP options request
    /// with two-or-more offered options (here, "allow-once" and "deny") must NEVER resolve to
    /// `options[0]` just because a caller forgot (or was unable) to supply which option was chosen.
    /// This test proves the FIXED behavior: `allow_always` (a recognized affirmative outcome) with
    /// `SelectedOptionId: null` maps to `cancelled`, NEVER `selected`. There is no code path left in
    /// `MapPermissionDecision` that can produce a `selected` result without an explicit, resolvable
    /// `SelectedOptionId` matching one of the offered options — see the next-but-one test for the
    /// unresolvable-id half of the same fail-closed guarantee.
    /// </summary>
    [Test]
    public async Task RequestPermission_AffirmativeOutcomeButNoSelectedOptionId_MapsToCancelled_NeverFirstOption() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow_always", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        // No SelectedOptionId was supplied — must be cancelled, NEVER "selected"/"allow-once".
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
        await Assert.That(outcome.TryGetProperty("optionId", out _)).IsFalse();
    }

    /// <summary>
    /// Same fail-closed guarantee as above, for a single-option permission prompt specifically —
    /// proves the fix does not special-case "only one option was offered" as an implicit "assume
    /// that one." A caller MUST echo back the single option's id explicitly; omitting it still maps
    /// to cancelled even when there is only one option that COULD have been meant.
    /// </summary>
    [Test]
    public async Task RequestPermission_SingleOptionOffered_NoSelectedOptionId_StillMapsToCancelled() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Spec-review Finding 6: proves resolution is by <see cref="AcpInteractionDecision.SelectedOptionId"/>,
    /// NEVER by re-matching <see cref="AcpInteractionDecision.SelectedOptionLabel"/> — duplicate
    /// labels across two DIFFERENT offered options must resolve to the option whose
    /// <c>optionId</c> was actually selected, not "whichever option happens to have this label
    /// first" (the old label-matching behavior this finding replaces).
    /// </summary>
    [Test]
    public async Task RequestPermission_DuplicateLabels_ResolvesByOptionIdNotFirstLabelMatch() {
        var bridge = new AcpInteractionBridge(
            // Both offered options are labelled "Allow" — only OptionId disambiguates which one
            // the human actually picked. Reordered relative to the wire order below on purpose.
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", "allow-second", "Allow", null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":[{"optionId":"allow-first","name":"Allow","kind":"allow_once"},{"optionId":"allow-second","name":"Allow","kind":"allow_always"},{"optionId":"deny","name":"Deny","kind":"reject_once"}]}""";
        var request = new AcpRequest(1, "session/request_permission", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("selected");
        // Must be "allow-second" (matched by id) — a label-based (or first-match) mapper would
        // have wrongly returned "allow-first", since that's the first option labelled "Allow".
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("allow-second");
    }

    /// <summary>
    /// Spec-review Finding 6: an unresolvable <see cref="AcpInteractionDecision.SelectedOptionId"/>
    /// (doesn't match any offered option's <c>optionId</c>) is treated as CANCELLED, not a silent
    /// grant via a first-option fallback — an id that was explicitly supplied but doesn't match
    /// anything offered indicates a correlation bug or a stale/replayed decision, and granting an
    /// unrelated option in that case would be worse than failing safe.
    /// </summary>
    [Test]
    public async Task RequestPermission_UnresolvableSelectedOptionId_TreatedAsCancelled() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", "does-not-exist", "Allow", null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Spec-review Finding 2 (the security-critical fail-safe half): ANY outcome string that is
    /// neither a recognized affirmative outcome (<c>allow</c>/<c>allow_once</c>/<c>allow_always</c>/
    /// <c>answered</c>) NOR a recognized negative outcome (<c>deny</c>/<c>cancel</c>) must map to
    /// <c>cancelled</c> — NEVER fall through to <c>selected</c>/<c>options[0]</c>. Before this fix,
    /// <c>MapPermissionDecision</c> only special-cased the literal strings <c>"deny"</c>/<c>"cancel"</c>
    /// and treated every other string (including a typo'd <c>"cancelled"</c>, or any future/unknown
    /// outcome) as "select the first option" — i.e. an unrecognized outcome silently GRANTED
    /// permission. This test uses the exact typo (<c>"cancelled"</c> instead of the canonical
    /// <c>"cancel"</c>, per Task A2's Interfaces note) that a server-side regression would produce,
    /// proving the daemon-side mapper is fail-safe even if the server ever sends a string outside
    /// the documented vocabulary.
    /// </summary>
    [Test]
    public async Task RequestPermission_UnrecognizedOutcome_MapsToCancelled_NeverFallsThroughToSelected() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("cancelled", null, null, null, null, null)), // NOT the canonical "cancel"
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
        await Assert.That(outcome.TryGetProperty("optionId", out _)).IsFalse(); // never selects an option for an unmapped outcome
    }

    [Test]
    public async Task UnrecognizedMethod_ReturnsNullResultSoConnectionSendsMethodNotFound() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "fs/read_text_file", Params: JsonDocument.Parse("{}").RootElement.Clone());

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ElicitationCreate_StabilizedSingleSelect_AnswersAcceptWithSelectedId() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => {
                captured = req;
                // Resolution is by SelectedOptionId ("yes"), not by label — labels are display-only.
                return Task.FromResult(new AcpInteractionDecision("answered", "yes", "Yes", 0, null, null));
            },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes","no"]}}}""", "Proceed?")),
            CancellationToken.None);

        await Assert.That(captured!.Value.Kind).IsEqualTo("elicitation");
        await Assert.That(captured.Value.IsMultiSelect).IsFalse();
        await Assert.That(captured.Value.Options!.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "yes", "no" });
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"accept","content":{"proceed":"yes"}}""");
    }

    /// <summary>
    /// A bare-string schema (no enum/oneOf) is the FreeText subset: forwarded with zero options
    /// (the server's existing generic free-text card), <c>requestedSchema</c> forwarded verbatim
    /// for audit, and an answered free text becomes a stabilized accept keyed by the property name.
    /// </summary>
    [Test]
    public async Task ElicitationCreate_FreeTextSchema_ForwardsSchemaVerbatim_AcceptsAnsweredText() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => {
                captured = req;

                return Task.FromResult(new AcpInteractionDecision("answered", null, null, null, "free text answer", null));
            },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"name":{"type":"string"}}}""", "Describe the config")),
            CancellationToken.None);

        await Assert.That(captured!.Value.RequestedSchema!.Value.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(captured.Value.Options).IsEmpty();
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"accept","content":{"name":"free text answer"}}""");
    }

    /// <summary>
    /// A <c>requestedSchema</c> that is not a JSON object is unrenderable: cancelled in the
    /// stabilized shape and NEVER routed to a human (the pre-stabilization lane used to forward
    /// it verbatim and let the server render a generic card; the stabilized classifier owns the
    /// decision daemon-side now).
    /// </summary>
    [Test]
    public async Task ElicitationCreate_SchemaIsNotAnObject_CancelsWithoutRouting() {
        var routed = 0;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { Interlocked.Increment(ref routed); return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams(""" "not-an-object" """.Trim(), "Proceed?")),
            CancellationToken.None);

        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        await Assert.That(routed).IsEqualTo(0);
    }

    // ── Payload-free "blocking request issued/resolved" lifecycle logging ──────────────────────

    /// <summary>Records every log call — mirrors <c>AcpTranscriptAggregationTests.CaptureLogger</c>'s
    /// established pattern.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    [Test]
    public async Task RequestPermission_Selected_LogsIssuedAndResolvedWithKindAndDecision_NeverToolContent() {
        var logger = new CaptureLogger();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", "allow-once", "Allow", null, null, null)),
            agentId: AgentId,
            logger: logger);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));
        await bridge.HandleAsync(request, CancellationToken.None);

        var infoEntries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).Contains(e => e.Message.Contains("issued") && e.Message.Contains("permission"));
        await Assert.That(infoEntries).Contains(e => e.Message.Contains("resolved") && e.Message.Contains("permission") && e.Message.Contains("selected"));

        // Payload-free: the tool title ("Run ls") and the chosen optionId ("allow-once") must never
        // leak into these Info logs, even though "allow-once" happens to also be a log-safe kind
        // token elsewhere — check the actual tool content, not option ids.
        await Assert.That(infoEntries).DoesNotContain(e => e.Message.Contains("Run ls"));
    }

    [Test]
    public async Task RequestPermission_Cancelled_LogsResolvedWithCancelledDecision() {
        var logger = new CaptureLogger();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("deny", null, null, null, null, null)),
            agentId: AgentId,
            logger: logger);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));
        await bridge.HandleAsync(request, CancellationToken.None);

        var infoEntries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).Contains(e => e.Message.Contains("resolved") && e.Message.Contains("cancelled"));
    }

    [Test]
    public async Task RequestPermission_MissingParams_NeverLogsIssuedOrResolved_NoInteractionWasActuallyDispatched() {
        // A malformed/unparsable request never reaches requestInteraction at all — there is no
        // "blocking request" to report as issued or resolved.
        var logger = new CaptureLogger();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: logger);

        var request = new AcpRequest(1, "session/request_permission", Params: null);
        await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(logger.Entries.Where(e => e.Level == LogLevel.Information)).IsEmpty();
    }

    [Test]
    public async Task ElicitationCreate_Accepted_LogsIssuedResolvedAndAnswered_NeverPromptText() {
        var logger = new CaptureLogger();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("answered", "yes", "Yes", 0, null, null)),
            agentId: AgentId,
            logger: logger);

        await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes","no"]}}}""", "Proceed?")),
            CancellationToken.None);

        var infoEntries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).Contains(e => e.Message.Contains("issued") && e.Message.Contains("elicitation"));
        await Assert.That(infoEntries).Contains(e => e.Message.Contains("resolved") && e.Message.Contains("elicitation") && e.Message.Contains("accept"));
        await Assert.That(infoEntries).Contains(e => e.Message.Contains("elicitation answered") && e.Message.Contains("SingleSelect"));
        await Assert.That(infoEntries).DoesNotContain(e => e.Message.Contains("Proceed?")); // never the prompt text
    }

    // ── Unattended review-flow interaction policies ─────────────────────────────────────────────
    //
    // A bridge built with AutoApprove selects the least-privilege ALLOW option by
    // exact Kind WITHOUT ever routing to a human, and fails closed (cancelled) when there is no
    // unambiguous allow option. Every case below uses a delegate that INCREMENTS a counter (never
    // throws — the bridge would swallow a throw and return cancelled, masking the assertion) so each
    // test can prove requestInteraction was invoked exactly zero times.

    static JsonElement PermissionParamsWithOptions(string optionsJson) {
        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":{{optionsJson}}}""";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Test]
    [Arguments("session/request_permission")]
    [Arguments("elicitation/create")]
    [Arguments("vendor/unknown_interaction")]
    public async Task FailPolicy_AnyInteraction_SignalsReap_WithoutRoutingToHuman(string method) {
        var routed = 0;
        var reaped = new List<string>();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { Interlocked.Increment(ref routed); return Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance,
            unattendedPolicy: AcpUnattendedInteractionPolicy.Fail,
            unexpectedUnattendedInteraction: reaped.Add);
        var request = new AcpRequest(1, method, PermissionParamsWithOptions("[]"));

        var result = await bridge.HandleAsync(request, CancellationToken.None);

        await Assert.That(routed).IsEqualTo(0);
        await Assert.That(reaped).IsEquivalentTo([method]);
        if (method == "vendor/unknown_interaction")
            await Assert.That(result).IsNull();
        else if (method == "elicitation/create")
            await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        else
            await Assert.That(result!.Value.GetProperty("outcome").GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    static (AcpInteractionBridge Bridge, Func<int> Calls) AutoApproveBridge(ILogger? logger = null) {
        var calls = 0;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { Interlocked.Increment(ref calls); return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)); },
            agentId: AgentId,
            logger: logger ?? NullLogger.Instance,
            unattendedPolicy: AcpUnattendedInteractionPolicy.AutoApprove);

        return (bridge, () => Volatile.Read(ref calls));
    }

    static async Task<(string Outcome, string? OptionId)> RunAutoApprovePermissionAsync(string optionsJson) {
        var (bridge, calls) = AutoApproveBridge();
        var request = new AcpRequest(1, "session/request_permission", PermissionParamsWithOptions(optionsJson));
        var result  = await bridge.HandleAsync(request, CancellationToken.None);
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(calls()).IsEqualTo(0); // never routed to a human
        return (outcome.GetProperty("outcome").GetString()!, outcome.TryGetProperty("optionId", out var id) ? id.GetString() : null);
    }

    [Test]
    public async Task AutoApprove_PrefersAllowOnce_OverAllowAlways() {
        var (outcome, optionId) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"r","name":"Reject","kind":"reject_once"},{"optionId":"ao","name":"Allow once","kind":"allow_once"},{"optionId":"aa","name":"Allow always","kind":"allow_always"}]""");

        await Assert.That(outcome).IsEqualTo("selected");
        await Assert.That(optionId).IsEqualTo("ao");
    }

    [Test]
    public async Task AutoApprove_OneAllowOnce_WithMultipleAllowAlways_SelectsTheOnce() {
        var (outcome, optionId) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"r","name":"Reject","kind":"reject_once"},{"optionId":"ao","name":"Allow once","kind":"allow_once"},{"optionId":"aa1","name":"Allow always","kind":"allow_always"},{"optionId":"aa2","name":"Allow always project","kind":"allow_always"}]""");

        await Assert.That(outcome).IsEqualTo("selected");
        await Assert.That(optionId).IsEqualTo("ao");
    }

    [Test]
    public async Task AutoApprove_OnlyAllowAlways_SelectsIt() {
        var (outcome, optionId) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"aa","name":"Allow always","kind":"allow_always"}]""");

        await Assert.That(outcome).IsEqualTo("selected");
        await Assert.That(optionId).IsEqualTo("aa");
    }

    [Test]
    public async Task AutoApprove_NoAllowOption_MapsToCancelled() {
        var (outcome, _) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"r","name":"Reject","kind":"reject_once"},{"optionId":"ra","name":"Reject always","kind":"reject_always"}]""");

        await Assert.That(outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AutoApprove_EmptyOptions_MapsToCancelled() {
        var (outcome, _) = await RunAutoApprovePermissionAsync("[]");

        await Assert.That(outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AutoApprove_AmbiguousTwoAllowOnce_MapsToCancelled() {
        var (outcome, _) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"ao1","name":"Allow once","kind":"allow_once"},{"optionId":"ao2","name":"Allow once too","kind":"allow_once"}]""");

        await Assert.That(outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AutoApprove_AmbiguousTwoAllowAlways_NoOnce_MapsToCancelled() {
        var (outcome, _) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"aa1","name":"Allow always","kind":"allow_always"},{"optionId":"aa2","name":"Allow always project","kind":"allow_always"}]""");

        await Assert.That(outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AutoApprove_BlankOptionId_MapsToCancelled() {
        var (outcome, _) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"   ","name":"Allow once","kind":"allow_once"}]""");

        await Assert.That(outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AutoApprove_DuplicateOptionId_MapsToCancelled() {
        // The chosen allow_once shares its OptionId with another offered option — echoing it could
        // select the wrong option server-side, so fail closed.
        var (outcome, _) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"dup","name":"Reject","kind":"reject_once"},{"optionId":"dup","name":"Allow once","kind":"allow_once"}]""");

        await Assert.That(outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AutoApprove_DeceptiveAllowName_ButRejectKind_MapsToCancelled() {
        // Kind wins over Name — a hostile agent labeling a reject option "Allow" must not be approved.
        var (outcome, _) = await RunAutoApprovePermissionAsync(
            """[{"optionId":"x","name":"Allow","kind":"reject_once"}]""");

        await Assert.That(outcome).IsEqualTo("cancelled");
    }

    [Test]
    public async Task AutoApprove_Elicitation_DeclinedWithoutRoutingToHuman_InStabilizedShape() {
        var (bridge, calls) = AutoApproveBridge();

        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes"]}}}""", "Proceed?")),
            CancellationToken.None);

        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        await Assert.That(calls()).IsEqualTo(0);
    }

    [Test]
    public async Task AutoApprove_AuditLog_PinsAgentIdAndKind_NoPathField() {
        var logger = new CaptureLogger();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { throw new InvalidOperationException("must not be called"); },
            agentId: AgentId,
            logger: logger,
            unattendedPolicy: AcpUnattendedInteractionPolicy.AutoApprove);

        var request = new AcpRequest(1, "session/request_permission", PermissionParamsWithOptions(
            """[{"optionId":"ao","name":"Allow once","kind":"allow_once"}]"""));
        await bridge.HandleAsync(request, CancellationToken.None);

        var infoEntries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).Contains(e =>
            e.Message.Contains("auto-approved")
            && e.Message.Contains(AgentId)
            && e.Message.Contains("allow_once"));
        // No path field is ever logged — the bridge has no trustworthy path (ToolCall is opaque).
        await Assert.That(infoEntries).DoesNotContain(e => e.Message.Contains("path"));
    }

    // ── Stabilized elicitation: shared helpers ──────────────────────────────────────────────────

    static AcpRequest ElicitationRequest(string paramsJson) =>
        new(1, "elicitation/create", JsonDocument.Parse(paramsJson).RootElement.Clone());

    static string FormParams(string schemaJson, string message = "Pick") =>
        $$"""{"sessionId":"{{AcpSessionId}}","message":"{{message}}","mode":"form","requestedSchema":{{schemaJson}}}""";

    /// <summary>Bridge with a routed-call counter and a capture logger — the standard harness for
    /// the pre-routing-cancel contract (cancel shape + not-routed + reason log, all three).</summary>
    static (AcpInteractionBridge Bridge, Func<int> Routed, CaptureLogger Logger) GateBridge(AcpInteractionDecision decision) {
        var routed = 0;
        var logger = new CaptureLogger();
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { Interlocked.Increment(ref routed); return Task.FromResult(decision); },
            agentId: AgentId,
            logger: logger);
        return (bridge, () => Volatile.Read(ref routed), logger);
    }

    const string MultiSelectBoundedSchema = """{"type":"object","properties":{"areas":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string","enum":["x","y","z"]}}}}""";
    const string MultiSelectMinTwoSchema  = """{"type":"object","properties":{"areas":{"type":"array","minItems":2,"items":{"type":"string","enum":["x","y","z"]}}}}""";

    static AcpInteractionDecision MultiDecision(string[]? ids, string? scalar = null) =>
        new("answered", scalar, null, null, null, null, SelectedOptionIds: ids);

    // ── Stabilized elicitation: pre-routing gate matrix ─────────────────────────────────────────
    //
    // EVERY pre-routing cancel must (a) answer the exact content-free stabilized cancel shape,
    // (b) never invoke the server delegate, and (c) log the snake_case reason. Frames are the
    // SDK-verdict-checked fixtures (see test-fixtures/acp-elicitation/generate.mjs).

    [Test]
    [Arguments(ElicitationFixtures.Params_EmptyMessage, ElicitationFixtures.Reason_Params_EmptyMessage)]
    [Arguments(ElicitationFixtures.Params_WhitespaceMessage, ElicitationFixtures.Reason_Params_WhitespaceMessage)]
    [Arguments(ElicitationFixtures.Params_OverlongMessage, ElicitationFixtures.Reason_Params_OverlongMessage)]
    [Arguments(ElicitationFixtures.Params_UrlMode, ElicitationFixtures.Reason_Params_UrlMode)]
    [Arguments(ElicitationFixtures.Params_RequestScoped, ElicitationFixtures.Reason_Params_RequestScoped)]
    [Arguments(ElicitationFixtures.Params_UnknownMode, ElicitationFixtures.Reason_Params_UnknownMode)]
    [Arguments(ElicitationFixtures.Params_MissingMode, ElicitationFixtures.Reason_Params_MissingMode)]
    [Arguments(ElicitationFixtures.Params_MissingMessage, ElicitationFixtures.Reason_Params_MissingMessage)]
    [Arguments(ElicitationFixtures.Params_NullMessage, ElicitationFixtures.Reason_Params_NullMessage)]
    [Arguments(ElicitationFixtures.Params_NonStringMessage, ElicitationFixtures.Reason_Params_NonStringMessage)]
    [Arguments(ElicitationFixtures.Params_MissingRequestedSchema, ElicitationFixtures.Reason_Params_MissingRequestedSchema)]
    [Arguments(ElicitationFixtures.Params_JsonNullRequestId, ElicitationFixtures.Reason_Params_JsonNullRequestId)]
    public async Task ElicitationGate_Cancels_WithoutRouting_AndLogsReason(string paramsJson, string expectedReason) {
        var (bridge, routed, logger) = GateBridge(new AcpInteractionDecision("answered", "x", null, null, null, null));

        var result = await bridge.HandleAsync(ElicitationRequest(paramsJson), CancellationToken.None);

        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        await Assert.That(routed()).IsEqualTo(0);
        await Assert.That(logger.Entries).Contains(e =>
            e.Message.Contains("cancelled before routing") && e.Message.Contains($"reason={expectedReason}"));
    }

    [Test]
    public async Task ElicitationGate_MissingParams_CancelsAsMalformedRequest() {
        var (bridge, routed, logger) = GateBridge(new AcpInteractionDecision("answered", "x", null, null, null, null));

        var result = await bridge.HandleAsync(new AcpRequest(1, "elicitation/create", Params: null), CancellationToken.None);

        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        await Assert.That(routed()).IsEqualTo(0);
        await Assert.That(logger.Entries).Contains(e => e.Message.Contains("reason=malformed_request"));
    }

    /// <summary>Scope precedence: a frame carrying BOTH a usable sessionId and a requestId is
    /// session-scoped and ROUTES (the request-scoped cancel only applies when no usable sessionId
    /// exists).</summary>
    [Test]
    public async Task ElicitationGate_BothSessionAndRequestId_RoutesAsSessionScoped() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { captured = req; return Task.FromResult(new AcpInteractionDecision("answered", "a", null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var result = await bridge.HandleAsync(
            ElicitationRequest(ElicitationFixtures.Params_BothSessionAndRequestId), CancellationToken.None);

        await Assert.That(captured!.Value.AcpSessionId).IsEqualTo("fc2e09cf-f4b0-4463-9dc1-bda11268896b");
        await Assert.That(result!.Value.GetProperty("action").GetString()).IsEqualTo("accept");
    }

    /// <summary>An exactly-at-cap message routes — the cap is exclusive at 8 Ki code units.</summary>
    [Test]
    public async Task ElicitationGate_ExactCapMessage_Routes() {
        var (bridge, routed, _) = GateBridge(new AcpInteractionDecision("answered", "a", null, null, null, null));

        var result = await bridge.HandleAsync(
            ElicitationRequest(ElicitationFixtures.Params_ExactCapMessage), CancellationToken.None);

        await Assert.That(routed()).IsEqualTo(1);
        await Assert.That(result!.Value.GetProperty("action").GetString()).IsEqualTo("accept");
    }

    // ── Stabilized elicitation: multi-select bounds + prompt composition ────────────────────────

    /// <summary>The forwarded interaction carries the classifier's EFFECTIVE bounds and the
    /// multi-select flag — the server renders from these, never from the raw schema.</summary>
    [Test]
    public async Task ElicitationMulti_ForwardsEffectiveBoundsAndMultiSelectFlag() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { captured = req; return Task.FromResult(MultiDecision(["x", "y"])); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);

        await Assert.That(captured!.Value.IsMultiSelect).IsTrue();
        await Assert.That(captured.Value.MinSelections).IsEqualTo(1);
        await Assert.That(captured.Value.MaxSelections).IsEqualTo(2);
        await Assert.That(captured.Value.Options!.Select(o => o.OptionId).ToArray()).IsEquivalentTo(new[] { "x", "y", "z" });
    }

    /// <summary>An accept carries ALL selected ids — not the first, not a subset.</summary>
    [Test]
    public async Task ElicitationMulti_Accept_CarriesAllSelectedIds() {
        var (bridge, _, _) = GateBridge(MultiDecision(["x", "y"]));

        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);

        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"accept","content":{"areas":["x","y"]}}""");
    }

    [Test]
    public async Task ElicitationMulti_BelowMinimum_Cancels() {
        var (bridge, _, _) = GateBridge(MultiDecision([]));
        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    [Test]
    public async Task ElicitationMulti_AboveMaximum_Cancels() {
        var (bridge, _, _) = GateBridge(MultiDecision(["x", "y", "z"]));
        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    /// <summary>Repeat ids collapse BEFORE the bounds check — ["x","x"] is one selection.</summary>
    [Test]
    public async Task ElicitationMulti_DuplicateIds_DedupThenBoundsCheck() {
        var (bridge, _, _) = GateBridge(MultiDecision(["x", "x"]));
        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"accept","content":{"areas":["x"]}}""");
    }

    [Test]
    public async Task ElicitationMulti_UnknownSelectedId_Cancels() {
        var (bridge, _, logger) = GateBridge(MultiDecision(["x", "not-offered"]));
        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        // Post-routing defensive cancel: the answered log must NOT fire.
        await Assert.That(logger.Entries).DoesNotContain(e => e.Message.Contains("elicitation answered"));
    }

    /// <summary>Old-server compatibility: a scalar-only decision wraps into a one-element accept —
    /// but ONLY when the effective minimum admits it.</summary>
    [Test]
    public async Task ElicitationMulti_ScalarFallback_AcceptsWhenMinIsOne() {
        var (bridge, _, _) = GateBridge(MultiDecision(ids: null, scalar: "x"));
        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"accept","content":{"areas":["x"]}}""");
    }

    /// <summary>With an effective minimum above one, a scalar-only decision CANCELS rather than
    /// emitting a below-minimum accept — the fallback obeys the same bounds check.</summary>
    [Test]
    public async Task ElicitationMulti_ScalarFallback_CancelsWhenMinAboveOne() {
        var (bridge, _, _) = GateBridge(MultiDecision(ids: null, scalar: "x"));
        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectMinTwoSchema)), CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    [Test]
    public async Task ElicitationMulti_NullListAndNullScalar_Cancels() {
        var (bridge, _, _) = GateBridge(MultiDecision(ids: null, scalar: null));
        var result = await bridge.HandleAsync(ElicitationRequest(FormParams(MultiSelectBoundedSchema)), CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    /// <summary>Prompt composition: message leads, then the property's title and description,
    /// blank-line-joined, case-sensitive-distinct (a duplicate segment is dropped).</summary>
    [Test]
    public async Task ElicitationPrompt_ComposesMessageTitleDescription_DroppingDuplicates() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { captured = req; return Task.FromResult(new AcpInteractionDecision("answered", "a", null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"choice":{"type":"string","title":"The title","description":"The description","enum":["a","b"]}}}""")),
            CancellationToken.None);
        await Assert.That(captured!.Value.Prompt).IsEqualTo("Pick\n\nThe title\n\nThe description");

        await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"choice":{"type":"string","title":"Pick","enum":["a","b"]}}}""")),
            CancellationToken.None);
        await Assert.That(captured!.Value.Prompt).IsEqualTo("Pick"); // title == message → dropped
    }

    /// <summary>Large-but-renderable composition: the routed prompt equals the segment sum exactly
    /// and sits under the loose bound (message cap + schema cap + separators).</summary>
    [Test]
    public async Task ElicitationPrompt_LargeRenderable_EqualsSegmentSum_WithinLooseBound() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { captured = req; return Task.FromResult(new AcpInteractionDecision("answered", "a", null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var message = new string('m', AcpInteractionBridge.MaxElicitationMessageCodeUnits);
        var title   = new string('t', 1000);
        var desc    = new string('d', 1000);
        var schema  = """{"type":"object","properties":{"choice":{"type":"string","title":"@TITLE@","description":"@DESC@","enum":["a","b"]}}}"""
            .Replace("@TITLE@", title).Replace("@DESC@", desc);
        var frame = """{"sessionId":"@SID@","message":"@MSG@","mode":"form","requestedSchema":@SCHEMA@}"""
            .Replace("@SID@", AcpSessionId).Replace("@MSG@", message).Replace("@SCHEMA@", schema);

        await bridge.HandleAsync(ElicitationRequest(frame), CancellationToken.None);

        var prompt = captured!.Value.Prompt!;
        await Assert.That(prompt.Length).IsEqualTo(message.Length + 2 + title.Length + 2 + desc.Length);
        await Assert.That(prompt.Length <= AcpInteractionBridge.MaxElicitationMessageCodeUnits + 32 * 1024 + 4).IsTrue();
    }

    // ── Stabilized elicitation: single-select + free-text decision mapping ──────────────────────

    [Test]
    public async Task ElicitationSingle_NullSelectedId_Cancels() {
        var (bridge, _, _) = GateBridge(new AcpInteractionDecision("answered", null, null, null, null, null));
        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes","no"]}}}""")),
            CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    /// <summary>An offered empty-string id is a legitimate answer — null, not emptiness, is the gate.</summary>
    [Test]
    public async Task ElicitationSingle_OfferedEmptyStringId_Accepts() {
        var (bridge, _, _) = GateBridge(new AcpInteractionDecision("answered", "", null, null, null, null));
        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"choice":{"type":"string","enum":["","real"]}}}""")),
            CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"accept","content":{"choice":""}}""");
    }

    [Test]
    public async Task ElicitationSingle_NonAnsweredOutcome_Cancels() {
        var (bridge, _, _) = GateBridge(new AcpInteractionDecision("cancel", "yes", null, null, null, null));
        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes","no"]}}}""")),
            CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    [Test]
    public async Task ElicitationFreeText_WhitespaceAnswer_Cancels() {
        var (bridge, _, _) = GateBridge(new AcpInteractionDecision("answered", null, null, null, " \t ", null));
        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"name":{"type":"string"}}}""")),
            CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    // ── Stabilized elicitation: transport failures ──────────────────────────────────────────────

    [Test]
    public async Task ElicitationTransport_DelegateThrows_Cancels() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => throw new InvalidOperationException("boom"),
            agentId: AgentId,
            logger: NullLogger.Instance);
        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes"]}}}""")),
            CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    [Test]
    public async Task ElicitationTransport_OperationCanceled_Cancels() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromCanceled<AcpInteractionDecision>(new CancellationToken(true)),
            agentId: AgentId,
            logger: NullLogger.Instance);
        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes"]}}}""")),
            CancellationToken.None);
        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
    }

    // ── Stabilized elicitation: metric-path positive controls ───────────────────────────────────
    //
    // The reason LOG is asserted throughout the gate matrix; these pin the METRIC half of the
    // cancel contract from the bridge path itself (the AcpMetricsTests listener test only proves
    // the metric API works — it cannot detect a bridge branch that forgets to call it).
    // Presence (not count) is asserted, so concurrent tests emitting the same reason can't break
    // these; the reason tag filter keeps unrelated emissions out.

    static (MeterListener Listener, Func<bool> Observed) ElicitationMetricListener(string reason) {
        var observed = false;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => {
            if (instrument.Meter.Name == "Capacitor.Cli.Daemon.Acp" && instrument.Name == "acp.elicitation_unrenderable")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => {
            foreach (var tag in tags) {
                if (tag.Key == "reason" && (tag.Value?.ToString()) == reason)
                    observed = true;
            }
        });
        listener.Start();
        return (listener, () => Volatile.Read(ref observed));
    }

    [Test]
    public async Task ElicitationGate_UrlMode_IncrementsReasonTaggedMetric() {
        var (listener, observed) = ElicitationMetricListener("url_mode");
        using var _ = listener;
        var (bridge, _, _) = GateBridge(new AcpInteractionDecision("answered", "x", null, null, null, null));

        await bridge.HandleAsync(ElicitationRequest(ElicitationFixtures.Params_UrlMode), CancellationToken.None);

        await Assert.That(observed()).IsTrue();
    }

    [Test]
    public async Task AutoApprove_Elicitation_IncrementsUnattendedDeclinedMetric() {
        var (listener, observed) = ElicitationMetricListener("unattended_declined");
        using var _ = listener;
        var (bridge, calls) = AutoApproveBridge();

        await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes"]}}}""", "Proceed?")),
            CancellationToken.None);

        await Assert.That(observed()).IsTrue();
        await Assert.That(calls()).IsEqualTo(0);
    }

    [Test]
    public async Task FailPolicy_Elicitation_IncrementsUnattendedForbiddenMetric() {
        var (listener, observed) = ElicitationMetricListener("unattended_forbidden");
        using var _ = listener;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => throw new InvalidOperationException("must not be called"),
            agentId: AgentId,
            logger: NullLogger.Instance,
            unattendedPolicy: AcpUnattendedInteractionPolicy.Fail,
            unexpectedUnattendedInteraction: _ => { });

        var result = await bridge.HandleAsync(
            ElicitationRequest(FormParams("""{"type":"object","properties":{"proceed":{"type":"string","enum":["yes"]}}}""", "Proceed?")),
            CancellationToken.None);

        await Assert.That(result!.Value.GetRawText()).IsEqualTo("""{"action":"cancel"}""");
        await Assert.That(observed()).IsTrue();
    }

    // ── AllowlistedAutoApprove ───────────────────────────────────────────────────────────────────
    //
    // The policy that exists because neither neighbour works for Kiro: Fail assumes a scoped-trust
    // reviewer raises no frame (measurably false — it intermittently prompts for a tool in its own
    // trust list), and AutoApprove does not inspect the tool at all.

    const string Admitted = "@kcap-flow-result-abc/submit_review_result";

    static JsonElement PermissionParamsFor(string title, string optionsJson) =>
        JsonDocument.Parse(
            $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"{{title}}"},"options":{{optionsJson}}}""")
            .RootElement.Clone();

    static (AcpInteractionBridge Bridge, List<string> Reaped, Func<int> Routed) AdmissionBridge() {
        var routed = 0;
        var reaped = new List<string>();

        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => {
                Interlocked.Increment(ref routed);
                return Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null));
            },
            agentId: AgentId,
            logger: NullLogger.Instance,
            unattendedPolicy: AcpUnattendedInteractionPolicy.AllowlistedAutoApprove,
            unexpectedUnattendedInteraction: reaped.Add,
            admittedToolIds: new HashSet<string>(StringComparer.Ordinal) { Admitted });

        return (bridge, reaped, () => routed);
    }

    /// <summary>The frame this policy exists for: the reviewer's own result tool, approved without a
    /// human and without reaping.</summary>
    [Test]
    public async Task AllowlistedAutoApprove_AdmittedTool_IsApprovedWithoutRoutingOrReaping() {
        var (bridge, reaped, routed) = AdmissionBridge();

        var result = await bridge.HandleAsync(
            new AcpRequest(1, "session/request_permission",
                PermissionParamsFor($"Running: {Admitted}",
                    """[{"optionId":"allow-once","name":"Yes","kind":"allow_once"}]""")),
            CancellationToken.None);

        await Assert.That(result!.Value.GetProperty("outcome").GetProperty("outcome").GetString())
            .IsEqualTo("selected");
        await Assert.That(reaped).IsEmpty();
        await Assert.That(routed()).IsEqualTo(0);
    }

    /// <summary>The control, and what separates this policy from AutoApprove: a tool this launch did
    /// not inject is reaped, exactly as Fail would.</summary>
    [Test]
    public async Task AllowlistedAutoApprove_UnadmittedTool_IsReaped() {
        var (bridge, reaped, routed) = AdmissionBridge();

        var result = await bridge.HandleAsync(
            new AcpRequest(1, "session/request_permission",
                PermissionParamsFor("Running: @kcap-flows/start_flow",
                    """[{"optionId":"allow-once","name":"Yes","kind":"allow_once"}]""")),
            CancellationToken.None);

        await Assert.That(result!.Value.GetProperty("outcome").GetProperty("outcome").GetString())
            .IsEqualTo("cancelled");
        await Assert.That(reaped).IsEquivalentTo(["session/request_permission"]);
        await Assert.That(routed()).IsEqualTo(0);
    }

    /// <summary>An admitted tool with no identifiable allow option is still reaped: guessing among
    /// unrecognised options is where a wrong pick grants something nobody asked for.</summary>
    [Test]
    public async Task AllowlistedAutoApprove_AdmittedToolWithNoAllowOption_IsReaped() {
        var (bridge, reaped, _) = AdmissionBridge();

        await bridge.HandleAsync(
            new AcpRequest(1, "session/request_permission",
                PermissionParamsFor($"Running: {Admitted}", "[]")),
            CancellationToken.None);

        await Assert.That(reaped).IsEquivalentTo(["session/request_permission"]);
    }

    /// <summary>Every NON-permission method under this policy behaves exactly as Fail — the tool
    /// admission is about permission frames only, and an elicitation has no tool to admit.</summary>
    [Test]
    [Arguments("elicitation/create")]
    [Arguments("vendor/unknown_interaction")]
    public async Task AllowlistedAutoApprove_NonPermissionMethods_AreReapedLikeFail(string method) {
        var (bridge, reaped, routed) = AdmissionBridge();

        await bridge.HandleAsync(
            new AcpRequest(1, method, PermissionParamsFor($"Running: {Admitted}", "[]")),
            CancellationToken.None);

        await Assert.That(reaped).IsEquivalentTo([method]);
        await Assert.That(routed()).IsEqualTo(0);
    }
}
