# Standalone Snapshot Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `WorktreeManager`'s standalone snapshot path work for a non-git source, without materialising out-of-source content or writing outside the snapshot.

**Architecture:** Replace the recursive `CopyDirectory` with a no-follow walk that classifies every entry by `FileAttributes.ReparsePoint` before touching it, excludes the destination by a per-invocation marker file rather than by name, and excludes `.git` as an entry of any type. Restructure `CreateAsync`'s standalone branch so destination validation, an atomic claim, and fresh creation all happen before any write, with the claim's lifetime enclosing rollback.

**Tech Stack:** .NET 10, C#, TUnit (Microsoft Testing Platform), AOT-compiled CLI.

## Global Constraints

- **No Linear issue ID tokens in any `.cs` file.** CI job "No Linear issue IDs in C# source" fails the build. `rg -n "AI-[0-9]+" src/ test/ --type cs` must be empty before every push. Markdown is fine.
- Tests are TUnit on MTP. Run with `dotnet run --project <testproj>`, never `dotnet test`. Always `await` assertions.
- Symlink-dependent tests call the existing `SkipUnlessPosixSymlinks()` helper — Windows symlink creation needs Developer Mode or elevation.
- **Absence is never asserted with `File.Exists` / `Directory.Exists` / `Path.Exists`** — all follow, so a dangling link reports absent. Use directory enumeration plus `File.GetAttributes` / `LinkTarget`.
- Reuse the existing `ResolveDeepestExisting` and `IsAtOrUnder` helpers in this class for canonicalization and containment; do not write new path-comparison logic.
- Full spec: Linear AI-1675, comment rev 7 (codex-clean).

---

### Task 1: Pure path rules — `.git` naming and link-target admissibility

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.SnapshotRules.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/SnapshotPathRulesTests.cs`

**Interfaces:**
- Produces:
  - `internal static bool IsGitEntryName(string name)` — case-insensitive `.git` match.
  - `internal static bool IsAdmissibleLinkTarget(string linkDirRelative, string rawTarget)` — true when the raw target is relative (not rooted in any form) and the component walk from `linkDirRelative` never rises above the snapshot root.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public class SnapshotPathRulesTests {
    [Test]
    [Arguments(".git"), Arguments(".GIT"), Arguments(".Git")]
    public async Task Git_entry_names_match_case_insensitively(string name) =>
        await Assert.That(WorktreeManager.IsGitEntryName(name)).IsTrue();

    [Test]
    [Arguments(".gitignore"), Arguments("git"), Arguments(".gitmodules"), Arguments("")]
    public async Task Non_git_entry_names_do_not_match(string name) =>
        await Assert.That(WorktreeManager.IsGitEntryName(name)).IsFalse();

    // A link that never rises above the root is admissible from any depth.
    [Test]
    [Arguments("", "releases/v2")]
    [Arguments("a", "../b/file")]
    [Arguments("a/b", "../../c")]
    public async Task Targets_that_never_escape_are_admissible(string dir, string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget(dir, target)).IsTrue();

    // The relocation bug: resolves inside the source, but lands in a sibling once transplanted.
    [Test]
    [Arguments("", "../proj/secret")]
    [Arguments("a", "../../a/b")]
    public async Task Targets_that_escape_and_reenter_are_rejected(string dir, string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget(dir, target)).IsFalse();

    [Test]
    [Arguments("", "../../outside")]
    [Arguments("a", "../../../outside")]
    public async Task Targets_that_escape_are_rejected(string dir, string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget(dir, target)).IsFalse();

    // Every rooted form, not just fully-qualified absolutes.
    [Test]
    [Arguments("/etc/passwd"), Arguments("\\foo"), Arguments("C:foo"), Arguments("C:\\foo")]
    public async Task Rooted_targets_are_rejected(string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget("", target)).IsFalse();

    [Test]
    public async Task Empty_target_is_rejected() =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget("", "")).IsFalse();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/SnapshotPathRulesTests/*"`
Expected: compile error — `IsGitEntryName` / `IsAdmissibleLinkTarget` do not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Text;

namespace Capacitor.Cli.Daemon.Services;

