using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Locks the auth-lapse notice wording. Every string is user-facing and asserted on by the
/// Claude-hook and poster tests, so a reword must be a deliberate edit here rather than a
/// silent drift between the pre-flight nudge and the server-rejection nudge.
/// </summary>
public class AuthLapseNoticeTests {
    [Test]
    public async Task every_notice_names_the_recovery_command() {
        await Assert.That(AuthLapseNotice.Expired).Contains("kcap login");
        await Assert.That(AuthLapseNotice.NotAuthenticated).Contains("kcap login");
        await Assert.That(AuthLapseNotice.Rejected).Contains("kcap login");
    }

    [Test]
    public async Task rejected_names_the_status_and_the_pause() {
        await Assert.That(AuthLapseNotice.Rejected).IsEqualTo(
            "[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap login' to resume.");
    }

    [Test]
    public async Task moved_strings_keep_their_existing_wording() {
        await Assert.That(AuthLapseNotice.Expired).IsEqualTo(
            "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume.");
        await Assert.That(AuthLapseNotice.NotAuthenticated).IsEqualTo(
            "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording.");
    }

    [Test]
    public async Task vendor_stderr_line_carries_the_tag_route_status_and_command_on_401() {
        var line = AuthLapseNotice.VendorStderrLine("codex-hook", "stop", 401);

        await Assert.That(line).IsEqualTo(
            "[kcap] codex-hook stop: HTTP 401 — the server rejected your credentials; run 'kcap login' to resume recording");
    }

    [Test]
    public async Task vendor_stderr_line_stays_the_bare_status_line_for_a_non_401_code() {
        var line = AuthLapseNotice.VendorStderrLine("codex-hook", "stop", 500);

        await Assert.That(line).IsEqualTo("[kcap] codex-hook stop: HTTP 500");
    }
}
