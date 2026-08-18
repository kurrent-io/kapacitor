using System.Text;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorAppendOnlyProbeTests {
    [Test]
    public async Task PrefixStable_true_when_later_is_a_pure_append() {
        var earlierBytes = Encoding.UTF8.GetBytes("line1\nline2\n");
        var laterBytes   = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var earlier = new CursorAppendOnlyProbe.Sample(earlierBytes.Length, CursorAppendOnlyProbe.Sha256Hex(earlierBytes));
        var later   = new CursorAppendOnlyProbe.Sample(laterBytes.Length, CursorAppendOnlyProbe.Sha256Hex(laterBytes));
        await Assert.That(CursorAppendOnlyProbe.PrefixStable(earlier, later, laterBytes)).IsTrue();
    }

    [Test]
    public async Task PrefixStable_false_when_prefix_was_rewritten() {
        var earlierBytes = Encoding.UTF8.GetBytes("lineA\nlineB\n");
        var laterBytes   = Encoding.UTF8.GetBytes("lineX\nlineB\nlineC\n"); // first line changed
        var earlier = new CursorAppendOnlyProbe.Sample(earlierBytes.Length, CursorAppendOnlyProbe.Sha256Hex(earlierBytes));
        var later   = new CursorAppendOnlyProbe.Sample(laterBytes.Length, CursorAppendOnlyProbe.Sha256Hex(laterBytes));
        await Assert.That(CursorAppendOnlyProbe.PrefixStable(earlier, later, laterBytes)).IsFalse();
    }

    [Test]
    public async Task PrefixStable_false_on_length_shrink() {
        var earlierBytes = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var laterBytes   = Encoding.UTF8.GetBytes("line1\n");
        var earlier = new CursorAppendOnlyProbe.Sample(earlierBytes.Length, CursorAppendOnlyProbe.Sha256Hex(earlierBytes));
        var later   = new CursorAppendOnlyProbe.Sample(laterBytes.Length, CursorAppendOnlyProbe.Sha256Hex(laterBytes));
        await Assert.That(CursorAppendOnlyProbe.PrefixStable(earlier, later, laterBytes)).IsFalse();
    }
}
