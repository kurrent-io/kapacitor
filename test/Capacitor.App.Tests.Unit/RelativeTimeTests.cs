using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class RelativeTimeTests {
    static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(0, "just now")]
    [Arguments(59, "just now")]
    [Arguments(60, "1m ago")]
    [Arguments(59 * 60, "59m ago")]
    [Arguments(60 * 60, "1h ago")]
    [Arguments(23 * 60 * 60, "23h ago")]
    [Arguments(24 * 60 * 60, "1d ago")]
    [Arguments(6 * 24 * 60 * 60, "6d ago")]
    public async Task Within_a_week_the_text_is_relative(int secondsAgo, string expected) {
        await Assert.That(RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_week_or_more_shows_the_date() {
        await Assert.That(RelativeTime.Format(Now.AddDays(-7), Now)).IsEqualTo("Aug 29");
        await Assert.That(RelativeTime.Format(new DateTimeOffset(2025, 12, 24, 0, 0, 0, TimeSpan.Zero), Now)).IsEqualTo("Dec 24, 2025");
    }

    [Test]
    public async Task A_future_stamp_reads_as_just_now() {
        await Assert.That(RelativeTime.Format(Now.AddMinutes(5), Now)).IsEqualTo("just now");
    }
}
