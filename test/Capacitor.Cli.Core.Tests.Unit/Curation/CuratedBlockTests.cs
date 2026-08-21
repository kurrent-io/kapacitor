using Capacitor.Cli.Core.Curation;

namespace Capacitor.Cli.Core.Tests.Unit.Curation;

public class CuratedBlockTests {
    static CuratedGuideline G(string cat, string text) => new(cat, text);

    [Test]
    public async Task Render_sorts_deterministically_and_returns_null_when_empty() {
        await Assert.That(CuratedBlock.Render([])).IsNull();

        var block = CuratedBlock.Render([G("quality", "b"), G("quality", "a"), G("efficiency", "c")])!;
        // efficiency sorts before quality; within quality, "a" before "b"
        var bullets = block.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToArray();
        await Assert.That(bullets).IsEquivalentTo(new[] { "- c", "- a", "- b" });
        await Assert.That(block.StartsWith(CuratedBlock.StartMarker, StringComparison.Ordinal)).IsTrue();
        await Assert.That(block.TrimEnd().EndsWith(CuratedBlock.EndMarker, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Splice_appends_when_absent_preserving_existing() {
        var content = "# My Project\n\nHand-written notes.\n";
        var block   = CuratedBlock.Render([G("quality", "always close writers")])!;
        var result  = CuratedBlock.Splice(content, block);

        await Assert.That(result.StartsWith("# My Project", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Contains("Hand-written notes.")).IsTrue();
        await Assert.That(result.Contains(CuratedBlock.StartMarker)).IsTrue();
        // idempotent
        await Assert.That(CuratedBlock.Splice(result, block)).IsEqualTo(result);
    }

    [Test]
    public async Task Splice_replaces_in_place_and_leaves_surrounding_untouched() {
        var head  = "# Title\n\nbefore\n";
        var tail  = "\nafter\n";
        var old   = CuratedBlock.Render([G("quality", "old line")])!;
        var start = head + old + tail;

        var fresh  = CuratedBlock.Render([G("quality", "new line")])!;
        var result = CuratedBlock.Splice(start, fresh);

        await Assert.That(result.Contains("new line")).IsTrue();
        await Assert.That(result.Contains("old line")).IsFalse();
        await Assert.That(result.Contains("before")).IsTrue();
        await Assert.That(result.Contains("after")).IsTrue();
    }

    [Test]
    public async Task Splice_removes_block_when_null() {
        var head   = "# Title\n\nkeep me\n";
        var block  = CuratedBlock.Render([G("quality", "x")])!;
        var withIt = CuratedBlock.Splice(head, block);

        var removed = CuratedBlock.Splice(withIt, null);
        await Assert.That(removed.Contains(CuratedBlock.StartMarker)).IsFalse();
        await Assert.That(removed.Contains("keep me")).IsTrue();
    }

    [Test]
    public async Task Splice_fails_closed_on_malformed_markers() {
        // start-only
        var startOnly = "before\n" + CuratedBlock.StartMarker + "\n- x\n";
        await Assert.That(() => CuratedBlock.Splice(startOnly, null)).Throws<CuratedBlockException>();

        // end-only
        var endOnly = "before\n" + CuratedBlock.EndMarker + "\n- x\n";
        await Assert.That(() => CuratedBlock.Splice(endOnly, null)).Throws<CuratedBlockException>();

        // out-of-order (end before start)
        var outOfOrder = CuratedBlock.EndMarker + "\n- x\n" + CuratedBlock.StartMarker + "\n";
        await Assert.That(() => CuratedBlock.Splice(outOfOrder, null)).Throws<CuratedBlockException>();

        // two blocks
        var twoBlocks =
            CuratedBlock.StartMarker + "\n- a\n" + CuratedBlock.EndMarker + "\n" +
            CuratedBlock.StartMarker + "\n- b\n" + CuratedBlock.EndMarker + "\n";
        await Assert.That(() => CuratedBlock.Splice(twoBlocks, null)).Throws<CuratedBlockException>();
    }

    [Test]
    public async Task Splice_result_ends_with_exactly_one_trailing_newline() {
        var content = "# My Project\n\nHand-written notes.\n";
        var block   = CuratedBlock.Render([G("quality", "always close writers")])!;

        // After appending a block the result ends with exactly one newline.
        var appended = CuratedBlock.Splice(content, block);
        await Assert.That(appended.EndsWith('\n')).IsTrue();
        await Assert.That(appended.EndsWith("\n\n", StringComparison.Ordinal)).IsFalse();

        // Byte-idempotent: second splice produces same output.
        await Assert.That(CuratedBlock.Splice(appended, block)).IsEqualTo(appended);

        // After removing the block the result ends with exactly one newline.
        var removed = CuratedBlock.Splice(appended, null);
        await Assert.That(removed.EndsWith('\n')).IsTrue();
        await Assert.That(removed.EndsWith("\n\n", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Splice_remove_does_not_eat_blank_lines_outside_markers() {
        // A block surrounded by user-authored blank lines — the blank lines that live
        // outside the markers must survive removal.
        var block  = CuratedBlock.Render([G("quality", "x")])!;
        var input  = "intro\n\n" + block + "\n\noutro\n";

        var result = CuratedBlock.Splice(input, null);

        await Assert.That(result.Contains(CuratedBlock.StartMarker)).IsFalse();
        await Assert.That(result.Contains(CuratedBlock.EndMarker)).IsFalse();
        await Assert.That(result.Contains("intro")).IsTrue();
        await Assert.That(result.Contains("outro")).IsTrue();
        // The blank line between intro and the block, and between the block and outro,
        // must both be preserved (the user put them there — they are outside the markers).
        await Assert.That(result.Contains("\n\n")).IsTrue();
    }

    [Test]
    public async Task ExtractBullets_returns_block_lines() {
        var block = CuratedBlock.Render([G("quality", "a"), G("quality", "b")])!;
        var bullets = CuratedBlock.ExtractBullets("noise\n" + block + "\nmore noise\n");
        await Assert.That(bullets).IsEquivalentTo(new[] { "a", "b" });
    }
}
