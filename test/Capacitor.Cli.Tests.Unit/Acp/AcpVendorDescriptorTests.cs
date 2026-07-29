// test/Capacitor.Cli.Tests.Unit/Acp/AcpVendorDescriptorTests.cs
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Test plan item 7 (Round 2 Finding 2 drops the throwing-construction cases from an
/// earlier revision of this design): pins <see cref="AcpVendorDescriptors.Cursor"/>'s literal
/// field values against today's hard-coded constants — a lightweight guard against an accidental
/// edit to the shared descriptor silently changing Cursor's behavior. There is no
/// <c>SupportsModelSelection</c> flag/invariant left to test — <see cref="AcpVendorDescriptor"/>
/// accepts any <see cref="IAcpModelSelector"/> for <see cref="AcpVendorDescriptor.ModelSelector"/>
/// unconditionally, so the one thing worth asserting is that Cursor's real descriptor constructs
/// successfully and round-trips through equality as expected.
/// </summary>
public class AcpVendorDescriptorTests {
    [Test]
    public async Task Cursor_MatchesTodaysHardCodedConstants() {
        var descriptor = AcpVendorDescriptors.Cursor;

        await Assert.That(descriptor.Vendor).IsEqualTo("cursor");
        await Assert.That(descriptor.Argv.SequenceEqual(["acp"])).IsTrue();
        await Assert.That(descriptor.UnattendedTrustArgv.SequenceEqual([
            "--force", "--approve-mcps", "--trust"
        ])).IsTrue();
        await Assert.That(descriptor.SupportsUnattended).IsTrue();
        await Assert.That(descriptor.UnattendedInteractionPolicy).IsEqualTo(AcpUnattendedInteractionPolicy.Fail);
        await Assert.That(descriptor.SupportsMcpServers).IsTrue();
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.SessionNew);
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsTrue();
        await Assert.That(descriptor.BorrowedReviewContainment)
            .IsEqualTo(AcpBorrowedReviewContainment.IndependentSnapshot);
        await Assert.That(descriptor.ModelSelector).IsEqualTo(ConfigOptionModelSelector.Instance);
    }

    [Test]
    public async Task Copilot_MatchesTodaysHardCodedConstants() {
        var descriptor = AcpVendorDescriptors.Copilot;

        await Assert.That(descriptor.Vendor).IsEqualTo("copilot");
        await Assert.That(descriptor.Argv.SequenceEqual(["--acp", "--stdio"])).IsTrue();
        await Assert.That(descriptor.UnattendedTrustArgv.SequenceEqual([
            "--allow-all-tools", "--no-ask-user", "--no-custom-instructions", "--disable-builtin-mcps"
        ])).IsTrue();
        await Assert.That(descriptor.SupportsUnattended).IsTrue();
        await Assert.That(descriptor.UnattendedInteractionPolicy).IsEqualTo(AcpUnattendedInteractionPolicy.AutoApprove);
        // ACP still advertises only HTTP/SSE, so session/new stdio forwarding stays disabled.
        await Assert.That(descriptor.SupportsMcpServers).IsFalse();
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.CopilotAdditionalConfig);
        // Copilot's borrowed-review capability is NOT declared statically: it is resolved per
        // platform by CopilotBorrowedReviewPolicy, because the tool surface that makes a borrowed
        // snapshot readable AND contained has only been verified on some platforms. Pinning a static
        // declaration here would pin an answer that no launch consults.
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();
        await Assert.That(descriptor.BorrowedReviewContainment)
            .IsEqualTo(AcpBorrowedReviewContainment.None);
        await Assert.That(descriptor.ModelSelector).IsEqualTo(ConfigOptionModelSelector.Instance);
    }

    [Test]
    public async Task Copilot_ResolveBinaryPath_ReadsConfigCopilotPath() {
        var config = new DaemonConfig { CopilotPath = "/opt/copilot/copilot" };

        await Assert.That(AcpVendorDescriptors.Copilot.ResolveBinaryPath(config)).IsEqualTo("/opt/copilot/copilot");
    }

    [Test]
    public async Task Cursor_ResolveBinaryPath_ReadsConfigCursorPath() {
        var config = new DaemonConfig { CursorPath = "/opt/cursor/cursor-agent" };

        await Assert.That(AcpVendorDescriptors.Cursor.ResolveBinaryPath(config)).IsEqualTo("/opt/cursor/cursor-agent");
    }

    [Test]
    public async Task Cursor_ResolveDefaultModel_ReadsConfigCursorModel() {
        var config = new DaemonConfig { CursorModel = "claude-opus-4-8" };

        await Assert.That(AcpVendorDescriptors.Cursor.ResolveDefaultModel(config)).IsEqualTo("claude-opus-4-8");
    }

    /// <summary>Any <see cref="IAcpModelSelector"/> — including a NoOp one, even though the real
    /// Cursor descriptor never uses it — constructs a valid descriptor. There is no invariant left
    /// to reject this combination (Round 2 Finding 2).</summary>
    [Test]
    public async Task Descriptor_ConstructsSuccessfully_WithAnyModelSelector() {
        var descriptor = new AcpVendorDescriptor(
            Vendor:              "test-vendor",
            ResolveBinaryPath:   _ => "test-vendor-cli",
            ResolveDefaultModel: _ => null,
            Argv:                ["acp"],
            UnattendedTrustArgv: [],
            SupportsUnattended:  false,
            ModelSelector:       NoOpModelSelector.Instance,
            SupportsMcpServers:  false
        );

        await Assert.That(descriptor.ModelSelector).IsEqualTo(NoOpModelSelector.Instance);
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.Unsupported);
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();
    }

    /// <summary>Qodo finding 3: a vendor that doesn't support unattended launches must not carry
    /// any <see cref="AcpVendorDescriptor.UnattendedTrustArgv"/> — the constructor enforces this
    /// invariant rather than relying solely on the orchestrator's external gate.</summary>
    [Test]
    public async Task Constructor_Throws_WhenUnattendedTrustArgvNonEmpty_AndSupportsUnattendedFalse() {
        await Assert.That(() => new AcpVendorDescriptor(
            Vendor:              "test-vendor",
            ResolveBinaryPath:   _ => "test-vendor-cli",
            ResolveDefaultModel: _ => null,
            Argv:                ["acp"],
            UnattendedTrustArgv: ["--trust"],
            SupportsUnattended:  false,
            ModelSelector:       NoOpModelSelector.Instance,
            SupportsMcpServers:  false
        )).Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_Throws_WhenSessionNewTransportLacksMcpServerSupport() {
        await Assert.That(() => new AcpVendorDescriptor(
            Vendor:                 "test-vendor",
            ResolveBinaryPath:      _ => "test-vendor-cli",
            ResolveDefaultModel:    _ => null,
            Argv:                   ["acp"],
            UnattendedTrustArgv:    ["--trust"],
            SupportsUnattended:     true,
            ModelSelector:          NoOpModelSelector.Instance,
            SupportsMcpServers:     false,
            ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,
            UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AutoApprove
        )).Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_Throws_WhenBorrowedReviewSupportLacksUnattendedSupport() {
        await Assert.That(() => new AcpVendorDescriptor(
            Vendor:                      "test-vendor",
            ResolveBinaryPath:           _ => "test-vendor-cli",
            ResolveDefaultModel:         _ => null,
            Argv:                        [],
            UnattendedTrustArgv:         [],
            SupportsUnattended:          false,
            ModelSelector:               NoOpModelSelector.Instance,
            SupportsMcpServers:          false,
            SupportsBorrowedReviewFlow: true
        )).Throws<ArgumentException>();
    }
}
