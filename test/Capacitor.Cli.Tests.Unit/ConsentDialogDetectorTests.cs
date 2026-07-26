using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests <see cref="ConsentDialogDetector"/> — the PTY-stream watchdog that trips a fail-fast
/// when a spawned interactive CLI renders a one-time consent/trust dialog no unattended launch
/// can dismiss. Covers the real bypass-permissions banner (the wedge that motivated this),
/// chunk-split delivery, ANSI-interspersed frames, the workspace-trust dialog, and — critically —
/// that ordinary session output never trips it (no false positives).
/// </summary>
public class ConsentDialogDetectorTests {
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // The real Claude 2.1.x full-screen banner (text extracted from the shipped CLI binary).
    const string BypassBanner =
        "WARNING: Claude Code running in Bypass Permissions mode\n\n" +
        "In Bypass Permissions mode, Claude Code will not ask for your approval before running " +
        "potentially dangerous commands.\n\n" +
        "By proceeding, you accept all responsibility for actions taken while running in Bypass " +
        "Permissions mode.\n\n" +
        "1. No, exit\n" +
        "2. Yes, I accept\n";

    [Test]
    public async Task Bypass_permissions_banner_in_one_chunk_trips_with_the_coded_message() {
        var detector = new ConsentDialogDetector();

        var reason = detector.Observe(Utf8(BypassBanner));

        await Assert.That(reason).IsNotNull();
        await Assert.That(reason!).Contains("Bypass-Permissions");
    }

    [Test]
    public async Task Bypass_banner_split_across_chunks_still_trips_once_both_markers_arrive() {
        var detector = new ConsentDialogDetector();

        // Headline arrives first — not enough on its own (the accept option hasn't rendered yet).
        var partial = detector.Observe(Utf8("WARNING: Claude Code running in Bypass Permissions mode\n"));
        await Assert.That(partial).IsNull();

        // The accept option lands in a later frame — now it trips.
        var reason = detector.Observe(Utf8("\n1. No, exit\n2. Yes, I accept\n"));
        await Assert.That(reason).IsNotNull();
    }

    [Test]
    public async Task Ansi_interspersed_banner_trips() {
        var detector = new ConsentDialogDetector();

        // A realistic Ink frame: box-drawing borders + SGR colour codes + cursor moves around the text.
        var framed =
            "\x1b[2J\x1b[H\x1b[1m\x1b[33m╭──────────────────────────────╮\x1b[0m\n" +
            "\x1b[1m WARNING: Claude Code running in Bypass Permissions mode \x1b[0m\n" +
            "\x1b[2m 1. No, exit \x1b[0m\n" +
            "\x1b[36m 2. Yes, I accept \x1b[0m\n" +
            "\x1b[33m╰──────────────────────────────╯\x1b[0m\n";

        var reason = detector.Observe(Utf8(framed));

        await Assert.That(reason).IsNotNull();
    }

    [Test]
    public async Task Workspace_trust_dialog_trips_with_a_trust_specific_message() {
        var detector = new ConsentDialogDetector();

        var reason = detector.Observe(Utf8("Do you trust the files in this folder?\n1. Yes, proceed\n2. No, exit\n"));

        await Assert.That(reason).IsNotNull();
        await Assert.That(reason!).Contains("trust");
    }

    [Test]
    public async Task Detector_latches_and_returns_the_same_message_on_later_chunks() {
        var detector = new ConsentDialogDetector();

        var first = detector.Observe(Utf8(BypassBanner));
        await Assert.That(first).IsNotNull();

        // Subsequent output must keep reporting the same tripped reason (the launch is already doomed).
        var again = detector.Observe(Utf8("some later redraw frame"));
        await Assert.That(again).IsEqualTo(first);
        await Assert.That(detector.Tripped).IsTrue();
    }

    [Test]
    public async Task Ordinary_session_output_never_trips() {
        var detector = new ConsentDialogDetector();

        // Real reviewer chatter that name-drops the feature but is NOT the consent dialog.
        string?[] results = [
            detector.Observe(Utf8("Reading files and running the review...\n")),
            detector.Observe(Utf8("The change enables bypass permissions mode for the daemon.\n")),
            detector.Observe(Utf8("I accept that this is a reasonable approach. Yes.\n")),
            detector.Observe(Utf8("Do you trust the tests? They pass locally.\n")),
        ];

        foreach (var r in results) await Assert.That(r).IsNull();
        await Assert.That(detector.Tripped).IsFalse();
    }

    [Test]
    public async Task Prose_quoting_the_banner_phrases_without_the_menu_layout_does_not_trip() {
        var detector = new ConsentDialogDetector();

        // A reviewer reading source/docs about the feature (e.g. this detector's own file, which
        // literally contains both phrases): the headline "bypass permissions mode" AND the accept
        // wording "Yes, I accept" both appear, but NOT the dialog's numbered "1. No, exit" /
        // "2. Yes, I accept" selection menu. Only the real full-screen dialog renders that numbered
        // layout, so the co-occurrence of loose phrases in prose must NOT trip a false wedge.
        var reason = detector.Observe(Utf8(
            "This watchdog trips when Claude renders its bypass permissions mode dialog, where an "
          + "unattended reviewer would otherwise have to choose \"Yes, I accept\" with no human "
          + "present to answer it.\n"));

        await Assert.That(reason).IsNull();
        await Assert.That(detector.Tripped).IsFalse();
    }

    [Test]
    public async Task Marker_split_mid_word_across_two_chunks_still_trips() {
        var detector = new ConsentDialogDetector();

        // The headline is delivered byte-torn ("Bypass Permis" | "sions mode"), exactly like a PTY
        // read that ends mid-word. The rolling window stitches the frames back together.
        await Assert.That(detector.Observe(Utf8("WARNING: Claude Code running in Bypass Permis"))).IsNull();
        var reason = detector.Observe(Utf8("sions mode\n1. No, exit\n2. Yes, I accept\n"));

        await Assert.That(reason).IsNotNull();
    }
}
