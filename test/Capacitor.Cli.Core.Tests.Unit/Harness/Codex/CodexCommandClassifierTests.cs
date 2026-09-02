using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// The classifier's verdicts, pinned so the same command labels the same way wherever it is
/// classified: the dashboard and the desktop chat must never disagree about one shell line.
public class CodexCommandClassifierTests {
    [Test]
    [Arguments("sed -n '1,220p' docs/SKILL.md", "read", "docs/SKILL.md", "SKILL.md")]
    [Arguments("sed -n '1,220p' /Users/alexey/.codex/file.md", "read", "/Users/alexey/.codex/file.md", "file.md")]
    [Arguments("nl -ba docs/superpowers/specs/foo.md", "read", "docs/superpowers/specs/foo.md", "foo.md")]
    [Arguments("cat README.md", "read", "README.md", "README.md")]
    [Arguments("head -n 50 src/Models.cs", "read", "src/Models.cs", "Models.cs")]
    [Arguments("head -n50 src/Models.cs", "read", "src/Models.cs", "Models.cs")]
    [Arguments("tail -n +10 log.txt", "read", "log.txt", "log.txt")]
    [Arguments("less src/Foo.cs", "read", "src/Foo.cs", "Foo.cs")]
    [Arguments("bat src/Foo.cs", "read", "src/Foo.cs", "Foo.cs")]
    public async Task Classifies_ReadCommands(string cmd, string expectedType, string expectedPath, string expectedName) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo(expectedType);
        await Assert.That(hint.Path).IsEqualTo(expectedPath);
        await Assert.That(hint.Name).IsEqualTo(expectedName);
    }

    [Test]
    [Arguments("rg --files", "list_files", null)]
    [Arguments("rg --files src", "list_files", "src")]
    [Arguments("ls", "list_files", null)]
    [Arguments("ls src", "list_files", "src")]
    [Arguments("git ls-files", "list_files", null)]
    [Arguments("git ls-files src", "list_files", "src")]
    [Arguments("find src -type f", "list_files", "src")]
    [Arguments("tree -L 2 src", "list_files", "src")]
    public async Task Classifies_ListFilesCommands(string cmd, string expectedType, string? expectedPath) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo(expectedType);
        await Assert.That(hint.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("rg foo src", "search", "foo", "src")]
    [Arguments("rg -n 'pattern' src/Models.cs", "search", "pattern", "src/Models.cs")]
    [Arguments("grep TODO README.md", "search", "TODO", "README.md")]
    [Arguments("git grep TODO src", "search", "TODO", "src")]
    public async Task Classifies_SearchCommands(string cmd, string expectedType, string expectedQuery, string? expectedPath) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo(expectedType);
        await Assert.That(hint.Query).IsEqualTo(expectedQuery);
        await Assert.That(hint.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("git status --short")]
    [Arguments("git status")]
    [Arguments("pwd")]
    [Arguments("npm install")]
    [Arguments("docker compose up")]
    public async Task Classifies_UnknownCommands_AsNull(string cmd) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNull();
    }

    [Test]
    public async Task UnwrapsBashLcWrapper() {
        // Codex sometimes wraps the model's `cmd` in `bash -lc "..."` — the
        // classifier must look through the wrapper to find the real verb.
        var hint = CodexCommandClassifier.Classify("bash -lc \"sed -n '1,220p' src/Models.cs\"");
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo("read");
        await Assert.That(hint.Path).IsEqualTo("src/Models.cs");
    }

    [Test]
    public async Task Pipeline_WithUnknownStage_FallsBackToUnknown() {
        // Codex collapses any pipeline that contains an unknown segment to a
        // single Unknown — matches the Rust impl's behavior.
        var hint = CodexCommandClassifier.Classify("rg --files | xargs perl -pi -e 's/foo/bar/'");
        await Assert.That(hint).IsNull();
    }

    [Test]
    public async Task NullOrWhitespace_ReturnsNull() {
        await Assert.That(CodexCommandClassifier.Classify(null)).IsNull();
        await Assert.That(CodexCommandClassifier.Classify("")).IsNull();
        await Assert.That(CodexCommandClassifier.Classify("   ")).IsNull();
    }

    // Codex's TUI keeps pipelines like `rg --files | head -n 50` labelled as
    // ListFiles because head/tail/sed/wc/awk without file operands are
    // formatting helpers, not unknown stages. Mirror that behaviour so common
    // Codex preview pipelines don't fall back to Shell.

    [Test]
    [Arguments("rg --files | head -n 50", "list_files", null, null)]
    [Arguments("rg --files src | head -n 50", "list_files", null, "src")]
    [Arguments("rg -n TODO src | head -n 50", "search", "TODO", "src")]
    [Arguments("nl -ba src/Models.cs | sed -n '1,80p'", "read", null, "src/Models.cs")]
    [Arguments("cat README.md | wc -l", "read", null, "README.md")]
    [Arguments("ls src | sort | uniq", "list_files", null, "src")]
    public async Task HelperStages_DoNotCollapsePipeline(string cmd, string expectedType, string? expectedQuery, string? expectedPath) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo(expectedType);
        await Assert.That(hint.Query).IsEqualTo(expectedQuery);
        await Assert.That(hint.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("rg -l Foo src | xargs perl -pi -e 's/Foo/Bar/'")]
    [Arguments("rg -l Foo src | xargs sed -i s/Foo/Bar/")]
    [Arguments("rg --files | xargs rm -f")]
    [Arguments("rg --files | xargs mv -t /tmp")]
    [Arguments("rg --files | xargs sh -c 'rm \"$@\"'")]
    [Arguments("rg --files | xargs bash -c 'echo x > $1'")]
    [Arguments("find . -name '*.tmp' | xargs chmod +x")]
    [Arguments("rg --files | xargs python apply.py")]
    [Arguments("rg --files | xargs git add")]
    public async Task DestructiveXargs_CollapsesPipeline(string cmd) {
        // Mutating / destructive pipelines must NOT be silently dropped — they
        // change state beyond what the primary command's classification implies.
        // Default xargs to Unknown except a strict display-only allowlist so any
        // xargs subcommand we don't explicitly recognize falls back to Shell.
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNull();
    }

    [Test]
    [Arguments("rg --files | xargs cat", "list_files", null)]
    [Arguments("rg --files src | xargs wc -l", "list_files", "src")]
    [Arguments("rg --files | xargs file", "list_files", null)]
    [Arguments("rg --files | xargs ls -la", "list_files", null)]
    public async Task DisplayOnlyXargs_KeepsPrimaryClassification(string cmd, string expectedType, string? expectedPath) {
        // xargs into cat/wc/file/ls is purely read-only — keep the primary
        // command's classification so the row labels what the user actually sees.
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo(expectedType);
        await Assert.That(hint.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("rg TODO src > results.txt")]
    [Arguments("rg TODO src >> results.txt")]
    [Arguments("rg --files >> file-list.txt")]
    [Arguments("cat foo.txt > bar.txt")]
    [Arguments("ls src 2> err.log")]
    [Arguments("rg TODO src &> all.log")]
    [Arguments("rg TODO src > out 2>&1")]
    // Glued forms (no space between operator and target). The tokenizer
    // emits these as single tokens like `>results.txt` or `foo.txt>bar.txt`,
    // so the redirection guard must scan the raw script — not check token
    // equality — to catch them.
    [Arguments("rg TODO src >results.txt")]
    [Arguments("rg TODO src 2>err.log")]
    [Arguments("rg TODO src 1>out.txt")]
    [Arguments("cat foo.txt>bar.txt")]
    [Arguments("rg --files>file-list.txt")]
    [Arguments("ls src &>log")]
    // bash -lc wrapper around a redirecting inner script must still collapse —
    // the inner shell is the one that parses `>` as redirection.
    [Arguments("bash -lc \"rg TODO src > out\"")]
    public async Task Redirections_CollapsePipeline(string cmd) {
        // A redirection means the shell did more than the primary command's
        // classification implies (wrote a file, swallowed stderr, etc.). Don't
        // mislabel `rg TODO src > results.txt` as a benign Grep.
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNull();
    }

    [Test]
    // Outer-shell suffixes around bash -lc must NOT be silently dropped: an
    // outer redirection (`>out`) or connector (`&& rm -f out`) is executed by
    // the spawning shell, not the wrapped script.
    [Arguments("bash -lc \"rg TODO src\" >out")]
    [Arguments("bash -lc \"rg TODO src\" 2>err.log")]
    [Arguments("zsh -c \"rg TODO src\" > out")]
    [Arguments("bash -lc \"rg TODO src\" && rm -f out")]
    [Arguments("bash -lc \"rg --files\" | xargs rm -f")]
    public async Task BashLc_OuterSideEffects_CollapsePipeline(string cmd) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNull();
    }

    [Test]
    // A clean `bash -lc "..."` with no outer suffix still unwraps and
    // classifies (the unwrap only fires for exactly 3 outer tokens).
    [Arguments("bash -lc \"sed -n '1,220p' src/Models.cs\"", "read", "src/Models.cs")]
    [Arguments("bash -lc \"rg --files src\"", "list_files", "src")]
    [Arguments("zsh -c \"rg TODO src\"", "search", "src")]
    public async Task BashLc_CleanWrapper_StillClassifies(string cmd, string expectedType, string? expectedPath) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo(expectedType);
        await Assert.That(hint.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("rg '>' src", "search", ">", "src")]
    [Arguments("rg \"<\" src", "search", "<", "src")]
    [Arguments("grep '<tag>' file.txt", "search", "<tag>", "file.txt")]
    [Arguments("rg \">>\" src", "search", ">>", "src")]
    public async Task QuotedAngleBrackets_StayClassified(string cmd, string expectedType, string expectedQuery, string expectedPath) {
        // Single- or double-quoted `>` / `<` are literal regex chars, not
        // redirections. The quote-aware scanner must skip them so legit
        // searches don't regress to Shell.
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNotNull();
        await Assert.That(hint!.Type).IsEqualTo(expectedType);
        await Assert.That(hint.Query).IsEqualTo(expectedQuery);
        await Assert.That(hint.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("head -n 50")]         // helper alone
    [Arguments("sed -n '1,50p'")]     // sed without file
    [Arguments("nl -ba")]             // nl with only flags
    [Arguments("head -n 50 | wc -l")] // helpers all the way down
    public async Task HelperOnlyPipelines_ReturnNull(string cmd) {
        var hint = CodexCommandClassifier.Classify(cmd);
        await Assert.That(hint).IsNull();
    }
}