public sealed partial class WorktreeManager {
    /// <summary>Whether an entry name is git control data, compared case-insensitively.
    ///
    /// <para><b>Deliberately asymmetric with the marker-based <c>.capacitor</c> exclusion.</b> A real
    /// <c>.git</c> must never land in the snapshot: the standalone path runs <c>git init</c> over the
    /// result, and a copied gitfile or repo directory makes that re-initialise a DIFFERENT repository and
    /// commit into it. So the fail-safe direction here is DROP. A <c>.Capacitor</c> directory is inert
    /// content, so its fail-safe direction is KEEP, and it gets the marker treatment instead. The marker
    /// technique is unavailable here because we do not own the source's <c>.git</c>.</para>
    ///
    /// <para><b>Accepted cost:</b> on a case-sensitive filesystem, inert content in a directory literally
    /// named <c>.GIT</c> is dropped. Safety over fidelity, and it matches this class's own
    /// <c>NormalizeRelativePath</c>, which already compares <c>.git</c> with OrdinalIgnoreCase.</para>
    /// </summary>
    internal static bool IsGitEntryName(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a symlink's RAW target may be recreated verbatim inside the snapshot.
    ///
    /// <para><b>Why "never escapes" and not "finally resolves inside".</b> Final-resolution containment is
    /// unsound under relocation, and reachable with a completely quiescent source. A source-root link
    /// <c>self -> ../&lt;source-dir-name&gt;/secret</c> resolves back inside the source — but the snapshot
    /// lives at <c>&lt;source&gt;/.capacitor/worktrees/&lt;name&gt;</c>, so the SAME raw target evaluated
    /// there becomes <c>&lt;...&gt;/worktrees/&lt;source-dir-name&gt;/secret</c>: a sibling agent's
    /// worktree. Identical bytes, different meaning, because the link's containing directory moved.</para>
    ///
    /// <para>Requiring the accumulated depth to never go negative is position-INDEPENDENT: a path that
    /// never rises above its own root resolves inside any root it is transplanted into, at equal depth. It
    /// also needs no path comparison, so it cannot inherit the OS-inferred case-sensitivity problem.</para>
    /// </summary>
    /// <param name="linkDirRelative">The link's own directory, relative to the snapshot root, using
    /// <c>/</c> separators. Empty for the root itself.</param>
    internal static bool IsAdmissibleLinkTarget(string linkDirRelative, string rawTarget) {
        if (string.IsNullOrEmpty(rawTarget)) return false;

        // Reject EVERY rooted form, not just fully-qualified absolutes: on Windows `\foo` and `C:foo` are
        // rooted-but-not-fully-qualified and would otherwise reach the depth walk as if relative.
        if (Path.IsPathRooted(rawTarget) || Path.IsPathFullyQualified(rawTarget)) return false;
        if (rawTarget.Length >= 2 && rawTarget[1] == ':') return false;   // `C:foo`, drive-relative

        var depth = 0;
        foreach (var part in SplitPathComponents(linkDirRelative)) {
            if (part == "..") { if (--depth < 0) return false; }
            else if (part != ".") depth++;
        }

        foreach (var part in SplitPathComponents(rawTarget)) {
            if (part == "..") { if (--depth < 0) return false; }
            else if (part != ".") depth++;
        }

        return true;
    }

    /// <summary>Splits on BOTH separators. A raw link target is authored by whoever wrote the tree, so it
    /// must not be parsed with one hardcoded separator.</summary>
    static IEnumerable<string> SplitPathComponents(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/SnapshotPathRulesTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/WorktreeManager.SnapshotRules.cs test/Capacitor.Cli.Tests.Unit/SnapshotPathRulesTests.cs
git commit -F- <<'EOF'
Add snapshot path rules: .git naming and link-target admissibility

Link admissibility requires the component walk to never rise above the
root, not merely to resolve inside it -- the latter is unsound once the
link is written at a different depth.
EOF
```

---

### Task 2: The no-follow copy walk

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.SnapshotRules.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs` — delete `CopyDirectory` (currently the last method, with the "restored deliberately" doc comment).

**Interfaces:**
- Consumes: `IsGitEntryName`, `IsAdmissibleLinkTarget` from Task 1.
- Produces: `void CopySnapshotTree(string source, string dest, string markerName)` — instance method so it can log skips.

- [ ] **Step 1: Implement the walk**

```csharp
    /// <summary>Copies <paramref name="source"/> into <paramref name="dest"/> without ever following a
    /// link and without descending into the destination.
    ///
    /// <para>Replaces the original <c>CopyDirectory</c>, which was broken in three ways at once: it
    /// recursed into its own destination until the path length blew up (so standalone snapshot creation
    /// had never completed), <c>File.Copy</c> materialised a symlink's TARGET (so a link to <c>~/.ssh</c>
    /// would have written real credentials into the agent's worktree as ordinary files), and its
    /// destination exclusion was a name match that is wrong in both directions under either case
    /// semantics.</para>
    ///
    /// <para><b>Guarantee and its limit.</b> For a source not concurrently written by another principal,
    /// nothing from outside <paramref name="source"/> is materialised. Classification and the subsequent
    /// read are NOT atomic, and .NET offers no portable no-follow open, so a principal able to swap an
    /// entry between the two can still defeat this. That limitation is accepted and documented for
    /// operators in the README's Daemon section; it is not closable here.</para>
    /// </summary>
    void CopySnapshotTree(string source, string dest, string markerName) =>
        CopySnapshotLevel(source, dest, relative: "", markerName);

    void CopySnapshotLevel(string source, string dest, string relative, string markerName) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source)) {
            var name = Path.GetFileName(entry);

            // Any type, every level: a `.git` FILE (`gitdir: ...`) is repository control data just as much
            // as the directory is, and copying one makes the snapshot's own `git init` re-initialise the
            // repository it names -- committing outside the snapshot entirely.
            if (IsGitEntryName(name)) continue;

            // Never copy this invocation's own bookkeeping. Only the exact name, so a user file that
            // merely resembles a marker is preserved like any other content.
            if (name == markerName) continue;

            // Classify BEFORE touching it. File.Copy would materialise a link's target, and recursion
            // through a directory link would copy the target tree and can cycle forever.
            var attrs = File.GetAttributes(entry);
            var destPath = Path.Combine(dest, name);

            if (attrs.HasFlag(FileAttributes.ReparsePoint)) {
                RecreateLinkIfAdmissible(entry, destPath, relative, name);
                continue;
            }

            if (!attrs.HasFlag(FileAttributes.Directory)) {
                File.Copy(entry, destPath);
                continue;
            }

            // A directory holding this invocation's marker IS the destination (or an ancestor of it).
            // Detected by READING the directory rather than by comparing its name or path, so it resolves
            // correctly under case-sensitive and case-insensitive semantics alike -- nothing is inferred
            // from the OS, which is what made every name- and path-based exclusion wrong in one direction
            // or the other.
            if (File.Exists(Path.Combine(entry, markerName))) continue;

            Directory.CreateDirectory(destPath);
            CopySnapshotLevel(entry, destPath, Combine(relative, name), markerName);
        }
    }

    void RecreateLinkIfAdmissible(string entry, string destPath, string relative, string name) {
        var target = new FileInfo(entry).LinkTarget ?? new DirectoryInfo(entry).LinkTarget;

        if (target is null || !IsAdmissibleLinkTarget(relative, target)) {
            LogSkippedLink(Combine(relative, name), target ?? "<unreadable>");
            return;
        }

        // Recreated as a LINK carrying the same raw target -- never resolved, never followed, so no
        // out-of-source bytes are ever written. A chain through a skipped link simply dangles.
        Directory.CreateSymbolicLink(destPath, target);
    }

    static string Combine(string relative, string name) =>
        relative.Length == 0 ? name : relative + "/" + name;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipped link {Path} in standalone snapshot: target {Target} is rooted or leaves the source")]
    partial void LogSkippedLink(string path, string target);
```

- [ ] **Step 2: Delete the old `CopyDirectory`**

Remove the entire `CopyDirectory` method and its `<summary>` block from the end of `WorktreeManager.cs`.

- [ ] **Step 3: Build**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: one error — `BuildStandaloneSnapshotAsync` still calls `CopyDirectory`. Task 3 fixes it.

- [ ] **Step 4: Commit after Task 3** (this task does not build standalone).

---

### Task 3: Destination validation, atomic claim, and ownership-gated rollback

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs` — `CreateAsync` (~line 204) and `BuildStandaloneSnapshotAsync` (~line 268).

**Interfaces:**
- Consumes: `CopySnapshotTree` from Task 2; existing `ResolveDeepestExisting`, `IsAtOrUnder`, `DeleteTreeNoFollow`.
- Produces: `internal static string? SnapshotFailurePoint` — test-only seam (Task 6).

- [ ] **Step 1: Move `Directory.CreateDirectory(worktreeRoot)` out of the common prologue**

In `CreateAsync`, delete the standalone `Directory.CreateDirectory(worktreeRoot);` line that currently sits before the `IsGitRepoWithCommits` check, and add it as the first statement inside the `if (await IsGitRepoWithCommits(repoPath))` block.

Rationale comment to add at the git-branch site:

```csharp
            // Created HERE, not in a shared prologue. The standalone branch below must validate the
            // destination chain BEFORE anything is created -- a pre-existing `.capacitor` or `worktrees`
            // symlink is followed by this call, which would put the snapshot at a location the source
            // tree chose. A standalone-only check placed after the branch decision runs too late.
            Directory.CreateDirectory(worktreeRoot);
```

- [ ] **Step 2: Replace the standalone tail of `CreateAsync`**

```csharp
        // Standalone: copy files + git init.
        return await CreateStandaloneAsync(repoPath, name, worktreeRoot, worktreePath);
    }

    /// <summary>Validates, claims, and builds a standalone snapshot.
    ///
    /// <para><b>Ordering is the whole point of this method.</b> Every check happens before any write, and
    /// the claim's lifetime strictly encloses rollback. Getting either wrong reopens a race or an escape
    /// that the checks themselves cannot see.</para>
    /// </summary>
    async Task<WorktreeInfo> CreateStandaloneAsync(
            string repoPath, string name, string worktreeRoot, string worktreePath) {
        // `name` reaches Path.Combine, which DISCARDS the root for an absolute value and would put the
        // destination anywhere; `../evil` would land it outside `worktrees`, where the marker cannot
        // exclude it and the copy recurses into its own destination again. Defense-in-depth today -- both
        // real callers omit `name` -- but this is a public method.
        if (name.Length == 0 || name != Path.GetFileName(name) ||
            name is "." or ".." || name.AsSpan().ContainsAny('/', '\\'))
            throw new InvalidOperationException($"standalone_snapshot_invalid_name: {name}");

        var root = ResolveDeepestExisting(repoPath);
        if (!IsAtOrUnder(ResolveDeepestExisting(worktreePath), root))
            throw new InvalidOperationException("standalone_snapshot_destination_escape");

        // No-follow, attribute-based, on the components WE introduce below the source root. Not from the
        // filesystem root: a source legitimately reached through a system symlink (macOS /tmp) is normal
        // and must not be refused. Path.Exists FOLLOWS, so a DANGLING link would report absent and then be
        // created through -- the same trap DeleteTreeNoFollow documents.
        var capacitorDir = Path.Combine(repoPath, ".capacitor");
        RefuseIfLink(capacitorDir);
        RefuseIfLink(worktreeRoot);

        Directory.CreateDirectory(worktreeRoot);

        // Atomic claim. `Directory.CreateDirectory` is a no-op on an existing directory, so the freshness
        // check below is check-then-create and cannot exclude a concurrent SAME-PRINCIPAL caller -- and
        // both racers would then consider the directory theirs, with either rollback deleting the other's
        // snapshot. There is no portable atomic directory claim, but FileMode.CreateNew on a FILE is
        // atomic everywhere, so the claim file supplies the exclusion the directory create cannot.
        var claimPath = Path.Combine(worktreeRoot, $".kcap-claim-{name}");
        FileStream claim;
        try {
            claim = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        } catch (IOException) {
            // Loser path: returns WITHOUT any destination cleanup. The rollback below is unconditional on
            // worktreePath, so a loser that fell through it would delete the WINNER's directory -- a claim
            // that made things worse than no claim.
            throw new InvalidOperationException($"standalone_snapshot_name_in_use: {name}");
        }

        // The claim is held until all success work is done, or until all rollback has finished. It is
        // released LAST. Releasing it inside BuildStandaloneSnapshotAsync would reopen the very race it
        // closes: that method's own unwinding completes before the catch below deletes the tree, so a new
        // same-name call could claim, create, and then be deleted by this call's delayed rollback.
        try {
            claim.Dispose();

            if (Directory.Exists(worktreePath) || File.Exists(worktreePath) || IsLinkEntry(worktreePath))
                throw new InvalidOperationException($"standalone_snapshot_destination_occupied: {name}");

            Directory.CreateDirectory(worktreePath);

            try {
                return await BuildStandaloneSnapshotAsync(repoPath, worktreePath);
            } catch {
                // Only the successful claimant reaches here, so this delete is ownership-gated.
                try { DeleteTreeNoFollow(worktreePath); } catch { /* keep the original failure */ }
                throw;
            }
        } finally {
            try { File.Delete(claimPath); } catch { /* best effort: a stale claim fails closed */ }
        }
    }

    /// <summary>Refuses a destination-chain component that is a link, WITHOUT following it.</summary>
    static void RefuseIfLink(string path) {
        if (IsLinkEntry(path))
            throw new InvalidOperationException($"standalone_snapshot_destination_link: {path}");
    }

    /// <summary>Whether the path itself is a link. Attribute-based, so a DANGLING link still reports as
    /// present -- which is the case that matters, since Path.Exists would call it absent.</summary>
    static bool IsLinkEntry(string path) {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
```

- [ ] **Step 3: Wire the marker into `BuildStandaloneSnapshotAsync`**

Replace its first line:

```csharp
    async Task<WorktreeInfo> BuildStandaloneSnapshotAsync(string repoPath, string worktreePath) {
        // Unique per invocation and created CreateNew, so a collision is detected rather than silently
        // shared, a hostile source cannot plant one that suppresses real content, and a marker orphaned by
        // a crash can never suppress anything on a later run.
        var markerName = $".kcap-snapshot-exclude-{Guid.NewGuid():N}";
        var markerPath = Path.Combine(Path.GetDirectoryName(worktreePath)!, markerName);

        try {
            // Fail-closed: without the marker the walk recurses into its own destination.
            using (new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            CopySnapshotTree(repoPath, worktreePath, markerName);
            FailHereIfRequested(nameof(CopySnapshotTree));
        } finally {
            // Cleanup failure logs and does not fail: the snapshot is already built and correct.
            try { File.Delete(markerPath); } catch { /* best effort */ }
        }

        // ... existing StripWorkspaceMcpConfig / git init / add / commit body unchanged ...
```

- [ ] **Step 4: Add the test seam**

```csharp
    /// <summary>Test-only injected failure point. A wall-clock race would make the claim-ownership tests
    /// flaky and, worse, green by luck; these need a deterministic rollback window.</summary>
    internal static string? SnapshotFailurePoint;
    internal static Func<Task>? SnapshotFailureHook;

    static void FailHereIfRequested(string point) {
        if (SnapshotFailurePoint != point) return;
        SnapshotFailureHook?.Invoke().GetAwaiter().GetResult();
        throw new InvalidOperationException("injected_standalone_failure");
    }
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
Expected: SUCCESS.

- [ ] **Step 6: Commit**

```bash
git add -u && git commit -F- <<'EOF'
Repair the standalone snapshot copy: no-follow walk, marker exclusion, claimed destination

Replaces CopyDirectory, which recursed into its own destination, copied
symlink targets rather than links, and excluded the destination by name.
EOF
```

---

### Task 4: Behaviour tests through the public API

**Files:**
- Modify: `test/Capacitor.Cli.Tests.Unit/WorktreeManagerTests.cs`

Add a `MakeNonGitSource()` fixture returning a temp directory that is deliberately NOT a git repo, plus `SnapshotEntryNames(path)` and `IsLink(path)` helpers built on `Directory.EnumerateFileSystemEntries` + `File.GetAttributes` — never `File.Exists`.

Implement spec tests 1–13, 15, 16. Each is a `[Test]` with `SkipUnlessPosixSymlinks()` where links are involved. Key anti-vacuity requirements, which are the point of several of these:

- Test 1 asserts a known file is in `HEAD` (`git ls-tree -r --name-only HEAD`), not merely that a commit exists.
- Test 3 asserts an ordinary sibling directory IS copied, so a no-op copy cannot pass.
- Test 4 asserts the cycle link **was recreated**, so dropping every link cannot pass.
- Test 6 plants a sentinel at the exact sibling path under `worktrees/` that the transplanted target would reach, **before** calling `CreateAsync`.
- Test 8 asserts unrelated lowercase `.capacitor` content survives, so dropping the whole directory cannot pass.
- Test 9 asserts the outside repo has no `HEAD` before and after, and that creation still succeeds.
- Test 13 asserts the pre-existing sentinel and `.git` data are still present and unmodified.

- [ ] **Step 1: Write the tests**
- [ ] **Step 2: Run — expect the new ones to pass and none of the existing ones to regress**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/WorktreeManagerTests/*"`

- [ ] **Step 3: Mutation-check the guards.** For each of tests 2, 3, 6, 9, 13, temporarily break the corresponding production guard and confirm the test fails. A guard assertion that never fails is not a guard.
- [ ] **Step 4: Commit**

---

### Task 5: Claim-ownership tests

**Files:**
- Modify: `test/Capacitor.Cli.Tests.Unit/WorktreeManagerTests.cs`

- [ ] **Step 1:** Test 14(a) — two concurrent `CreateAsync` calls with the same explicit `name`: exactly one succeeds, one throws `standalone_snapshot_name_in_use`, winner's snapshot intact.
- [ ] **Step 2:** Test 14(b) — set `SnapshotFailurePoint`/`SnapshotFailureHook` so call 1 fails and **blocks inside rollback** on a `TaskCompletionSource`; assert a second same-name call cannot acquire until call 1 releases; then release and assert the second call's snapshot is intact.
- [ ] **Step 3:** Test 14(c) — the loser leaves the winner's destination untouched.
- [ ] **Step 4:** Stale claim refuses that name fail-closed.
- [ ] **Step 5:** Reset the static seam in a `finally` in every test that sets it, so it cannot leak across tests. Mark these `[NotInParallel]` — they mutate process-global state.
- [ ] **Step 6: Commit**

---

### Task 6: Restore the standalone end-to-end MCP case

**Files:**
- Modify: `test/Capacitor.Cli.Tests.Unit/WorkspaceMcpNeutralizationTests.cs`

- [ ] **Step 1:** Add a test: a non-git source carrying `.mcp.json` yields a snapshot with it stripped, and absent from the initial commit (`git ls-tree -r --name-only HEAD`).
- [ ] **Step 2:** The fixture asserts the source is genuinely non-git first — otherwise the test silently exercises the linked-worktree branch and proves nothing.
- [ ] **Step 3: Commit**

---

### Task 7: README operator note

**Files:**
- Modify: `README.md` — `### Daemon` section.

- [ ] **Step 1:** Add the precondition: a standalone snapshot (taken when an agent targets a directory that is not a git repo with commits) must not be pointed at a workspace another principal can write during the launch; the practical test is whether any account or process other than the daemon's own has write access to that tree while an agent is starting.
- [ ] **Step 2:** Cross-reference it from the `CopySnapshotTree` doc comment.
- [ ] **Step 3: Commit**

---

## Pre-push checklist

- [ ] `rg -n "AI-[0-9]+" src/ test/ --type cs` — must be empty.
- [ ] `dotnet build Capacitor.Cli.slnx` (or the solution in use) succeeds.
- [ ] The unit suite passes: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
- [ ] The integration suite passes (this repo's CLI convention).
