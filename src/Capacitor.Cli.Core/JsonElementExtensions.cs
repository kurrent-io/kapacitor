using System.Text.Json;

namespace Capacitor.Cli.Core;

static class JsonElementExtensions {
    extension(JsonElement el) {
        // The one primitive the property accessors below cannot express: whether the element ITSELF
        // is an object. They all answer about a named property, so a caller guarding a document root
        // (or any element it is about to read properties off) had no helper to reach for and dropped
        // to a raw ValueKind comparison instead.
        public bool IsObject => el.ValueKind == JsonValueKind.Object;

        // Same shape as IsObject, for callers that already have an element in hand (e.g. a property
        // value pulled via TryGetProperty) and need to branch on ITS kind rather than a named
        // property of it — the accessors below only ever answer about a property BY NAME.
        public bool IsString => el.ValueKind == JsonValueKind.String;
        public bool IsArray  => el.ValueKind == JsonValueKind.Array;
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
    }
}
