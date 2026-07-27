using System.Text.Json;

namespace Capacitor.Cli.Core;

static class JsonElementExtensions {
    extension(JsonElement el) {
        public string? Str(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // Guard the ValueKind before TryGetInt64: it THROWS on a non-Number element (string,
        // bool, null, object, array), so an unguarded call lets a schema-drift frame bubble an
        // exception up and take out the whole notification instead of dropping one bad field.
        // Same defensive contract as Str/Obj/Arr above — a wrong-typed field reads as absent.
        // A fractional number still returns null via TryGetInt64 rather than truncating.
        public long? Num(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

        public JsonElement? Obj(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        public JsonElement? Arr(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;
    }
}
