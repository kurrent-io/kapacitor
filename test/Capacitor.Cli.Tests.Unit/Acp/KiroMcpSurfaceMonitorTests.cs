using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The tripwire detects a suppression failure. It is not the containment, so its residual (selective
/// notification loss) degrades detection rather than the boundary — but everything it DOES claim to
/// catch is pinned here, including the two shapes a membership-only check would miss.
/// </summary>
public class KiroMcpSurfaceMonitorTests {
    const string Channel = "kcap-flow-result-abc";

    static AcpNotification Note(string method, string serverName) =>
        new(method, JsonSerializer.Deserialize<JsonElement>(
            $$"""{"sessionId":"s1","serverName":"{{serverName}}"}"""));

    static KiroMcpSurfaceMonitor Monitor(params string[] injected) =>
        new(injected.ToHashSet(StringComparer.Ordinal), Channel);

    [Test]
    public async Task AnInjectedServer_IsAdmitted() {
        var m = Monitor(Channel);
        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, Channel));

        await Assert.That(m.Violation).IsNull();
        await Assert.That(m.ResultChannelReady).IsTrue();
    }

    [Test]
    public async Task AServerOutsideTheInjectedSet_IsAViolation() {
        var m = Monitor(Channel);
        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, "kcap-flows"));

        await Assert.That(m.Violation).IsNotNull();
        await Assert.That(m.Violation!).StartsWith("kiro_reviewer_mcp_surface_unexpected");
        await Assert.That(m.Violation!).Contains("kcap-flows");
    }

    /// <summary>
    /// The COUNT rule. A duplicate of an injected name is inside the injected set, so a
    /// membership-only check admits it — this is the case that distinguishes the two.
    /// </summary>
    [Test]
    public async Task ASecondInitializationOfAnInjectedName_IsAViolation() {
        var m = Monitor(Channel);
        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, Channel));
        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, Channel));

        await Assert.That(m.Violation).IsNotNull();
        await Assert.That(m.Violation!).StartsWith("kiro_reviewer_mcp_surface_unexpected");
    }

    /// <summary>
    /// Enforcement runs for the whole session, so an initialization arriving long after the result
    /// channel is up still trips. A sampling scheme would have closed its window by now.
    /// </summary>
    [Test]
    public async Task ALateInitialization_IsStillAViolation() {
        var m = Monitor(Channel);
        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, Channel));
        await Assert.That(m.Violation).IsNull();          // control: healthy up to here

        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, "kcap-memory"));
        await Assert.That(m.Violation).IsNotNull();
    }

    [Test]
    public async Task ResultChannelFailure_HasItsOwnCode() {
        var m = Monitor(Channel);
        m.Observe(Note(KiroMcpSurfaceMonitor.InitFailureMethod, Channel));

        await Assert.That(m.Violation!).StartsWith("kiro_reviewer_result_channel_unavailable");
    }

    /// <summary>An allowlist server failing to start is not fatal — only the result channel is.</summary>
    [Test]
    public async Task AnAllowlistServerFailure_IsNotFatal() {
        var m = Monitor(Channel, "kcap-review-xyz");
        m.Observe(Note(KiroMcpSurfaceMonitor.InitFailureMethod, "kcap-review-xyz"));

        await Assert.That(m.Violation).IsNull();
    }

    [Test]
    public async Task SilenceIsNotReadiness() =>
        await Assert.That(Monitor(Channel).ResultChannelReady).IsFalse();

    /// <summary>The first violation is the one reported; a later one may be its consequence.</summary>
    [Test]
    public async Task TheFirstViolationSticks() {
        var m = Monitor(Channel);
        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, "kcap-flows"));
        var first = m.Violation;

        m.Observe(Note(KiroMcpSurfaceMonitor.InitFailureMethod, Channel));

        await Assert.That(m.Violation).IsEqualTo(first);
    }

    [Test]
    public async Task UnrelatedNotificationsAreIgnored() {
        var m = Monitor(Channel);
        m.Observe(new AcpNotification("session/update", null));
        m.Observe(Note("_kiro.dev/metadata", "whatever"));

        await Assert.That(m.Violation).IsNull();
    }

    /// <summary>
    /// A documented gap, asserted as a gap rather than left to be discovered: a build that reports
    /// only the injected channel while quietly starting another server passes. Requiring the channel
    /// to appear catches TOTAL silence, never selective omission.
    /// </summary>
    [Test]
    public async Task KnownUncovered_SelectiveNotificationOmissionIsNotDetected() {
        var m = Monitor(Channel);
        m.Observe(Note(KiroMcpSurfaceMonitor.InitializedMethod, Channel));

        // A hostile/regressed build simply never emits the notification for the extra server.
        await Assert.That(m.Violation).IsNull();
        await Assert.That(m.ResultChannelReady).IsTrue();
    }

    [Test]
    public async Task For_ReturnsNullForANonKiroOrNonReviewLaunch() {
        var specs = new List<AcpMcpServerSpec> { new(Channel, "kcap", [], []) };
        var identity = LaunchIdentity.ForLaunch(aliasResultChannel: true);

        await Assert.That(KiroMcpSurfaceMonitor.For(
            AcpVendorDescriptors.Gemini, isReviewFlow: true, specs, identity)).IsNull();
        await Assert.That(KiroMcpSurfaceMonitor.For(
            AcpVendorDescriptors.Kiro, isReviewFlow: false, specs, identity)).IsNull();
        await Assert.That(KiroMcpSurfaceMonitor.For(
            AcpVendorDescriptors.Kiro, isReviewFlow: true, specs, identity)).IsNotNull();
    }
}
