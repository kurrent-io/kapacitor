using System.Text.Json;

namespace Capacitor.Models.Transcripts;

public static class JsonElementExtensions {
    extension(JsonElement el) {
        // The element's own kind, for a document root or a value already in hand; every accessor
        // below answers about a named property instead.
        public bool IsObject => el.ValueKind == JsonValueKind.Object;
        public bool IsString => el.ValueKind == JsonValueKind.String;
        public bool IsArray  => el.ValueKind == JsonValueKind.Array;
        public bool IsNumber => el.ValueKind == JsonValueKind.Number;
        public bool IsNull   => el.ValueKind == JsonValueKind.Null;

        public string? Str(string property) => el.IsObject && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // Guard the ValueKind before TryGetInt64: it THROWS on a non-Number element (string,
        // bool, null, object, array), so an unguarded call lets a schema-drift frame bubble an
        // exception up and take out the whole notification instead of dropping one bad field.
        // Same defensive contract as Str/Obj/Arr above — a wrong-typed field reads as absent.
        // A fractional number still returns null via TryGetInt64 rather than truncating.
        public long? Num(string property) => el.IsObject && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

        // Only True/False resolve; anything else (missing, wrong-typed) reads as absent — same
        // defensive contract as Str/Num/Obj/Arr, and the nullable result lets a caller that only
        // cares about an explicit `true` write `el.Bool("x") == true` without conflating "false" with
        // "absent".
        public bool? Bool(string property) => el.IsObject && el.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        public JsonElement? Obj(string property) => el.IsObject && el.TryGetProperty(property, out var v) && v.IsObject ? v : null;

        public JsonElement? Arr(string property) => el.IsObject && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

        // The property as-is, whatever its kind — for the one caller that has to copy a value
        // verbatim (a non-object tool input wrapped into an object). Every other read wants a
        // typed accessor above; this one deliberately answers "present" for JSON null too.
        public JsonElement? Prop(string property) => el.IsObject && el.TryGetProperty(property, out var v) ? v : null;
    }
}
