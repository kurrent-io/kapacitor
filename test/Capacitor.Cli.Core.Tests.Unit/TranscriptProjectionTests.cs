namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptProjectionTests {
    [Test]
    public async Task For_is_case_insensitive_and_null_for_an_unknown_vendor() {
        await Assert.That(TranscriptProjection.For("claude")).IsNotNull();
        await Assert.That(TranscriptProjection.For("Claude")).IsSameReferenceAs(TranscriptProjection.For("claude")!);
        await Assert.That(TranscriptProjection.For("gemini")).IsNull();
    }

    [Test]
    public async Task Cap_keeps_at_most_4096_units_including_the_marker_and_never_splits_a_pair() {
        var plain = new string('a', 5000);
        var capped = TranscriptProjectionText.Cap(plain);
        await Assert.That(capped.Length).IsEqualTo(4096);
        await Assert.That(capped[^1]).IsEqualTo('…');

        // 4094 units, then an astral pair straddling the cut position 4095.
        var astral = new string('a', 4094) + "😀" + new string('b', 100);
        var cappedAstral = TranscriptProjectionText.Cap(astral);
        await Assert.That(cappedAstral.Length).IsEqualTo(4095);
        await Assert.That(char.IsHighSurrogate(cappedAstral[^2])).IsFalse();
        await Assert.That(cappedAstral[^1]).IsEqualTo('…');

        await Assert.That(TranscriptProjectionText.Cap("short")).IsEqualTo("short");
        await Assert.That(TranscriptProjectionText.Cap(new string('a', 4096)).Length).IsEqualTo(4096);
    }

    [Test]
    public async Task WrapAsObject_builds_a_json_object_around_any_value() {
        using var doc = System.Text.Json.JsonDocument.Parse("""[1,"x",null]""");
        var fromElement = TranscriptProjectionText.WrapAsObject("input", doc.RootElement);
        await Assert.That(fromElement).IsEqualTo("""{"input":[1,"x",null]}""");
        await Assert.That(System.Text.Json.JsonDocument.Parse(fromElement).RootElement.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Object);

        var fromString = TranscriptProjectionText.WrapAsObject("arguments", "raw \"q\"");
        // Utf8JsonWriter's default encoder escapes a quote as \u0022, not \".
        await Assert.That(fromString).IsEqualTo("""{"arguments":"raw \u0022q\u0022"}""");
        await Assert.That(System.Text.Json.JsonDocument.Parse(fromString).RootElement.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Object);
    }

    [Test]
    public async Task For_registers_codex() {
        await Assert.That(TranscriptProjection.For("CODEX")).IsSameReferenceAs(Capacitor.Cli.Core.Harness.Codex.CodexRolloutEvents.Instance);
    }
}
