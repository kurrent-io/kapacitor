using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core;

/// One transcript line in, zero or more canonical chat events out. Stateless: the consumer
/// orders by arrival and pairs tool calls to results by id itself.
public interface ITranscriptProjection {
    IReadOnlyList<AcpEventEnvelope> Project(string line);
}

/// The one registration site: a vendor's transcript projection lives under Harness/&lt;Vendor&gt;/
/// and is named here, nowhere else.
public static class TranscriptProjection {
    public static ITranscriptProjection? For(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeTranscriptEvents.Instance,
        _        => null,
    };
}

/// Output rules both projections share, so an envelope reads the same whichever vendor wrote it.
internal static class TranscriptProjectionText {
    public const int ToolResultCap = 4096;
    const string CapMarker = "…";

    /// At most ToolResultCap units including the marker; a cut that would split a surrogate
    /// pair drops the high half too, so the result can be one unit short of the cap.
    public static string Cap(string text) {
        if (text.Length <= ToolResultCap) return text;
        var cut = ToolResultCap - CapMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return string.Concat(text.AsSpan(0, cut), CapMarker);
    }

    public static string JoinTextBlocks(JsonElement array, string blockType, string textProperty = "text") {
        var sb = new StringBuilder();
        foreach (var block in array.EnumerateArray()) {
            if (block.Str("type") != blockType || block.Str(textProperty) is not { } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    public static string WrapAsObject(string property, JsonElement value) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName(property);
            value.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string WrapAsObject(string property, string value) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString(property, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
