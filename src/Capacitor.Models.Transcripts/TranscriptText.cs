using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts;

public static class TranscriptText {
    public static string JoinTextBlocks(JsonElement array, string blockType, string textProperty = "text") {
        var sb = new StringBuilder();
        foreach (var block in array.EnumerateArray()) {
            if (block.Str("type") != blockType || block.Str(textProperty) is not { } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    public static Struct StructOf(JsonElement obj) => Struct.Parser.ParseJson(obj.GetRawText());

    public static Struct Wrap(string property, JsonElement value) {
        var s = new Struct();
        s.Fields[property] = Value.Parser.ParseJson(value.GetRawText());
        return s;
    }

    public static Struct Wrap(string property, string value) {
        var s = new Struct();
        s.Fields[property] = Value.ForString(value);
        return s;
    }
}
