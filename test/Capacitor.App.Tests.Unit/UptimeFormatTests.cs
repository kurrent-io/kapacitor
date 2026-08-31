namespace Capacitor.App.Tests.Unit;

/// UptimeFormat's bucket boundaries — the shared formatter behind SessionCardViewModel.Age and
/// the rail rows' age sub-line.
public class UptimeFormatTests {
    [Test]
    [Arguments(0, "0s")]
    [Arguments(-5, "0s")] // negative clamps
    [Arguments(59, "59s")]
    [Arguments(60, "1m")]
    [Arguments(3599, "59m")] // 59m59s: minutes-only bucket drops seconds
    [Arguments(3600, "1h")] // exact hour: zero-remainder drops the minutes unit
    [Arguments(86340, "23h 59m")]
    [Arguments(86400, "1d")] // exact day: zero-remainder drops the hours unit
    [Arguments(90000, "1d 1h")]
    public async Task Format_boundary_table(int seconds, string expected) {
        await Assert.That(UptimeFormat.Format(TimeSpan.FromSeconds(seconds))).IsEqualTo(expected);
    }
}
