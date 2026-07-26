// test/Capacitor.Cli.Tests.Unit/Daemon/DaemonRunnerCursorAvailabilityTests.cs
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// <c>DaemonRunner.RunAsync</c> silently omits an unavailable vendor from
/// <c>DaemonConfig.SupportedVendors</c> (correct — the launch dialog just won't offer it), but gave
/// operators no clue WHY when that vendor is Cursor. <see cref="DaemonRunner.ShouldWarnCursorUnavailable"/>
/// is the pure predicate extracted from that startup seam so it's testable without spinning up the
/// full DI host <c>RunAsync</c> builds — this only proves the predicate; the actual
/// <c>LogCursorUnavailable</c> Warning call at the <c>RunAsync</c> call site is not independently
/// unit-tested (would require a full host boot).
/// </summary>
public class DaemonRunnerCursorAvailabilityTests {
    /// <summary>Minimal <see cref="IHostedAgentRuntimeFactory"/> stand-in — only <see cref="Vendor"/>/<see cref="IsAvailable"/>/<see cref="SupportsUnattended"/>/<see cref="ReviewerModelResolver"/> matter here.</summary>
    sealed class FakeRuntimeFactory(
            string vendor, bool isAvailable, bool supportsUnattended = false,
            bool supportsBorrowedReviewFlow = false,
            IReviewerModelResolver? reviewerModelResolver = null) : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended { get; } = supportsUnattended;
        public bool   SupportsBorrowedReviewFlow { get; } = supportsBorrowedReviewFlow;
        public IReviewerModelResolver? ReviewerModelResolver { get; } = reviewerModelResolver;

        public bool IsAvailable() => isAvailable;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    /// <summary>Fake reviewer-model resolver — only <see cref="Vendor"/>/<see cref="PolicyVersion"/>
    /// matter for the capability-advertisement tests (advertisement is vendor-neutral: it reads
    /// whether a resolver exists + its policy version, never a model string).</summary>
    sealed class FakeReviewerModelResolver(string vendor, string policyVersion) : IReviewerModelResolver {
        public string Vendor        { get; } = vendor;
        public string PolicyVersion { get; } = policyVersion;

        public ReviewerModelResolution Resolve(string requestedModel) =>
            new(ReviewerModelDisposition.Unavailable);
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_CursorFactoryUnavailable_ReturnsTrue() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("cursor", isAvailable: false),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsTrue();
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_CursorFactoryAvailable_ReturnsFalse() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("cursor", isAvailable: true),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsFalse();
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_NoCursorFactoryRegistered_ReturnsFalse() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("codex", isAvailable: false),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsFalse();
    }

    // === Reviewer vendor override: UnattendedVendors computation ===

    [Test]
    public async Task ComputeUnattendedVendors_IncludesAvailableCopilot_WhenUnattendedEnabled() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("copilot", isAvailable: true, supportsUnattended: true),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories))
            .IsEquivalentTo(["claude", "copilot"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ComputeUnattendedVendors_ExcludesAvailableCursor_WithoutCertification() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsUnattended: false),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories)).IsEquivalentTo(["claude", "codex"]);
    }

    [Test]
    public async Task ComputeUnattendedVendors_ExcludesUnavailableVendorEvenIfItSupportsUnattended() {
        // Claude installed but unavailable (binary not found) must not be advertised, regardless
        // of what SupportsUnattended says — installation is still a prerequisite.
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: false, supportsUnattended: true),
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories)).IsEquivalentTo(["codex"]);
    }

    [Test]
    public async Task ComputeUnattendedVendors_OrdersAlphabetically() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories)).IsEquivalentTo(["claude", "codex"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ComputeUnattendedVendors_NoFactoriesReturnsEmptyArray() {
        await Assert.That(DaemonRunner.ComputeUnattendedVendors([])).IsEmpty();
    }

    [Test]
    public async Task ComputeUnattendedVendorCapabilities_AdvertisesClaudePolicyFacts() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { ClaudePath = "/definitely/missing/claude" });

        await Assert.That(capabilities).Count().IsEqualTo(2);
        var claude = capabilities.Single(c => c.Vendor == "claude");
        await Assert.That(claude.CliVersion).IsNull();
        await Assert.That(claude.LauncherPolicyVersion)
            .IsEqualTo(DaemonRunner.ClaudeLauncherPolicyVersion);
        await Assert.That(claude.BorrowedReviewSupported).IsFalse();
    }

    [Test]
    public async Task ComputeUnattendedVendorCapabilities_WithMissingCursorArtifact_FailsClosed() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsUnattended: true,
                supportsBorrowedReviewFlow: true),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { CursorPath = "/definitely/missing/cursor-agent" });

        await Assert.That(capabilities).Count().IsEqualTo(1);
        await Assert.That(capabilities[0].Vendor).IsEqualTo("cursor");
        await Assert.That(capabilities[0].CliVersion).IsNull();
        await Assert.That(capabilities[0].LauncherPolicyVersion)
            .IsEqualTo(DaemonRunner.CursorLauncherPolicyVersion);
        await Assert.That(capabilities[0].BorrowedReviewSupported).IsFalse();
        await Assert.That(capabilities[0].BorrowedReviewContainment).IsNull();
    }

    [Test]
    public async Task CliVersionAllowed_EvaluatesConfiguredRange() {
        await Assert.That(DaemonRunner.CliVersionAllowed("v1.2.3", ">=1.2.0 <2.0.0")).IsTrue();
        await Assert.That(DaemonRunner.CliVersionAllowed("2.0.0", ">=1.2.0 <2.0.0")).IsFalse();
        await Assert.That(DaemonRunner.CliVersionAllowed(null, ">=1.2.0")).IsFalse();
        await Assert.That(DaemonRunner.CliVersionAllowed("1.2.3", "")).IsFalse();
    }

    // === Reviewer model resolution: capability advertisement ===

    [Test]
    public async Task ComputeUnattendedVendorCapabilities_WithResolver_AdvertisesReviewerModelResolution() {
        // Installed + unattended-certified + HAS a resolver ⇒ the vendor advertises reviewer-model
        // resolution and its policy version.
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true,
                reviewerModelResolver: new FakeReviewerModelResolver("claude", "claude-reviewer-model-v1")),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { ClaudePath = "/definitely/missing/claude" });

        var claude = capabilities.Single(c => c.Vendor == "claude");
        await Assert.That(claude.SupportsReviewerModelResolution).IsTrue();
        await Assert.That(claude.ReviewerModelPolicyVersion).IsEqualTo("claude-reviewer-model-v1");
    }

    [Test]
    public async Task ComputeUnattendedVendorCapabilities_WithoutResolver_DoesNotAdvertiseReviewerModelResolution() {
        // Installed + unattended-certified but NO resolver ⇒ the vendor keeps its vendor-only
        // unattended support but must advertise false/null (never widened to "supported").
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { CodexPath = "/definitely/missing/codex" });

        var codex = capabilities.Single(c => c.Vendor == "codex");
        await Assert.That(codex.SupportsReviewerModelResolution).IsFalse();
        await Assert.That(codex.ReviewerModelPolicyVersion).IsNull();
    }
}
