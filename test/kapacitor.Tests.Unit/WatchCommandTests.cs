using kapacitor;

namespace kapacitor.Tests.Unit;

public class TryExtractUserTextTests {
    [Test]
    [Arguments("""{"type":"user","message":{"content":"hello world"}}""", "hello world")]
    [Arguments("""{"type":"user","message":{"content":"fix the bug"}}""", "fix the bug")]
    public async Task StringContent_ReturnsText(string line, string expected) {
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ArrayContent_ReturnsFirstTextElement() {
        var line = """{"type":"user","message":{"content":[{"type":"text","text":"from array"}]}}""";
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("from array");
    }

    [Test]
    public async Task ArrayContent_SkipsNonTextElements() {
        var line = """{"type":"user","message":{"content":[{"type":"image","url":"x"},{"type":"text","text":"second"}]}}""";
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("second");
    }

    [Test]
    [Arguments("""{"type":"assistant","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"system","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"user","isMeta":true,"message":{"content":"meta stuff"}}""")]
    [Arguments("""{"type":"user","message":{"content":"<local-command-stdout>some output"}}""")]
    [Arguments("""{"type":"user","message":{"content":[{"type":"text","text":"<local-command-stdout>output"}]}}""")]
    [Arguments("not json at all")]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("""{"type":"user"}""")]
    [Arguments("""{"type":"user","message":{}}""")]
    [Arguments("""{"type":"user","message":{"content":[]}}""")]
    public async Task ReturnsNull_ForInvalidOrFilteredInput(string line) {
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsNull();
    }
}

public class StripMarkdownTests {
    [Test]
    [Arguments("**bold**", "bold")]
    [Arguments("*italic*", "italic")]
    [Arguments("`code`", "code")]
    [Arguments("## heading", "heading")]
    [Arguments("### sub heading", "sub heading")]
    [Arguments("**bold** and *italic* and `code`", "bold and italic and code")]
    [Arguments("plain text", "plain text")]
    [Arguments("", "")]
    public async Task StripsMarkdownFormatting(string input, string expected) {
        var result = WatchCommand.StripMarkdown(input);
        await Assert.That(result).IsEqualTo(expected);
    }
}

public class RepoPayloadChangedTests {
    static RepositoryPayload MakePayload(
        string owner = "o", string repo = "r", string branch = "main",
        int? prNumber = 1, string prUrl = "u", string prTitle = "t"
    ) => new() { Owner = owner, RepoName = repo, Branch = branch, PrNumber = prNumber, PrUrl = prUrl, PrTitle = prTitle };

    [Test]
    public async Task NullCurrent_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, MakePayload())).IsFalse();

    [Test]
    public async Task NullLastSent_ReturnsTrue() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), null)).IsTrue();

    [Test]
    public async Task BothNull_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, null)).IsFalse();

    [Test]
    public async Task SameValues_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), MakePayload())).IsFalse();

    [Test]
    [Arguments("Owner")]
    [Arguments("RepoName")]
    [Arguments("Branch")]
    [Arguments("PrNumber")]
    [Arguments("PrUrl")]
    [Arguments("PrTitle")]
    public async Task DifferentField_ReturnsTrue(string field) {
        var a = MakePayload();
        var b = field switch {
            "Owner"    => a with { Owner = "x" },
            "RepoName" => a with { RepoName = "x" },
            "Branch"   => a with { Branch = "x" },
            "PrNumber" => a with { PrNumber = 99 },
            "PrUrl"    => a with { PrUrl = "x" },
            "PrTitle"  => a with { PrTitle = "x" },
            _          => a
        };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsTrue();
    }

    [Test]
    public async Task NonComparedFields_DoNotTriggerChange() {
        var a = MakePayload() with { UserName = "alice" };
        var b = MakePayload() with { UserName = "bob" };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsFalse();
    }
}

public class CountFileLinesTests {
    [Test]
    [Arguments("line1\nline2\nline3\n", 3)]
    [Arguments("single", 1)]
    [Arguments("", 0)]
    public async Task CountsLines(string content, int expected) {
        var path = Path.GetTempFileName();
        try {
            File.WriteAllText(path, content);
            await Assert.That(WatchCommand.CountFileLines(path)).IsEqualTo(expected);
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MissingFile_ReturnsZero() =>
        await Assert.That(WatchCommand.CountFileLines("/tmp/nonexistent_" + Guid.NewGuid())).IsEqualTo(0);
}
