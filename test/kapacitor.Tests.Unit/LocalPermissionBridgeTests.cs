using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using kapacitor.Daemon;
using kapacitor.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace kapacitor.Tests.Unit;

public class LocalPermissionBridgeTests {
    static (LocalPermissionBridge bridge, FakeServerConnection server) CreateBridge(
        Func<string, string?, JsonElement?, JsonElement?, CancellationToken, Task<PermissionDecision>>? respond = null
    ) {
        var server = new FakeServerConnection(respond);
        var bridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance);

        return (bridge, server);
    }

    // Short HttpClient timeout so a misbehaving listener fails the test in seconds rather than
    // stalling the suite on the default ~100s. Bridge replies are loopback and immediate, so
    // anything past 5s indicates a regression worth surfacing fast.
    static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(5) };

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task StartAsync_ExposesLoopbackBaseUrlWithToken() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);

            await Assert.That(bridge.BaseUrl).IsNotNull();
            var uri = new Uri(bridge.BaseUrl!);

            await Assert.That(uri.Host).IsEqualTo("127.0.0.1");
            await Assert.That(uri.Scheme).IsEqualTo("http");

            // Path is "/<32-char hex token>"
            var token = uri.AbsolutePath.Trim('/');
            await Assert.That(token.Length).IsEqualTo(32);
            await Assert.That(token.All(Uri.IsHexDigit)).IsTrue();
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task PostingToWrongTokenReturns404() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);

            var uri      = new Uri(bridge.BaseUrl!);
            var bogusUrl = $"http://127.0.0.1:{uri.Port}/{new string('0', 32)}/permission-request";

            using var client   = CreateClient();
            using var response = await client.PostAsync(bogusUrl, JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task PostingToWrongPathReturns404() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/something-else", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task GetReturns404() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.GetAsync($"{bridge.BaseUrl}/permission-request");

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task MalformedJsonReturns400() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var content  = new StringContent("{ this is not json", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"{bridge.BaseUrl}/permission-request", content);

            await Assert.That((int)response.StatusCode).IsEqualTo(400);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task MissingSessionIdReturns400() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/permission-request", JsonContent.Create(new { tool_name = "Bash" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(400);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ValidRequestStripsDashesAndForwardsArgsToServer() {
        var (bridge, server) = CreateBridge((sid, tool, input, suggestions, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client = CreateClient();
            var       payload = new {
                session_id             = "11111111-2222-3333-4444-555555555555",
                tool_name              = "Bash",
                tool_input             = new { command = "ls" },
                permission_suggestions = new { reason = "ok" }
            };
            using var response = await client.PostAsync($"{bridge.BaseUrl}/permission-request", JsonContent.Create(payload));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);

            var call = server.Calls[0];
            await Assert.That(call.SessionId).IsEqualTo("11111111222233334444555555555555");
            await Assert.That(call.ToolName).IsEqualTo("Bash");
            await Assert.That(call.ToolInput?.GetProperty("command").GetString()).IsEqualTo("ls");
            await Assert.That(call.Suggestions?.GetProperty("reason").GetString()).IsEqualTo("ok");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ResponseShapeMirrorsClaudeHookSchema() {
        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/permission-request", JsonContent.Create(new { session_id = "abc" }));

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            var hookOutput = doc.RootElement.GetProperty("hookSpecificOutput");
            await Assert.That(hookOutput.GetProperty("hookEventName").GetString()).IsEqualTo("PermissionRequest");
            await Assert.That(hookOutput.GetProperty("decision").GetProperty("behavior").GetString()).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ApplyPermissionsAndUpdatedInputAreCopiedIntoDecision() {
        using var apDoc = JsonDocument.Parse("""{"allow":["Bash(ls:*)"]}""");
        using var uiDoc = JsonDocument.Parse("""{"command":"ls -la"}""");

        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", apDoc.RootElement.Clone(), uiDoc.RootElement.Clone()))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/permission-request", JsonContent.Create(new { session_id = "abc" }));

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            var decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("applyPermissions").GetProperty("allow")[0].GetString()).IsEqualTo("Bash(ls:*)");
            await Assert.That(decision.GetProperty("updatedInput").GetProperty("command").GetString()).IsEqualTo("ls -la");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ServerFailureFallsBackToDeny() {
        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            throw new InvalidOperationException("hub call broke")
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            var decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("behavior").GetString()).IsEqualTo("deny");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task StopAsyncReleasesPort() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);

            var port = new Uri(bridge.BaseUrl!).Port;
            await bridge.StopAsync(CancellationToken.None);

            // After stop, the port should accept a fresh bind. If StopAsync didn't release
            // it, this would either throw or hang.
            var probe = new TcpListener(IPAddress.Loopback, port);
            try {
                probe.Start();
            } finally {
                probe.Stop();
            }
        } finally {
            // Ensure DisposeAsync runs even if the probe.Start() above throws — otherwise the
            // listener / CTS leak into later tests in the same process.
            await bridge.DisposeAsync();
        }
    }
}

/// <summary>
/// Bypasses ServerConnection's HubConnection plumbing so the bridge can be exercised
/// without a real server. RequestPermissionAsync is virtual on the base class.
/// </summary>
sealed class FakeServerConnection : ServerConnection {
    readonly Func<string, string?, JsonElement?, JsonElement?, CancellationToken, Task<PermissionDecision>>? _respond;

    public List<Call> Calls { get; } = [];

    public FakeServerConnection(
        Func<string, string?, JsonElement?, JsonElement?, CancellationToken, Task<PermissionDecision>>? respond
    ) : base(
        new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLogger<ServerConnection>.Instance
    ) {
        _respond = respond;
    }

    public override Task<PermissionDecision> RequestPermissionAsync(
            string            sessionId,
            string?           toolName,
            JsonElement?      toolInput,
            JsonElement?      suggestions,
            CancellationToken ct
        ) {
        Calls.Add(new Call(sessionId, toolName, toolInput, suggestions));

        return _respond is null
            ? Task.FromResult(new PermissionDecision("allow", null, null))
            : _respond(sessionId, toolName, toolInput, suggestions, ct);
    }

    public sealed record Call(string SessionId, string? ToolName, JsonElement? ToolInput, JsonElement? Suggestions);
}
