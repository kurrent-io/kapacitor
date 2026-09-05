using System.Text.Json;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class JsonElementExtensionsTests {
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Prop_returns_any_kind_and_null_when_absent_or_not_an_object() {
        var el = Parse("""{"arr":[1,2],"num":3,"nil":null,"str":"s"}""");

        await Assert.That(el.Prop("arr")!.Value.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(el.Prop("num")!.Value.GetInt32()).IsEqualTo(3);
        await Assert.That(el.Prop("nil")!.Value.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(el.Prop("str")!.Value.GetString()).IsEqualTo("s");
        await Assert.That(el.Prop("absent")).IsNull();
        await Assert.That(Parse("[1]").Prop("x")).IsNull();
        await Assert.That(Parse("\"scalar\"").Prop("x")).IsNull();
    }
}
