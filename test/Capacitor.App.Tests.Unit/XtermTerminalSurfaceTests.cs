using Capacitor.App.Services;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class XtermTerminalSurfaceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly string Esc = ((char)27).ToString();

    static bool Underlined(XtermTerminalSurface surface, int x) =>
        surface.Model.Terminal.Engine.Buffer.GetLine(0)![x].Attributes.IsUnderline();

    /// Pins the end-to-end guard: an underline-colour selector in the feed reaches the emulator
    /// rewritten, so it underlines nothing, while a real underline still does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_underline_colour_in_the_feed_does_not_underline_later_cells() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(40, 4);
            surface.Feed(Esc + "[58;2;255;4;4ma" + Esc + "[39mb" + Esc + "[4mc");

            await Assert.That(Underlined(surface, 0)).IsFalse();
            await Assert.That(Underlined(surface, 1)).IsFalse();
            await Assert.That(Underlined(surface, 2)).IsTrue();
        });
    }

    /// Pins the diagnostic tap: with a dump path, every fed frame is appended to that file as
    /// the bytes the emulator saw, before any rewriting.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dump_path_receives_every_fed_frame_before_rewriting() {
        await RunOnUiAsync(async () => {
            var dump = Tmp.PathTo("feed.bin");
            var surface = new XtermTerminalSurface(40, 4, dump);
            surface.Feed("ab" + Esc + "[59m");
            surface.Feed("c");

            await Assert.That(File.ReadAllText(dump)).IsEqualTo("ab" + Esc + "[59mc");
        });
    }
}
