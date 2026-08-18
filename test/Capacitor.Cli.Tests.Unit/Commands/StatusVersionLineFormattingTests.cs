using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

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
    public async Task NotAvailable_PrintsBareVersion() {
        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", default)).IsEqualTo("kcap 0.11.12");
    }

    [Test]
    public async Task NotNewer_PrintsBareVersion_NoAnnotation() {
        var advisory = new UpdateAdvisory(Current: "0.11.12", Target: "0.11.12", Newer: false, ServerCapped: false);

        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", advisory)).IsEqualTo("kcap 0.11.12");
    }

    [Test]
    public async Task Newer_AppendsInlineUpdateAvailableAnnotation() {
        var advisory = new UpdateAdvisory(Current: "0.11.12", Target: "0.11.14", Newer: true, ServerCapped: false);

        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", advisory))
            .IsEqualTo("kcap 0.11.12 (update available: 0.11.14)");
    }

    [Test]
    public async Task ServerCapped_AppendsServerVersionMarker() {
        // npm is ahead of the server; the annotation names the capped (server) target and marks it.
        var advisory = new UpdateAdvisory(Current: "0.11.12", Target: "0.11.15", Newer: true, ServerCapped: true);

        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", advisory))
            .IsEqualTo("kcap 0.11.12 (update available: 0.11.15, server version)");
    }

    // Newer:true with a null Target is a shape the resolver never actually produces together, but the
    // formatter defends against it anyway rather than trusting the flag alone — same discipline as
    // UpdateNotice.FlushAsync's own pattern match.
    [Test]
    public async Task Newer_ButTargetNull_PrintsBareVersion() {
        var advisory = new UpdateAdvisory(Current: "0.11.12", Target: null, Newer: true, ServerCapped: false);

        await Assert.That(StatusCommand.FormatVersionLine("0.11.12", advisory)).IsEqualTo("kcap 0.11.12");
    }
}
