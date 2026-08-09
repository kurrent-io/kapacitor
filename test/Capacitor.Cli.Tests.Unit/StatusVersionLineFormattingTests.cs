using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pure formatting coverage for <see cref="StatusCommand.FormatVersionLine"/> — the I/O-free half
/// of the Version line printed by <c>kcap status</c>. The stateful half (reusing
/// <c>UpdateNotice</c>'s shared lazy check, calling <c>MarkReported()</c>, respecting the
/// update-check opt-outs) is covered end-to-end by the spawned-process tests in
/// <c>UpdateNoticeDeliveryTests</c>, since it needs a real profile config and the real
/// cross-process static state <c>UpdateNotice</c> relies on.
/// </summary>
public class StatusVersionLineFormattingTests {
    [Test]
    public async Task NullResult_PrintsBareVersion() {
        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", null)).IsEqualTo("kcap 0.11.12");
    }

    [Test]
    public async Task NotNewer_PrintsBareVersion_NoAnnotation() {
        var result = new UpdateCommand.UpdateCheckResult(Current: "0.11.12", Latest: "0.11.12", Newer: false, FromCache: true);

        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", result)).IsEqualTo("kcap 0.11.12");
    }

    [Test]
    public async Task Newer_AppendsInlineUpdateAvailableAnnotation() {
        var result = new UpdateCommand.UpdateCheckResult(Current: "0.11.12", Latest: "0.11.14", Newer: true, FromCache: true);

        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", result))
            .IsEqualTo("kcap 0.11.12 (update available: 0.11.14)");
    }

    // Newer:true with a null Latest is a shape the current UpdateCheckResult never actually
    // produces together, but the formatter defends against it anyway rather than trusting the
    // flag alone — same discipline as UpdateNotice.FlushAsync's own pattern match.
    [Test]
    public async Task Newer_ButLatestNull_PrintsBareVersion() {
        var result = new UpdateCommand.UpdateCheckResult(Current: "0.11.12", Latest: null, Newer: true, FromCache: true);

        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", result)).IsEqualTo("kcap 0.11.12");
    }
}
