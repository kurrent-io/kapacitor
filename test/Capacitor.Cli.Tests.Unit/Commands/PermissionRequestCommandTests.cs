using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Bare because KCAP_DAEMON_URL is read by more than one production command (also
// CodexHookCommand) and inherited by spawned children, so no cohort of key-holders
// can exclude its readers.
[NotInParallel]
public class PermissionRequestCommandTests {
    const string EnvVar = "KCAP_DAEMON_URL";

    [Test]
    public async Task ReturnsFalseWhenEnvVarIsUnset() {
        using var _ = EnvScope.Exclusive(EnvVar, null);

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task ReturnsFalseWhenEnvVarIsEmpty() {
        using var _ = EnvScope.Exclusive(EnvVar, "");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task AcceptsLoopbackHttpAndTrimsTrailingSlash() {
        using var _ = EnvScope.Exclusive(EnvVar, "http://127.0.0.1:51234/abc/");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsTrue();
        await Assert.That(url).IsEqualTo("http://127.0.0.1:51234/abc");
    }

    [Test]
    public async Task RejectsLocalhostDnsName() {
        // We require literal 127.0.0.1 — "localhost" could resolve to non-loopback in a misconfigured env.
        using var _ = EnvScope.Exclusive(EnvVar, "http://localhost:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsNonLoopbackHost() {
        using var _ = EnvScope.Exclusive(EnvVar, "http://example.com:8080/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsHttpsLoopback() {
        // The daemon bridge is plain http on loopback — https implies a different
        // endpoint and shouldn't be accepted via this env var.
        using var _ = EnvScope.Exclusive(EnvVar, "https://127.0.0.1:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsMalformedUrl() {
        using var _ = EnvScope.Exclusive(EnvVar, "not-a-url");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task Bridge_payload_adds_agent_id_and_cwd_and_leaves_the_server_shape_alone() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","tool_name":"Bash","tool_input":{"command":"ls"},"permission_suggestions":null,"cwd":"/repo","transcript_path":"/t"}""")!;
        var bridge = PermissionRequestCommand.BuildBridgePayload(node, "abc", "agent-1");
        await Assert.That(bridge["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(bridge["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(bridge["tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(bridge["transcript_path"]).IsNull();

        var withoutAgent = PermissionRequestCommand.BuildBridgePayload(node, "abc", null);
        await Assert.That(withoutAgent["agent_id"]).IsNull();
        await Assert.That(withoutAgent["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
    }
}
