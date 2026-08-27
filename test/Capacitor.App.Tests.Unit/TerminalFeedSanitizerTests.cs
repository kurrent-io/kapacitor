using Capacitor.App.Services;
using static Capacitor.App.Tests.Unit.Ansi;

namespace Capacitor.App.Tests.Unit;

public class TerminalFeedSanitizerTests {
    static string Csi(string parameters) => Esc + "[" + parameters + "m";

    /// Pins the underline-colour rule: 58 with its arguments, and 59, leave the feed entirely,
    /// in every form they are written.
    [Test]
    public async Task Underline_colour_selectors_are_dropped_with_their_arguments() {
        var s = new TerminalFeedSanitizer();
        await Assert.That(s.Sanitize(Csi("58;2;255;4;4") + "a")).IsEqualTo("a");
        await Assert.That(s.Sanitize(Csi("1;58;5;4;31"))).IsEqualTo(Csi("1;31"));
        await Assert.That(s.Sanitize(Csi("59"))).IsEqualTo("");
        await Assert.That(s.Sanitize(Csi("4;58:2::1:2:3"))).IsEqualTo(Csi("4"));
    }

    /// Pins the sub-parameter rule: a colon form becomes the semicolon form the emulator reads,
    /// and an underline style of 0 is the underline-off code.
    [Test]
    public async Task Colon_sub_parameters_are_normalised_to_semicolon_forms() {
        var s = new TerminalFeedSanitizer();
        await Assert.That(s.Sanitize(Csi("4:0"))).IsEqualTo(Csi("24"));
        await Assert.That(s.Sanitize(Csi("4:3"))).IsEqualTo(Csi("4"));
        await Assert.That(s.Sanitize(Csi("38:2::10:20:30"))).IsEqualTo(Csi("38;2;10;20;30"));
        await Assert.That(s.Sanitize(Csi("48:2:10:20:30"))).IsEqualTo(Csi("48;2;10;20;30"));
        await Assert.That(s.Sanitize(Csi("48:5:7"))).IsEqualTo(Csi("48;5;7"));
    }

    /// Pins chunk safety: a sequence cut anywhere by a frame boundary is held and rewritten
    /// whole, including a lone escape at the end of a frame.
    [Test]
    public async Task A_sequence_cut_by_a_frame_boundary_is_rewritten_whole() {
        var whole = Csi("58;2;1;2;3") + "a" + Esc + "[24mb";
        for (var split = 1; split < whole.Length; split++) {
            var s = new TerminalFeedSanitizer();
            var text = s.Sanitize(whole[..split]) + s.Sanitize(whole[split..]);
            await Assert.That(text).IsEqualTo("a" + Csi("24") + "b");
        }
    }

    /// Pins the pass-through: text, other control sequences and well-formed SGR are untouched,
    /// and an empty SGR stays the reset it is.
    [Test]
    public async Task Everything_else_passes_through_untouched() {
        var s = new TerminalFeedSanitizer();
        var input = "plain " + Esc + "[2J" + Esc + "]0;title\a" + Csi("38;2;1;2;3") + Csi("") + Csi("0;1;4;24") + Esc + "M";
        await Assert.That(s.Sanitize(input)).IsEqualTo(input);
    }

    /// Pins the hold cap: parameter bytes that never end are not a sequence and flow through,
    /// so a stream of garbage cannot pin the sanitiser's buffer.
    [Test]
    public async Task An_over_long_partial_sequence_flows_through() {
        var s = new TerminalFeedSanitizer();
        var input = Esc + "[" + new string('1', 80);
        await Assert.That(s.Sanitize(input)).IsEqualTo(input);
    }

    /// Pins the private-parameter rule: a sequence such as xterm's modifyOtherKeys set, which
    /// only shares SGR's final byte, leaves the feed rather than reaching the SGR handler.
    [Test]
    public async Task Private_parameter_sequences_ending_in_m_are_dropped() {
        var s = new TerminalFeedSanitizer();
        await Assert.That(s.Sanitize(Esc + "[>4;2ma")).IsEqualTo("a");
        await Assert.That(s.Sanitize(Esc + "[?4mb")).IsEqualTo("b");
        await Assert.That(s.Sanitize(Esc + "[>5u" + Esc + "[<u")).IsEqualTo(Esc + "[>5u" + Esc + "[<u");
    }
}
