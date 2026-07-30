using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// Pins the DECIDED behaviour for durable state that can outlive a Cursor session: a
/// well-formed marker without an ack fails CLOSED (and silently), while a malformed marker
/// fails OPEN to top-level. The asymmetry is deliberate and is only defensible because the
/// offline recovery path (<c>kcap import --cursor</c> plus the server-side adoption sweep)
/// exists.
///
/// <para>
/// None of these states has a producer on the measured cursor-agent contract — the arm that
/// would write a marker never runs there — but consumption is NOT gated: TryLoadLink runs on
/// every event, so a marker persisted by another surface or an older build is still read.
/// See docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md (D2a).
/// </para>
/// </summary>
public class CursorSubagentStaleStateTests {
    static string MarkerPath(string child) =>
        Path.Combine(PathHelpers.ConfigPath("cursor-subagent-links"), child);

    [Test]
    public async Task A_well_formed_marker_is_loaded_and_would_activate_the_divert() {
        var child = NewChildId();
        try {
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "researcher");

            var marker = CursorLiveSubagentLinker.TryLoadLink(child);
            await Assert.That(marker).IsNotNull();
            await Assert.That(marker!.Value.ParentSessionId).IsEqualTo("parent-sid");
            await Assert.That(marker.Value.SubagentType).IsEqualTo("researcher");
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task A_truncated_marker_fails_open_to_top_level() {
        var child = NewChildId();
        try {
            Directory.CreateDirectory(PathHelpers.ConfigPath("cursor-subagent-links"));
            // One line only: TryLoadLink requires >= 2 with a non-empty first.
            File.WriteAllText(MarkerPath(child), "only-one-line\n");

            // Fails OPEN — no link, so the session is treated as top-level. That is the safe
            // direction: the session still gets captured, just not nested.
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task A_marker_with_an_empty_parent_id_also_fails_open() {
        var child = NewChildId();
        try {
            Directory.CreateDirectory(PathHelpers.ConfigPath("cursor-subagent-links"));
            File.WriteAllText(MarkerPath(child), "\ntask\n");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task A_marker_without_an_ack_leaves_the_child_gated_and_silent() {
        var child = NewChildId();
        try {
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "task");

            // The marker activates the divert, but no ack was ever recorded — so every non-start
            // hook for this child returns early at the HasSubagentStartAck gate: its raw event
            // AND its transcript backfill are suppressed indefinitely.
            //
            // This is FAIL-CLOSED AND SILENT by decision (it preserves start-before-content
            // ordering). Note the contrast with the truncated-marker case above, which fails
            // OPEN. Recovery for this one is `kcap import --cursor` plus the adoption sweep;
            // nothing is logged or surfaced at the time of the loss.
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNotNull();
            await Assert.That(CursorMarkers.HasSubagentStartAck(child)).IsFalse();
        } finally { TryDeleteMarker(child); }
    }

    // ---------------------------------------------------------------------------------
    // KNOWN-BUG CHARACTERIZATION TESTS — NOT a contract.
    //
    // A SaveLink write failure is swallowed, and the caller has ALREADY assigned
    // subagentParentId before calling it, so the divert still runs. Whichever way the start
    // then completes, side effects land with no marker on disk:
    //   - start POST succeeds  -> ack marked + {parent}-{child} watcher spawned + backfill
    //                             under the parent, while later hooks miss TryLoadLink and go
    //                             top-level;
    //   - start POST fails     -> spooled entry whose later drain does the same.
    // Either way the same child transcript can be routed BOTH under the parent and as its own
    // top-level session.
    //
    // The design spec (D2a) labels this state UNSUPPORTED and lists remedies; the leading one
    // is to have SaveLink report success and fail open BEFORE the start is posted. These tests
    // are therefore EXCLUDED from the mutation rule that governs every other pin in this file.
    // When a remedy lands they are EXPECTED to fail — rewrite or delete them then. Do not
    // "fix" them to keep passing.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task SaveLink_write_failure_currently_leaves_no_marker_known_risk() {
        var child = NewChildId();
        var blocker = MarkerPath(child);
        try {
            // A DIRECTORY where the marker FILE must go: File.WriteAllLines throws, SaveLink
            // swallows it. Chosen over redirecting KCAP_CONFIG_DIR because PathHelpers resolves
            // the config dir into a process-wide static readonly field.
            Directory.CreateDirectory(blocker);

            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "task");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally {
            try { Directory.Delete(blocker, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task SaveLink_failure_does_not_signal_the_caller_known_risk() {
        var child = NewChildId();
        var blocker = MarkerPath(child);
        try {
            Directory.CreateDirectory(blocker);

            // SaveLink returns void and throws nothing, so the caller cannot tell the marker was
            // lost — which is exactly why the start's side effects still run. Remedy 1 in the
            // spec is to make this observable and fail open instead. That this call simply
            // returns IS the finding.
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "task");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
            await Assert.That(CursorMarkers.HasSubagentStartAck(child)).IsFalse();
        } finally {
            try { Directory.Delete(blocker, true); } catch { /* best effort */ }
        }
    }

    static string NewChildId() => Guid.NewGuid().ToString("N");

    static void TryDeleteMarker(string child) {
        try { File.Delete(MarkerPath(child)); } catch { /* best effort */ }
    }
}
