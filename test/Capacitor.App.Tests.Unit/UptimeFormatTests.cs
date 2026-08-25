namespace Capacitor.App.Tests.Unit;

/// UptimeFormat (spec §8 boundary table) — retargeted from the deleted AgentGridTests.cs (AI-2199:
/// the Agents grid is gone, but SessionCardViewModel.Age and RailSessionViewModel still format
/// uptime through this same shared utility).
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
