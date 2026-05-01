using System.Text.Json;
using System.Text.Json.Serialization;
using kapacitor;

namespace kapacitor.Tests.Unit;

/// <summary>
/// Locks in the SignalR JSON wire format for <see cref="LaunchAgentCommand"/>:
/// the server (Kurrent.Capacitor) configures its SignalR JSON protocol with
/// <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c> and snake_case
/// property names, so <see cref="LaunchKind"/> rides the wire as a camelCase
/// string. The daemon's <see cref="KapacitorJsonContext"/> must be able to
/// parse that — historically it couldn't, and the SignalR client silently
/// dropped every <c>LaunchAgent</c> invocation (DEV-1665).
/// </summary>
public class LaunchAgentCommandWireFormatTests {
    /// <summary>
    /// Mirrors src/Kurrent.Capacitor/Program.cs SignalR <c>AddJsonProtocol</c>
    /// configuration. If the server changes its options, update this here too —
    /// the test is meaningless if it doesn't match production.
    /// </summary>
    static readonly JsonSerializerOptions ServerWireOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Test]
    public async Task DaemonDeserialises_DefaultKind() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "abc12345",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, KapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.Default);
        await Assert.That(parsed.AgentId).IsEqualTo("abc12345");
    }

    [Test]
    public async Task DaemonDeserialises_ReviewKind_WithReviewInfo() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "def67890",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/tmp/repo",
            Tools:         null,
            AttachmentIds: null,
            Kind:          LaunchKind.Review,
            Review:        new ReviewLaunchInfo("kurrent-io", "kapacitor", 42)
        );

        var wire   = JsonSerializer.Serialize(cmd, ServerWireOptions);
        var parsed = JsonSerializer.Deserialize(wire, KapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchKind.Review);
        await Assert.That(parsed.Review).IsNotNull();
        await Assert.That(parsed.Review!.Value.Owner).IsEqualTo("kurrent-io");
        await Assert.That(parsed.Review!.Value.Repo).IsEqualTo("kapacitor");
        await Assert.That(parsed.Review!.Value.PrNumber).IsEqualTo(42);
    }

    [Test]
    public async Task ServerWire_EncodesKindAsCamelCaseString() {
        var cmd = new LaunchAgentCommand(
            AgentId:       "x",
            Prompt:        null,
            Model:         "opus",
            Effort:        null,
            RepoPath:      "/r",
            Tools:         null,
            AttachmentIds: null,
            Kind:          LaunchKind.Review
        );

        var wire = JsonSerializer.Serialize(cmd, ServerWireOptions);

        await Assert.That(wire).Contains("\"kind\":\"review\"");
    }
}
