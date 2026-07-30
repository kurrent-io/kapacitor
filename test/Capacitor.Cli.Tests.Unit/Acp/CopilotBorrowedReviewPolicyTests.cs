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
        var p = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        await Assert.That(p.Supported).IsTrue();
        await Assert.That(p.Containment).IsEqualTo(AcpBorrowedReviewContainment.IndependentSnapshot);
        await Assert.That(p.ExtraBorrowedToolIds).IsEquivalentTo(CopilotBorrowedReviewPolicy.ReadToolIds);
        // The readable allowlist and the OS read boundary ship as one thing.
        await Assert.That(p.RequiresProcessSandbox).IsTrue();
    }

    /// <summary>The host that CAN run Copilot but CANNOT enforce the boundary is unsupported, not
    /// "supported without the sandbox".
    ///
    /// <para>This is the load-bearing half of the pairing. The read tools are what make an outside
    /// path reachable in the first place, so an entry granting them without the OS boundary would
    /// leave confidentiality resting on the vendor continuing to ask permission — which is exactly
    /// the dependency the sandbox exists to remove.</para></summary>
    [Test]
    public async Task The_verified_platform_without_a_sandbox_is_unsupported() {
        var p = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: false, authBrokerAvailable: () => true);

        await Assert.That(p.Supported).IsFalse();
        await Assert.That(p.Containment).IsEqualTo(AcpBorrowedReviewContainment.None);
        await Assert.That(p.ExtraBorrowedToolIds).IsEmpty();
        await Assert.That(p.RequiresProcessSandbox).IsFalse();
    }

    /// <summary>The other half of the same pairing, and the reason support is gated on the broker at
    /// ADVERTISEMENT rather than checked at spawn.
    ///
    /// <para>The sandbox no longer grants the keychain, so a daemon with no brokerable credential
    /// cannot authenticate a contained reviewer at all. Advertising borrowed review anyway would trade
    /// an honest, coded start rejection (plus the <c>context-only</c> remedy) for a flow that dies
    /// mid-launch — the same security posture with strictly worse behaviour.</para></summary>
    [Test]
    public async Task The_verified_platform_without_a_brokerable_credential_is_unsupported() {
        var p = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => false);

        await Assert.That(p.Supported).IsFalse();
        await Assert.That(p.Containment).IsEqualTo(AcpBorrowedReviewContainment.None);
        await Assert.That(p.ExtraBorrowedToolIds).IsEmpty();
        await Assert.That(p.RequiresProcessSandbox).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(Unverified))]
    public async Task An_unverified_platform_fails_closed((OSPlatform Os, Architecture Arch) key) {
        var p = CopilotBorrowedReviewPolicy.Resolve(
            key.Os, key.Arch, sandboxAvailable: true, authBrokerAvailable: () => true);

        await Assert.That(p.Supported).IsFalse()
            .Because($"{key.Os}/{key.Arch} has no verified tool surface");
        await Assert.That(p.Containment).IsEqualTo(AcpBorrowedReviewContainment.None);
        // The load-bearing half: an unverified platform must not be able to reach the readable argv.
        await Assert.That(p.ExtraBorrowedToolIds).IsEmpty();
        await Assert.That(p.RequiresProcessSandbox).IsFalse();
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

    /// <summary>What ships now: <c>Current</c> CONSULTS the table rather than being pinned unsupported.
    ///
    /// <para>This replaces the <c>No_platform_advertises_borrowed_review_yet</c> tripwire, which
    /// existed because the profile still had to grant the vendor's state, the keychain,
    /// <c>/Library</c> and the whole runtime prefix in order to start and authenticate. All four are
    /// closed, so the tripwire is deleted rather than weakened — and the assertion that replaces it is
    /// the one that still matters: whatever <c>Current</c> resolves to, it must be the SAME entry the
    /// argv builder uses, and it must be one the table can actually produce.</para>
    ///
    /// <para>Deliberately not asserted as <c>Supported = true</c>: that would fail on any CI host
    /// without <c>sandbox-exec</c> or without a brokerable credential, which are exactly the hosts the
    /// fail-closed arms exist for.</para></summary>
    [Test]
    public async Task The_host_entry_is_resolved_from_the_table_not_pinned() {
        var current  = CopilotBorrowedReviewPolicy.Current;
        var expected = CopilotBorrowedReviewPolicy.Resolve(
            CurrentOsForTest(), RuntimeInformation.ProcessArchitecture,
            BorrowedReviewSandbox.Available, () => BorrowedReviewAuthBroker.Available);

        await Assert.That(current.Supported).IsEqualTo(expected.Supported);
        await Assert.That(current.Containment).IsEqualTo(expected.Containment);
        await Assert.That(current.ExtraBorrowedToolIds).IsEquivalentTo(expected.ExtraBorrowedToolIds);
        await Assert.That(current.RequiresProcessSandbox).IsEqualTo(expected.RequiresProcessSandbox);
    }

    /// <summary>A supported host entry can only ever be the sandboxed, readable one. This is the
    /// invariant the deleted tripwire was standing in for: not "borrowed review is off", but "borrowed
    /// review is never on without its boundary".</summary>
    [Test]
    public async Task A_supported_host_entry_always_carries_the_sandbox_and_the_read_tools() {
        var current = CopilotBorrowedReviewPolicy.Current;

        if (!current.Supported) {
            await Assert.That(current.Containment).IsEqualTo(AcpBorrowedReviewContainment.None);
            await Assert.That(current.ExtraBorrowedToolIds).IsEmpty();
            await Assert.That(current.RequiresProcessSandbox).IsFalse();

            return;
        }

        await Assert.That(current.RequiresProcessSandbox).IsTrue();
        await Assert.That(current.Containment).IsEqualTo(AcpBorrowedReviewContainment.IndependentSnapshot);
        await Assert.That(current.ExtraBorrowedToolIds).IsEquivalentTo(CopilotBorrowedReviewPolicy.ReadToolIds);
        await Assert.That(BorrowedReviewSandbox.Available).IsTrue();
        await Assert.That(BorrowedReviewAuthBroker.Available).IsTrue();
    }

    static OSPlatform CurrentOsForTest() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)       ? OSPlatform.OSX
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? OSPlatform.Linux
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
        : OSPlatform.Create("UNKNOWN");

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

    /// <summary>Resolving credential availability can RUN an operator-configured command, so it must not
    /// be asked on a host where borrowed review is impossible and the answer unused. Asserted by call
    /// count on each failing precondition: passing a bool instead of a thunk would evaluate it eagerly and
    /// spend up to the command timeout at every daemon's startup, on every platform.</summary>
    [Test]
    [Arguments("linux",   false)]
    [Arguments("windows", false)]
    [Arguments("osx-x64", false)]
    [Arguments("no-sandbox", false)]
    [Arguments("supported", true)]
    public async Task The_credential_probe_runs_only_when_every_other_precondition_holds(
            string scenario, bool shouldProbe) {
        var probes = 0;

        var (os, arch, sandbox) = scenario switch {
            "linux"      => (OSPlatform.Linux,   Architecture.Arm64, true),
            "windows"    => (OSPlatform.Windows, Architecture.Arm64, true),
            "osx-x64"    => (OSPlatform.OSX,     Architecture.X64,   true),
            "no-sandbox" => (OSPlatform.OSX,     Architecture.Arm64, false),
            _            => (OSPlatform.OSX,     Architecture.Arm64, true)
        };

        CopilotBorrowedReviewPolicy.Resolve(os, arch, sandbox, () => { probes++; return true; });

        await Assert.That(probes).IsEqualTo(shouldProbe ? 1 : 0);
    }
}
