using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// One vendor's borrowed-review capability, resolved for the platform this daemon is running on.
///
/// <para>Every consumer of borrowed-review capability reads THIS, never the static descriptor:
/// the advertised <c>SupportsBorrowedReviewFlow</c>, the containment token, the
/// snapshot-materialization requirement, the pre-spawn validation, and the launch argv. Splitting
/// them is how advertisement and spawn drift apart — a daemon that advertises "no borrowed review"
/// while the descriptor still says yes leaves the readable argv reachable on a stale borrowed
/// command, which is the shape of the bug this whole area exists to close.</para>
/// </summary>
/// <param name="ExtraBorrowedToolIds">Tool ids added to the exclusive <c>--available-tools</c>
/// allowlist for a BORROWED-SNAPSHOT review launch only. Empty for every other launch, which keeps
/// the flow-result-only clamp — see <see cref="CopilotBorrowedReviewPolicy"/>.</param>
/// <param name="RequiresProcessSandbox">Whether a borrowed-snapshot launch under this entry must be
/// wrapped in an OS filesystem sandbox. Set wherever the readable allowlist is granted: widening the
/// tool surface without an independent read boundary would leave confidentiality resting on the
/// vendor continuing to ask permission for out-of-bounds paths. See
/// <see cref="BorrowedReviewSandbox"/>.</param>
internal sealed record ResolvedBorrowedReviewPolicy(
    bool                         Supported,
    AcpBorrowedReviewContainment Containment,
    IReadOnlyList<string>        ExtraBorrowedToolIds,
    bool                         RequiresProcessSandbox = false
) {
    /// <summary>A vendor with no platform-specific policy: whatever its descriptor declares, and no
    /// extra tool ids. This is every vendor except Copilot.</summary>
    public static ResolvedBorrowedReviewPolicy FromDescriptor(AcpVendorDescriptor descriptor) =>
        new(descriptor.SupportsBorrowedReviewFlow, descriptor.BorrowedReviewContainment, []);

    /// <summary>Not supported anywhere: no borrowed launch, no containment token, no extra tools.</summary>
    public static readonly ResolvedBorrowedReviewPolicy Unsupported =
        new(false, AcpBorrowedReviewContainment.None, []);
}

/// <summary>
/// Which platforms Copilot's borrowed-review tool surface has actually been verified on, and the
/// read tools that surface consists of.
///
/// <para><b>Why an exclusive allowlist rather than a deny-list.</b> Live probing of Copilot 1.0.75
/// found that <c>--deny-tool=write</c> does NOT cover its file-create/edit tool. A direct write to an
/// absolute path outside the snapshot was not denied — it raised an inbound
/// <c>session/request_permission</c> ("Access paths outside trusted directories"), and this daemon's
/// unattended interaction policy auto-approves exactly that shape. A deny-list therefore leaves the
/// write path open while looking closed.</para>
///
/// <para>Keeping <c>--available-tools</c> EXCLUSIVE and widening it to the read tools makes WRITE
/// containment structural instead: no write or exec tool is representable at all, so there is no
/// permission request for anything to grant. READ containment is a separate problem and is NOT
/// solved by the allowlist — see <see cref="BorrowedReviewSandbox"/>, which is why a supported entry
/// requires an OS sandbox and a host that cannot provide one is unsupported. Verified live — with this allowlist Copilot reports
/// <c>bash</c>, <c>create</c> and <c>edit</c> among its disabled tools, raises no permission request,
/// and states plainly that it has no tool capable of writing.</para>
///
/// <para><b>Why the platform key.</b> The verification above was performed on one platform. Tool
/// identifiers and path-trust semantics are the security boundary here, and nothing establishes they
/// are identical elsewhere, so an unverified platform gets no entry and advertises no borrowed
/// review. This is NOT the version-keyed certification that was removed — a vendor build
/// auto-updates underneath a user and that gate died within days; an OS and architecture do not
/// change under a running daemon, and this encodes a fact nobody has measured rather than
/// re-deriving one that was deliberately trusted.</para>
/// </summary>
internal static class CopilotBorrowedReviewPolicy {
    /// <summary>The read/search tools a borrowed Copilot reviewer may use, on top of the flow-result
    /// channel. Deliberately excludes <c>bash</c>, <c>create</c> and <c>edit</c> — the exclusive
    /// allowlist is what makes those unrepresentable.
    ///
    /// <para><see cref="ImmutableArray{T}"/> rather than <c>string[]</c> because this IS the tool
    /// allowlist: a shared mutable array reaches the argv builder by reference, so any code holding
    /// it could write <c>ReadToolIds[0] = "bash"</c> and silently hand every subsequent borrowed
    /// reviewer a shell. A security boundary should not be a writable static.</para></summary>
    internal static readonly ImmutableArray<string> ReadToolIds = ["glob", "grep", "view"];

