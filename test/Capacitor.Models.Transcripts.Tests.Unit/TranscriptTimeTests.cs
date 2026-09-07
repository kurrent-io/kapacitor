namespace Capacitor.Models.Transcripts.Tests.Unit;

public class TranscriptTimeTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_parseable_record_timestamp_is_the_effective_time_and_is_kept_raw() {
        var (at, raw) = TranscriptTime.Resolve("2026-08-26T12:00:00Z", Received);
        await Assert.That(at).IsEqualTo(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        await Assert.That(raw).IsEqualTo("2026-08-26T12:00:00Z");
    }

    [Test]
    public async Task A_missing_timestamp_falls_back_to_the_receive_time_with_no_raw_string() {
        var (at, raw) = TranscriptTime.Resolve(null, Received);
        await Assert.That(at).IsEqualTo(Received);
        await Assert.That(raw).IsNull();
    }

    [Test]
    public async Task An_unparseable_timestamp_falls_back_but_keeps_the_raw_string() {
        var (at, raw) = TranscriptTime.Resolve("yesterday", Received);
        await Assert.That(at).IsEqualTo(Received);
        await Assert.That(raw).IsEqualTo("yesterday");
    }
}
