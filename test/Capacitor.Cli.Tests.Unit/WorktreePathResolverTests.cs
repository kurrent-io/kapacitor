namespace Capacitor.Cli.Tests.Unit;

public class WorktreePathResolverTests {
    // --- StripWorktreeSuffix (pure pattern logic, no fs) ---

    [Test]
    public async Task Strip_returns_parent_for_dot_claude_worktrees_leaf() {
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/.claude/worktrees/adaptive-toasting-squid");
        await Assert.That(result).IsEqualTo("/dev/kcap-cli");
    }

    [Test]
    public async Task Strip_returns_parent_for_dot_capacitor_worktrees() {
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/.capacitor/worktrees/agent-05e74395770b4a");
        await Assert.That(result).IsEqualTo("/dev/kcap-cli");
    }

    [Test]
    public async Task Strip_returns_parent_for_dot_git_worktrees() {
        var result = WorktreePathResolver.StripWorktreeSuffix("/home/me/repo/.git/worktrees/feature");
        await Assert.That(result).IsEqualTo("/home/me/repo");
    }

    [Test]
    public async Task Strip_is_generic_over_arbitrary_dot_segment() {
        // The whole point: not a hard-coded list of dot-segment names.
        var result = WorktreePathResolver.StripWorktreeSuffix("/u/proj/.somefuturetool/worktrees/slug");
        await Assert.That(result).IsEqualTo("/u/proj");
    }

    [Test]
    public async Task Strip_keeps_tail_below_worktree_root() {
        // Session ran inside the worktree at a subdirectory.
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/.claude/worktrees/slug/src/Foo");
        await Assert.That(result).IsEqualTo("/dev/kcap-cli");
    }

    [Test]
    public async Task Strip_accepts_backslash_separators() {
        var result = WorktreePathResolver.StripWorktreeSuffix(@"C:\dev\kcap-cli\.claude\worktrees\slug");
        await Assert.That(result).IsEqualTo(@"C:\dev\kcap-cli");
    }

    [Test]
    public async Task Strip_returns_null_when_no_dot_segment() {
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/worktrees/slug");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_returns_null_when_middle_segment_is_not_worktrees() {
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/.claude/sessions/slug");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_returns_null_when_slug_segment_is_missing() {
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/.claude/worktrees");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_returns_null_when_slug_segment_is_empty_trailing_separator() {
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/.claude/worktrees/");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_ignores_dot_dot_segment() {
        // ".." is not a real dot-segment — must not be treated as worktree root marker.
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/../worktrees/slug");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_ignores_lone_dot_segment() {
        // "." alone is not a real dot-segment.
        var result = WorktreePathResolver.StripWorktreeSuffix("/dev/kcap-cli/./worktrees/slug");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_returns_null_when_pattern_sits_at_root() {
        // /.git/worktrees/X — there's no meaningful project prefix above '/'.
        var result = WorktreePathResolver.StripWorktreeSuffix("/.git/worktrees/X");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_returns_null_for_windows_drive_root_backslash() {
        // C:\.claude\worktrees\X — would otherwise strip to "C:", which is
        // not a meaningful project directory.
        var result = WorktreePathResolver.StripWorktreeSuffix(@"C:\.claude\worktrees\X");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_returns_null_for_windows_drive_root_forward_slash() {
        // C:/.claude/worktrees/X — same issue, forward-slash variant.
        var result = WorktreePathResolver.StripWorktreeSuffix("C:/.claude/worktrees/X");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strip_still_works_for_windows_drive_subdirectory() {
        // C:\dev\repo\.claude\worktrees\X — the pattern is well below drive
        // root, so it still strips to C:\dev\repo.
        var result = WorktreePathResolver.StripWorktreeSuffix(@"C:\dev\repo\.claude\worktrees\X");
        await Assert.That(result).IsEqualTo(@"C:\dev\repo");
    }

    // --- Resolve (filesystem-aware wrapper) ---

    [Test]
    public async Task Resolve_returns_cwd_unchanged_when_it_exists() {
        var (path, stripped) = WorktreePathResolver.Resolve(
            "/dev/kcap-cli/.claude/worktrees/slug",
            _ => true                              // cwd exists → no strip even though pattern matches
        );

        await Assert.That(path).IsEqualTo("/dev/kcap-cli/.claude/worktrees/slug");
        await Assert.That(stripped).IsFalse();
    }

    [Test]
    public async Task Resolve_strips_to_parent_when_cwd_missing_and_parent_exists() {
        var (path, stripped) = WorktreePathResolver.Resolve(
            "/dev/kcap-cli/.claude/worktrees/slug",
            p => p == "/dev/kcap-cli"
        );

        await Assert.That(path).IsEqualTo("/dev/kcap-cli");
        await Assert.That(stripped).IsTrue();
    }

    [Test]
    public async Task Resolve_keeps_original_when_neither_cwd_nor_parent_exists() {
        var (path, stripped) = WorktreePathResolver.Resolve(
            "/dev/kcap-cli/.claude/worktrees/slug",
            _ => false
        );

        await Assert.That(path).IsEqualTo("/dev/kcap-cli/.claude/worktrees/slug");
        await Assert.That(stripped).IsFalse();
    }

    [Test]
    public async Task Resolve_keeps_original_when_path_does_not_match_pattern() {
        var (path, stripped) = WorktreePathResolver.Resolve(
            "/dev/kcap-cli/src/missing",
            _ => false
        );

        await Assert.That(path).IsEqualTo("/dev/kcap-cli/src/missing");
        await Assert.That(stripped).IsFalse();
    }

    [Test]
    public async Task Resolve_passes_empty_string_through() {
        var (path, stripped) = WorktreePathResolver.Resolve("", _ => false);
        await Assert.That(path).IsEqualTo("");
        await Assert.That(stripped).IsFalse();
    }

    [Test]
    public async Task Resolve_skips_filesystem_probe_when_pattern_does_not_match() {
        // Import calls Resolve for every session cwd; the vast majority
        // don't match the worktree pattern. The cheap pure-string scan
        // must run before any filesystem probe so non-matching paths
        // never touch the disk.
        var probes = 0;
        var (path, stripped) = WorktreePathResolver.Resolve(
            "/dev/kcap-cli/src/Foo",
            _ => { probes++; return true; }
        );

        await Assert.That(path).IsEqualTo("/dev/kcap-cli/src/Foo");
        await Assert.That(stripped).IsFalse();
        await Assert.That(probes).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_probes_filesystem_when_pattern_matches() {
        // Sanity check the other half of the perf ordering: when the
        // pattern matches we DO need to call exists.
        var probes = 0;
        WorktreePathResolver.Resolve(
            "/dev/kcap-cli/.claude/worktrees/slug",
            _ => { probes++; return false; }
        );

        await Assert.That(probes).IsGreaterThan(0);
    }
}
