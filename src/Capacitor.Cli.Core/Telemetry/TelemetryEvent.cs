using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>One captured event. Properties are a <see cref="JsonObject"/> rather than a typed
/// record because the property set varies per event name; serialisation goes through
/// <c>JsonNode.ToJsonString()</c>, which is AOT-safe.</summary>
public sealed record TelemetryEvent(string Name, JsonObject Properties, DateTimeOffset Timestamp);
