using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the hello frames. snake_case on the wire; shared verbatim by the
/// daemon, the CLI, and the desktop app. Deserialization ignores unmapped members (STJ
/// default) — additive fields must never break an older client.
public sealed record ClientHelloDto(string? ClientName, string? ClientVersion);

public sealed record HelloReplyDto(
    int ProtocolVersion, string DaemonVersion, string DaemonName, List<string> Capabilities);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ClientHelloDto))]
[JsonSerializable(typeof(HelloReplyDto))]
public partial class HelloIpcJsonContext : JsonSerializerContext;
