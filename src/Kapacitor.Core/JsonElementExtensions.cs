using System.Text.Json;

namespace kapacitor;

static class JsonElementExtensions {
    extension(JsonElement el) {
        public string? Str(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public long? Num(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.TryGetInt64(out var n) ? n : null;

        public JsonElement? Obj(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        public JsonElement? Arr(string property) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;
    }
}
