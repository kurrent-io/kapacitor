using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class KcapMcpRegistryReviewFlowTests {
    [Test]
    public async Task Resolve_accepts_read_only_servers_case_insensitively() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["Kcap-Review"], out var servers, out var rejected);

        await Assert.That(ok).IsTrue();
        await Assert.That(rejected).IsNull();
        await Assert.That(servers.Length).IsEqualTo(1);
        await Assert.That(servers[0]).IsEqualTo("kcap-review");   // canonical id
    }

    [Test]
    public async Task Resolve_dedupes_and_keeps_multiple_read_servers() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(
            ["kcap-review", "kcap-sessions", "kcap-review"], out var servers, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(servers.Length).IsEqualTo(2);
        await Assert.That(servers).Contains("kcap-review");
        await Assert.That(servers).Contains("kcap-sessions");
    }

    [Test]
    public async Task Resolve_rejects_flow_starting_server() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-review", "kcap-flows"], out var servers, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("kcap-flows");
        await Assert.That(servers).IsEmpty();
    }

    [Test]
    public async Task Resolve_rejects_write_server_kcap_memory() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-memory"], out _, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("kcap-memory");
    }

    [Test]
    public async Task Resolve_rejects_write_server_kcap_workitems() {
        // kcap-workitems is registered on every harness, but it is a writer: it must never be
        // auto-approved for an unattended review-flow reviewer (only kcap-review/kcap-sessions are).
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-workitems"], out _, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("kcap-workitems");
    }

    [Test]
    public async Task Resolve_rejects_unknown_server() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["not-a-server"], out _, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("not-a-server");
    }

    [Test]
    public async Task Resolve_treats_reserved_flow_result_id_as_a_satisfied_no_op() {
        // kcap-flow-result is always injected by the launcher and is not a registry entry; the
        // server's dynamic-flow policy legitimately lists it, so it must be accepted (not rejected)
        // and NOT re-emitted in the resolved servers. Every reviewer runtime shares this.
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(
            ["kcap-flow-result", "KCAP-FLOW-RESULT", "kcap-review"], out var servers, out var rejected);

        await Assert.That(ok).IsTrue();
        await Assert.That(rejected).IsNull();
        await Assert.That(servers).IsEquivalentTo(["kcap-review"]);
    }

    [Test]
    public async Task Resolve_null_or_empty_is_ok_empty() {
        var ok1 = KcapMcpRegistry.TryResolveReviewFlowAllowlist(null, out var s1, out var r1);
        await Assert.That(ok1).IsTrue();
        await Assert.That(s1).IsEmpty();
        await Assert.That(r1).IsNull();

        var ok2 = KcapMcpRegistry.TryResolveReviewFlowAllowlist([], out var s2, out _);
        await Assert.That(ok2).IsTrue();
        await Assert.That(s2).IsEmpty();
    }

    // Contract guard (static half): every auto-approvable server carries a tool classification.
    // (The dynamic half — cross-checking each server's live tools/list — is added in Task 4.)
    [Test]
    public async Task Every_auto_approvable_server_has_a_tool_classification() {
        foreach (var srv in KcapMcpRegistry.ReviewFlowAutoApprovableServers) {
            await Assert.That(KcapMcpRegistry.ReviewFlowUnattendedSafeTools.ContainsKey(srv)).IsTrue();
            await Assert.That(KcapMcpRegistry.ReviewFlowUnattendedSafeTools[srv]).IsNotEmpty();
        }
    }

    // The classification must never name a flow-starting or non-registered server.
    [Test]
    public async Task Classification_only_covers_auto_approvable_servers() {
        foreach (var srv in KcapMcpRegistry.ReviewFlowUnattendedSafeTools.Keys)
            await Assert.That(KcapMcpRegistry.ReviewFlowAutoApprovableServers.Contains(srv)).IsTrue();
    }

    // ── Reserved result channel tool catalog ─────────────────────────────────────────
    //
    // The ordered catalog on KcapMcpRegistry is the single source of truth for the reserved
    // channel's tools. The flow-result server's tools/list is compared against it DIRECTLY
    // (bidirectional: an advertised tool missing from the catalog fails just like a catalog
    // entry the server no longer advertises), so the next tool addition can't silently leave
    // the bridge auto-approve or the Copilot ACP argv behind.

    [Test]
    public async Task Flow_result_server_advertises_exactly_the_reserved_channel_catalog_in_order() {
        var advertised = McpFlowResultServer.BuildToolsList().Select(t => t.Name).ToArray();
        var catalog    = KcapMcpRegistry.ReservedResultChannelTools.Select(t => t.Name).ToArray();

        await Assert.That(advertised.SequenceEqual(catalog, StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Unattended_safe_set_is_exactly_the_catalogs_safe_names() {
        var safeNames = KcapMcpRegistry.ReservedResultChannelTools
            .Where(t => t.UnattendedSafe)
            .Select(t => t.Name)
            .ToArray();

        await Assert.That(KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Count)
            .IsEqualTo(safeNames.Length);
        foreach (var name in safeNames)
            await Assert.That(KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Contains(name)).IsTrue();
    }

    [Test]
    public async Task Unattended_safe_set_membership_is_case_sensitive() {
        // The set feeds a permission auto-approve, so membership must be exact Ordinal —
        // a case variant of a safe tool name is NOT the safe tool.
        await Assert.That(KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Contains("submit_review_result")).IsTrue();
        await Assert.That(KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Contains("Submit_Review_Result")).IsFalse();
        await Assert.That(KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Contains("SEND_FLOW_MESSAGE")).IsFalse();
    }
}
