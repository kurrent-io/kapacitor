using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the hello frames. snake_case on the wire; shared verbatim by the
/// daemon, the CLI, and the desktop app. Deserialization ignores unmapped members (STJ
/// default) — additive fields must never break an older client.
public sealed record ClientHelloDto(string? ClientName, string? ClientVersion);

/// <summary>
/// <see cref="Capabilities"/> is <see langword="null"/> = field absent (older daemon); treat as
/// empty. STJ leaves a missing reference-typed member at its default rather than throwing, so a
/// non-nullable declaration here would be a lie a client could NRE on the moment it dereferences an
/// older daemon's reply.
/// </summary>
public sealed record HelloReplyDto(
    int ProtocolVersion, string DaemonVersion, string DaemonName, List<string>? Capabilities);

/// <summary>Single source of truth for the local control socket hello protocol version this build
/// speaks — both what <c>HandleHelloAsync</c> reports and what a per-verb hello probe (e.g.
/// <c>service install --verify</c>) requires of a freshly-installed daemon.</summary>
public static class HelloProtocol {
    public const int CurrentVersion = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ClientHelloDto))]
[JsonSerializable(typeof(HelloReplyDto))]
public partial class HelloIpcJsonContext : JsonSerializerContext;
