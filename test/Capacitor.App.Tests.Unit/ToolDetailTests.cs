using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class ToolDetailTests {
    [Test]
    public async Task Picks_the_first_present_key_in_priority_order() {
        await Assert.That(ToolDetail.From("""{"command":"ls","description":"List files"}""")).IsEqualTo("List files");
        await Assert.That(ToolDetail.From("""{"file_path":"/a/b.cs","command":"x"}""")).IsEqualTo("x");
        await Assert.That(ToolDetail.From("""{"pattern":"*.cs"}""")).IsEqualTo("*.cs");
        await Assert.That(ToolDetail.From("""{"input":"const r = 1;"}""")).IsEqualTo("const r = 1;");
    }

    [Test]
    public async Task Keeps_the_first_line_and_cuts_at_80_characters() {
        await Assert.That(ToolDetail.From("""{"command":"first line\nsecond"}""")).IsEqualTo("first line");
        var longLine = new string('x', 100);
        var detail = ToolDetail.From($$"""{"command":"{{longLine}}"}""");
        await Assert.That(detail.Length).IsEqualTo(80);
        await Assert.That(detail[^1]).IsEqualTo('…');
    }

    [Test]
    public async Task Never_splits_a_surrogate_pair_at_the_cut() {
        var line = new string('x', 78) + "😀" + new string('y', 20);
        var detail = ToolDetail.From($$"""{"command":"{{line}}"}""");
        await Assert.That(detail.Length).IsEqualTo(79);
        await Assert.That(detail[^1]).IsEqualTo('…');
        await Assert.That(char.IsHighSurrogate(detail[^2])).IsFalse();
    }

    [Test]
    public async Task Empty_when_nothing_applies() {
        await Assert.That(ToolDetail.From("""{"other":"x"}""")).IsEqualTo("");
        await Assert.That(ToolDetail.From("""{"command":"   "}""")).IsEqualTo("");
        await Assert.That(ToolDetail.From("not json")).IsEqualTo("");
        await Assert.That(ToolDetail.From(null)).IsEqualTo("");
    }

    /// Pins path display: a path under the session's root reads relative to it — the daemon's
    /// per-agent worktree under the repository counts as the root — while other keys and paths
    /// elsewhere are left alone.
    [Test]
    public async Task File_paths_read_relative_to_the_sessions_root() {
        const string repo = "/Users/me/dev/repo";
        await Assert.That(ToolDetail.From("""{"file_path":"/Users/me/dev/repo/.capacitor/worktrees/agent-1/src/Foo.cs"}""", repo)).IsEqualTo("src/Foo.cs");
        await Assert.That(ToolDetail.From("""{"file_path":"/Users/me/dev/repo/src/Foo.cs"}""", repo)).IsEqualTo("src/Foo.cs");
        await Assert.That(ToolDetail.From("""{"path":"/Users/me/dev/repo/.capacitor/worktrees/agent-1/docs"}""", repo)).IsEqualTo("docs");
        await Assert.That(ToolDetail.From("""{"notebook_path":"/Users/me/dev/repo/n.ipynb"}""", repo)).IsEqualTo("n.ipynb");
        await Assert.That(ToolDetail.From("""{"file_path":"/elsewhere/x.cs"}""", repo)).IsEqualTo("/elsewhere/x.cs");
        await Assert.That(ToolDetail.From("""{"command":"cat /Users/me/dev/repo/x"}""", repo)).IsEqualTo("cat /Users/me/dev/repo/x");
        await Assert.That(ToolDetail.From("""{"file_path":"/Users/me/dev/repo/src/Foo.cs"}""", null)).IsEqualTo("/Users/me/dev/repo/src/Foo.cs");
    }
}