    /// <summary>Platform entries whose tool surface has been verified. Absent, unknown, or
    /// unverified ⇒ unsupported and fail closed. Pure and keyed only on OS + architecture: no
    /// probing, no vendor call, no version input — a compiled record of what was measured.</summary>
    /// <param name="sandboxAvailable">Whether this host can actually enforce the read boundary. A
    /// host that cannot is UNSUPPORTED, not "supported without the sandbox" — the readable allowlist
    /// and the OS boundary ship as one thing, and an entry that granted the first without the second
    /// would be the exact confidentiality gap this design exists to close.</param>
    internal static ResolvedBorrowedReviewPolicy Resolve(
            OSPlatform os, Architecture arch, bool sandboxAvailable) =>
        os == OSPlatform.OSX && arch == Architecture.Arm64 && sandboxAvailable
            ? new(true, AcpBorrowedReviewContainment.IndependentSnapshot, ReadToolIds,
                  RequiresProcessSandbox: true)
            : ResolvedBorrowedReviewPolicy.Unsupported;

    /// <summary>
    /// This machine's entry — currently <b>unsupported on every platform, deliberately</b>.
    ///
    /// <para><see cref="Resolve"/> above is the real, tested table and it stays exactly as it is;
    /// what ships disabled is the decision to consult it. The reason is a specific unclosed gap
    /// rather than doubt about the mechanism: the sandbox profile must still grant recursive reads of
    /// <c>~/.copilot</c>, <c>~/Library/Keychains</c>, <c>/Library</c> and <c>/opt/homebrew</c> so the
    /// vendor can start and authenticate, and those are data-bearing. A vendor build that silently
    /// accepted an out-of-bounds path could point the allowlisted read tools at them and exfiltrate
    /// through the result channel with <b>no</b> interaction frame — so the interaction <c>Fail</c>
    /// policy never fires and the sandbox permits the read. The boundary is vendor-independent for
    /// arbitrary paths; it is not yet vendor-independent for those four.</para>
    ///
    /// <para>Closing it means a per-launch HOME/state directory, authentication brokered through
    /// <c>COPILOT_GITHUB_TOKEN</c>/<c>GH_TOKEN</c> instead of the keychain grant, and runtime grants
    /// narrowed to executables and packages rather than whole config trees. That makes the daemon a
    /// credential-handling component, which is a decision in its own right and not one to take as a
    /// side effect of a tool-allowlist fix.</para>
    ///
    /// <para>Everything else in this change is live and load-bearing: snapshot routing, the platform
    /// table and its fail-closed wiring, the <c>Fail</c>-on-any-interaction override for borrowed
    /// launches, the sandbox and its enforcement test, and the containment token contract. Flipping
    /// this one line back to <c>Resolve(CurrentOs(), …)</c> is all that enabling costs once the
    /// grants above are closed — and until then the server answers a Copilot borrowed request with
    /// <c>vendor_containment_unreadable</c> plus the <c>context-only</c> remedy, which is honest.</para>
    /// </summary>
    internal static ResolvedBorrowedReviewPolicy Current { get; } =
        ResolvedBorrowedReviewPolicy.Unsupported;

    static OSPlatform CurrentOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? OSPlatform.OSX
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OSPlatform.Linux
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
        : OSPlatform.Create("UNKNOWN");
}
