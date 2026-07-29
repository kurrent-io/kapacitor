using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The platform gate on Copilot's borrowed-review tool surface, and the property that makes it
/// meaningful: advertisement and spawn resolve from the SAME entry.
///
/// <para><b>Why a platform gate at all.</b> The tool surface was verified live on one platform. Tool
/// identifiers and path-trust semantics are the security boundary here, and nothing establishes they
/// are identical elsewhere. An unverified platform therefore gets no entry and advertises no borrowed
/// review — honest, and fail-closed.</para>
///
/// <para>This is NOT the version-keyed certification that was removed. That one keyed on a vendor
/// build, which auto-updates underneath a user; it drifted by construction and died within days. OS
/// and architecture do not change under a running daemon.</para>
/// </summary>
public class CopilotBorrowedReviewPolicyTests {
    public static IEnumerable<Func<(OSPlatform Os, Architecture Arch)>> Unverified() {
        yield return () => (OSPlatform.OSX,     Architecture.X64);
        yield return () => (OSPlatform.Linux,   Architecture.Arm64);
        yield return () => (OSPlatform.Linux,   Architecture.X64);
        yield return () => (OSPlatform.Windows, Architecture.X64);
        yield return () => (OSPlatform.Windows, Architecture.Arm64);
        yield return () => (OSPlatform.Create("UNKNOWN"), Architecture.X64);
    }

    [Test]
    public async Task The_verified_platform_supports_borrowed_review_with_the_read_tools() {
        var p = CopilotBorrowedReviewPolicy.Resolve(OSPlatform.OSX, Architecture.Arm64);

        await Assert.That(p.Supported).IsTrue();
        await Assert.That(p.Containment).IsEqualTo(AcpBorrowedReviewContainment.IndependentSnapshot);
        await Assert.That(p.ExtraBorrowedToolIds).IsEquivalentTo(CopilotBorrowedReviewPolicy.ReadToolIds);
    }

    [Test]
    [MethodDataSource(nameof(Unverified))]
    public async Task An_unverified_platform_fails_closed((OSPlatform Os, Architecture Arch) key) {
        var p = CopilotBorrowedReviewPolicy.Resolve(key.Os, key.Arch);

        await Assert.That(p.Supported).IsFalse()
            .Because($"{key.Os}/{key.Arch} has no verified tool surface");
        await Assert.That(p.Containment).IsEqualTo(AcpBorrowedReviewContainment.None);
        // The load-bearing half: an unverified platform must not be able to reach the readable argv.
        await Assert.That(p.ExtraBorrowedToolIds).IsEmpty();
    }

    // The allowlist must never carry the write/exec surface — that is what makes containment
    // structural rather than dependent on Copilot's deny-list naming, which live probing found does
    // NOT cover its file-create tool.
    [Test]
    [Arguments("create")]
    [Arguments("edit")]
    [Arguments("bash")]
    [Arguments("write")]
    [Arguments("shell")]
    public async Task The_read_tool_set_never_includes_a_write_or_exec_tool(string forbidden) {
        await Assert.That(CopilotBorrowedReviewPolicy.ReadToolIds).DoesNotContain(forbidden);
    }

    // ── advertisement and spawn agree ─────────────────────────────────────────────────────────
    //
    // Splitting these is how a daemon ends up advertising "no borrowed review" while the descriptor
    // still permits the readable argv on a stale or forged borrowed command. Both must derive from
    // one resolved entry.

    [Test]
    public async Task Advertisement_and_spawn_resolve_from_the_same_entry() {
        // What the daemon would advertise for this host...
        var advertised = AcpHostedAgentRuntimeFactory.PolicyFor(AcpVendorDescriptors.Copilot);
        // ...must be the very entry the argv builder uses.
        var host       = CopilotBorrowedReviewPolicy.Current;

        await Assert.That(advertised.Supported).IsEqualTo(host.Supported);
        await Assert.That(advertised.Containment).IsEqualTo(host.Containment);
        await Assert.That(advertised.ExtraBorrowedToolIds).IsEquivalentTo(host.ExtraBorrowedToolIds);
    }

    // A vendor with no platform policy is unaffected: it still reads its own descriptor, so this
    // change cannot silently narrow Cursor or any future borrowed-capable vendor.
    [Test]
    public async Task A_vendor_without_a_platform_policy_keeps_its_descriptor_capability() {
        var p = AcpHostedAgentRuntimeFactory.PolicyFor(AcpVendorDescriptors.Cursor);

        await Assert.That(p.Supported).IsEqualTo(AcpVendorDescriptors.Cursor.SupportsBorrowedReviewFlow);
        await Assert.That(p.Containment).IsEqualTo(AcpVendorDescriptors.Cursor.BorrowedReviewContainment);
        await Assert.That(p.ExtraBorrowedToolIds).IsEmpty();
    }

    // An unsupported entry must refuse the launch outright, not merely omit the read tools — a
    // reviewer launched blind is the defect this issue exists to end.
    [Test]
    public async Task An_unsupported_entry_refuses_a_borrowed_launch() {
        var ctx = new RuntimeStartContext(
            AgentId: "agent-1", Vendor: "copilot", SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "b", SourceRepo: "/repo"), Prompt: "",
            Model: "default", Effort: null, Tools: null,
            IsReview: false, IsReviewFlow: true, Review: null,
            Cols: 80, Rows: 24, ServerUrl: "http://kcap.test", DaemonBridgeUrl: null,
            CapacitorPath: "/usr/local/bin/kcap") with {
                Work = WorkLocation.BorrowedCwd, McpAllowlist = ["kcap-review"]
            };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Copilot, new(), ctx, ResolvedBorrowedReviewPolicy.Unsupported));

        await Assert.That(ex.Message).Contains("owned worktree");
    }
}
