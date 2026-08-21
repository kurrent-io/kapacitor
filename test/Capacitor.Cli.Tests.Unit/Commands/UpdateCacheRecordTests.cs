using System.Globalization;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Unit coverage for <see cref="UpdateCommand.UpdateCacheRecord"/>: legacy
/// two-field on-disk compat, round-trip JSON, and the freshness/backoff
/// arithmetic <see cref="UpdateCommand.CheckForUpdateAsync"/>'s decision
/// ladder depends on. See <c>UpdateChannelQueryTests</c> for the end-to-end
/// coverage of the coordinator itself.
/// </summary>
public class UpdateCacheRecordTests {
    [Test]
    public async Task LegacyTwoFieldCache_ReadsAsSuccessRecord() {
        var json = """{"latest_version":"0.11.10","checked_at":"2026-08-08T10:00:00Z"}""";
        var rec  = UpdateCommand.UpdateCacheRecord.Parse(json);

        await Assert.That(rec).IsNotNull();
        await Assert.That(rec!.LatestVersion).IsEqualTo("0.11.10");
        await Assert.That(rec.Failed).IsFalse();
        await Assert.That(rec.AttemptedAt).IsNull();
    }

    [Test]
    public async Task LegacyTwoFieldCache_IsFresh_WithinTtl() {
        var rec = UpdateCommand.UpdateCacheRecord.Parse(
            """{"latest_version":"0.11.10","checked_at":"2026-08-08T10:00:00Z"}""");

        await Assert.That(rec!.IsFresh(
            DateTimeOffset.Parse("2026-08-08T10:00:00Z", CultureInfo.InvariantCulture).AddHours(1),
            TimeSpan.FromHours(24))).IsTrue();
    }

    [Test]
    public async Task Parse_UnparseableJson_ReturnsNull() {
        await Assert.That(UpdateCommand.UpdateCacheRecord.Parse("not json")).IsNull();
        await Assert.That(UpdateCommand.UpdateCacheRecord.Parse("")).IsNull();
    }

    [Test]
    public async Task ToJson_RoundTrips_ThroughParse() {
        var now  = DateTimeOffset.Parse("2026-08-08T10:00:00Z", CultureInfo.InvariantCulture);
        var orig = new UpdateCommand.UpdateCacheRecord("0.11.10", now, AttemptedAt: null, Failed: false);
        var rec  = UpdateCommand.UpdateCacheRecord.Parse(orig.ToJson());

        await Assert.That(rec).IsNotNull();
        await Assert.That(rec!.LatestVersion).IsEqualTo("0.11.10");
        await Assert.That(rec.CheckedAt).IsEqualTo(now);
        await Assert.That(rec.AttemptedAt).IsNull();
        await Assert.That(rec.Failed).IsFalse();
    }

    [Test]
    public async Task FailedRecord_ToJson_RoundTrips_RetainingLatestVersion() {
        var attemptedAt = DateTimeOffset.Parse("2026-08-08T10:00:00Z", CultureInfo.InvariantCulture);
        var orig        = new UpdateCommand.UpdateCacheRecord("0.11.10", CheckedAt: null, attemptedAt, Failed: true);
        var rec         = UpdateCommand.UpdateCacheRecord.Parse(orig.ToJson());

        await Assert.That(rec).IsNotNull();
        await Assert.That(rec!.LatestVersion).IsEqualTo("0.11.10");
        await Assert.That(rec.CheckedAt).IsNull();
        await Assert.That(rec.AttemptedAt).IsEqualTo(attemptedAt);
        await Assert.That(rec.Failed).IsTrue();
    }

    [Test]
    public async Task FailedRecord_WithinBackoffTtl_SkipsNetwork() {
        var rec = new UpdateCommand.UpdateCacheRecord("0.11.10", CheckedAt: DateTimeOffset.UtcNow.AddHours(-30),
                                        AttemptedAt: DateTimeOffset.UtcNow.AddMinutes(-10), Failed: true);

        await Assert.That(rec.InFailureBackoff(DateTimeOffset.UtcNow, TimeSpan.FromHours(1))).IsTrue();
        // stale-while-backoff: the retained last-known version is still served
        await Assert.That(rec.LatestVersion).IsEqualTo("0.11.10");
    }

    [Test]
    public async Task FailedRecord_PastBackoffTtl_NoLongerBacksOff() {
        var rec = new UpdateCommand.UpdateCacheRecord("0.11.10", CheckedAt: null,
                                        AttemptedAt: DateTimeOffset.UtcNow.AddHours(-2), Failed: true);

        await Assert.That(rec.InFailureBackoff(DateTimeOffset.UtcNow, TimeSpan.FromHours(1))).IsFalse();
    }

    [Test]
    public async Task SuccessRecord_NeverInFailureBackoff() {
        var rec = new UpdateCommand.UpdateCacheRecord("0.11.10", CheckedAt: DateTimeOffset.UtcNow,
                                        AttemptedAt: null, Failed: false);

        await Assert.That(rec.InFailureBackoff(DateTimeOffset.UtcNow, TimeSpan.FromHours(1))).IsFalse();
    }

    [Test]
    public async Task FailedRecord_NeverFresh_EvenWithoutCheckedAt() {
        var rec = new UpdateCommand.UpdateCacheRecord("0.11.10", CheckedAt: null,
                                        AttemptedAt: DateTimeOffset.UtcNow, Failed: true);

        await Assert.That(rec.IsFresh(DateTimeOffset.UtcNow, TimeSpan.FromHours(24))).IsFalse();
    }

    [Test]
    public async Task SuccessRecord_PastTtl_NoLongerFresh() {
        var rec = new UpdateCommand.UpdateCacheRecord("0.11.10", CheckedAt: DateTimeOffset.UtcNow.AddHours(-25),
                                        AttemptedAt: null, Failed: false);

        await Assert.That(rec.IsFresh(DateTimeOffset.UtcNow, TimeSpan.FromHours(24))).IsFalse();
    }
}
