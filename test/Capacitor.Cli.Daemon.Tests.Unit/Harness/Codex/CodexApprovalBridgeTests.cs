using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// <see cref="CodexApprovalBridge"/> forwards a server-initiated <c>*/requestApproval</c> to an injected
/// "ask the user" delegate and maps the decision back to codex's wire shape — always fail-closed. Tested
/// against the delegate directly (no SignalR). The security invariant: only a recognized affirmative
/// outcome carrying a known option id grants; everything else denies.
/// </summary>
public class CodexApprovalBridgeTests {
    const string AgentId  = "agent-1";
    static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    static CodexApprovalBridge Bridge(
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> requestInteraction,
            TimeSpan? timeout = null) =>
        new(requestInteraction, AgentId, NullLogger.Instance, timeout ?? LongTimeout);

    static CodexApprovalBridge Deciding(AcpInteractionDecision decision, TimeSpan? timeout = null) =>
        Bridge((_, _) => Task.FromResult(decision), timeout);

    static AcpRequest ApprovalRequest(string method, string paramsJson) =>
        new(1, method, JsonDocument.Parse(paramsJson).RootElement.Clone());

    const string CommandMethod = "item/commandExecution/requestApproval";
    const string FileMethod    = "item/fileChange/requestApproval";
    const string PermsMethod   = "item/permissions/requestApproval";
    const string CommandParams = """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1"}""";

    static async Task<string?> Decision(JsonElement? result) =>
        await Task.FromResult(result is { } r && r.TryGetProperty("decision", out var d) ? d.GetString() : null);

    // ── The grant paths (positive controls) ────────────────────────────────────────────────────
    [Test]
    public async Task Command_accept_maps_to_decision_accept() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", "Allow", null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("accept");
    }

    [Test]
    public async Task Command_accept_for_session_maps_to_decision_acceptForSession() {
        var bridge = Deciding(new AcpInteractionDecision("allow_always", "acceptForSession", "Allow for session", null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("acceptForSession");
    }

    [Test]
    public async Task File_change_accept_maps_to_decision_accept() {
        // The file-change method name is by-convention; the suffix match must still route it.
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", "Allow", null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(FileMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("accept");
    }

    // ── Fail-closed paths ───────────────────────────────────────────────────────────────────────
    [Test]
    public async Task Deny_outcome_maps_to_decline() {
        var bridge = Deciding(new AcpInteractionDecision("deny", null, null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Affirmative_outcome_with_unknown_option_id_declines() {
        // Fail-safe: an affirmative outcome whose SelectedOptionId is NOT one we offered must never grant.
        var bridge = Deciding(new AcpInteractionDecision("allow", "somethingElse", null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Affirmative_outcome_with_null_option_id_declines() {
        var bridge = Deciding(new AcpInteractionDecision("allow", null, null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Missing_thread_id_declines_without_asking() {
        var asked = false;
        var bridge = Bridge((_, _) => { asked = true; return Task.FromResult(new AcpInteractionDecision("allow", "accept", null, null, null, null)); });
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, """{"itemId":"item-1"}"""), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
        await Assert.That(asked).IsFalse(); // never correlated → never forwarded to a human
    }

    [Test]
    public async Task Timeout_declines() {
        var bridge = Bridge(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return default; }, TimeSpan.FromMilliseconds(50));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Oversized_timeout_is_clamped_and_does_not_throw() {
        // A misconfigured huge timeout must not throw at CancelAfter (which caps ~24.8 days) — the ctor
        // clamps it, so the normal accept path still works.
        var bridge = Bridge((_, _) => Task.FromResult(new AcpInteractionDecision("allow", "accept", null, null, null, null)),
            timeout: TimeSpan.FromDays(100));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("accept");
    }

    [Test]
    public async Task Delegate_throwing_declines_never_errors() {
        var bridge = Bridge((_, _) => throw new InvalidOperationException("boom"));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline"); // valid body, not a JSON-RPC error
    }

    // ── Permissions grant shape ({permissions,scope}, not {decision}) ────────────────────────────
    [Test]
    public async Task Permissions_grant_accept_echoes_requested_profile() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        var paramsJson = """{"threadId":"thread-1","itemId":"item-1","permissions":{"network":{"allowed":true}}}""";
        var result = await bridge.HandleAsync(ApprovalRequest(PermsMethod, paramsJson), CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var r = result!.Value;
        await Assert.That(r.TryGetProperty("decision", out _)).IsFalse();
        await Assert.That(r.GetProperty("scope").GetString()).IsEqualTo("turn");
        await Assert.That(r.GetProperty("permissions").GetProperty("network").GetProperty("allowed").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Permissions_grant_deny_returns_empty_grant_not_a_decision() {
        var bridge = Deciding(new AcpInteractionDecision("deny", null, null, null, null, null));
        var paramsJson = """{"threadId":"thread-1","itemId":"item-1","permissions":{"network":{"allowed":true}}}""";
        var result = await bridge.HandleAsync(ApprovalRequest(PermsMethod, paramsJson), CancellationToken.None);

        var r = result!.Value;
        await Assert.That(r.TryGetProperty("decision", out _)).IsFalse();          // never the malformed decision shape
        await Assert.That(r.GetProperty("permissions").EnumerateObject().Any()).IsFalse(); // empty grant
        await Assert.That(r.GetProperty("scope").GetString()).IsEqualTo("turn");
    }

    [Test]
    public async Task Permissions_grant_accept_over_non_object_profile_falls_to_empty_deny() {
        // Even an affirmative decision must not grant a profile we can't read back as an object — echoing
        // {} at the affirmative "session" scope could over-grant if the server treats it as defaults.
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        var paramsJson = """{"threadId":"thread-1","itemId":"item-1","permissions":"all"}"""; // non-object profile
        var result = await bridge.HandleAsync(ApprovalRequest(PermsMethod, paramsJson), CancellationToken.None);

        var r = result!.Value;
        await Assert.That(r.GetProperty("permissions").EnumerateObject().Any()).IsFalse(); // empty, not the string
        await Assert.That(r.GetProperty("scope").GetString()).IsEqualTo("turn");           // deny scope, never "session"
    }

    // ── Non-approval requests keep the always-decline shapes ─────────────────────────────────────
    [Test]
    public async Task Elicitation_request_declines_with_action_shape() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest("session/elicitation/create", """{"threadId":"t"}"""), CancellationToken.None);
        await Assert.That(result!.Value.GetProperty("action").GetString()).IsEqualTo("decline");
    }

    [Test]
    public async Task Unknown_server_request_declines_with_decision_shape() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest("some/other/method", """{"threadId":"t"}"""), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }
}
