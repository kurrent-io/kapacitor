using Capacitor.Cli.Core.Harness.Codex;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// <summary>
/// Covers the <c>[projects."…"]</c> pre-trust key form. Codex normalises the path before it
/// looks the entry up — on Windows to lowercase, drive letter included — and TOML keys are
/// case-sensitive, so a raw mixed-case worktree path is a *different* key. The write then has
/// no effect and Codex parks on its directory-trust prompt: the hosted agent never starts a
/// session and the dashboard sits on "Waiting for session to start…" with an empty terminal.
/// Same failure PR #265 fixed for Claude's <c>~/.claude.json</c> trust key.
/// </summary>
public class CodexProjectKeyTests {
    [Test]
    public async Task NormalizeProjectKey_lowercases_on_windows_and_keeps_backslashes() {
        if (!OperatingSystem.IsWindows()) return;

        var key = CodexPaths.NormalizeProjectKey(@"C:\Users\Me\Src\Repo\.capacitor\worktrees\Agent-01");

        await Assert.That(key).IsEqualTo(@"c:\users\me\src\repo\.capacitor\worktrees\agent-01");
    }

    /// <summary>Unix filesystems are case-sensitive and Codex does not fold case there —
    /// lowercasing would break the macOS/Linux path that already works.</summary>
    [Test]
    public async Task NormalizeProjectKey_preserves_case_on_unix() {
        if (OperatingSystem.IsWindows()) return;

        const string path = "/Users/Me/Src/Repo/.capacitor/worktrees/Agent-01";

        await Assert.That(CodexPaths.NormalizeProjectKey(path)).IsEqualTo(path);
    }

    [Test]
    public async Task NormalizeProjectKey_is_absolute_and_collapsed() {
        var key = CodexPaths.NormalizeProjectKey(
            Path.Combine(Directory.GetCurrentDirectory(), "sub", "..", "leaf"));

        await Assert.That(Path.IsPathFullyQualified(key)).IsTrue();
        await Assert.That(key).DoesNotContain("..");
    }

    /// <summary>End-to-end through the writer: two casings of one worktree must land on a single
    /// entry, not two. Before normalisation this produced two — one of them inert.</summary>
    [Test]
    public async Task TrustWorktree_writes_one_entry_regardless_of_input_casing() {
        using var tmp = new TempDir();
        var configPath = tmp.PathTo("config.toml");

        var upper = OperatingSystem.IsWindows()
            ? @"C:\Src\Repo\Worktrees\Agent-01"
            : "/Src/Repo/Worktrees/Agent-01";

        CodexConfigToml.TrustWorktree(upper, configPath);
        CodexConfigToml.TrustWorktree(upper.ToLowerInvariant(), configPath);

        var projects = (TomlTable)TomlSerializer.Deserialize<TomlTable>(
            await File.ReadAllTextAsync(configPath))!["projects"];

        // Windows folds both inputs onto one key; Unix is case-sensitive so they stay distinct.
        await Assert.That(projects.Count).IsEqualTo(OperatingSystem.IsWindows() ? 1 : 2);
        await Assert.That(projects.ContainsKey(CodexPaths.NormalizeProjectKey(upper))).IsTrue();
    }

    /// <summary>A second call for the same worktree is a no-op, so a relaunch doesn't rewrite
    /// the user's config.</summary>
    [Test]
    public async Task TrustWorktree_is_idempotent_under_the_normalized_key() {
        using var tmp = new TempDir();
        var configPath = tmp.PathTo("config.toml");

        var path = OperatingSystem.IsWindows() ? @"C:\Src\Wt" : "/Src/Wt";

        await Assert.That(CodexConfigToml.TrustWorktree(path, configPath))
            .IsEqualTo(CodexConfigToml.Change.Updated);
        await Assert.That(CodexConfigToml.TrustWorktree(path, configPath))
            .IsEqualTo(CodexConfigToml.Change.Unchanged);
    }
}
