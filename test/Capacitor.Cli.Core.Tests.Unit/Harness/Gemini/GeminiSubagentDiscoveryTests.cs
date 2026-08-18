using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Gemini;

/// <summary>
/// Unit tests for <see cref="GeminiSubagentDiscovery"/> — the shared discovery
/// used by both the import path and the live watcher: locate nested subagent files under
/// <c>chats/&lt;dashedParent&gt;/</c>, resolve each subagent's type from the parent's
/// <c>invoke_agent</c> call, and canonicalize the (dashed) subId for the server.
/// </summary>
public class GeminiSubagentDiscoveryTests {
    const string DashedParent = "0a900000-0000-4000-8000-000000000903";
    const string DashedSub    = "57d9b498-2705-4af5-b060-ebaba4878c96";

    static string WriteParentWithSubagent(string tmp, string agentName) {
        var chats = Path.Combine(tmp, "chats");
        Directory.CreateDirectory(chats);

        var parent = Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl");
        File.WriteAllLines(parent, new[] {
            $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"{{agentName}}","prompt":"p"},"agentId":"{{DashedSub}}","status":"success"}]}"""
        });

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);
        File.WriteAllText(
            Path.Combine(subDir, DashedSub + ".jsonl"),
            $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","kind":"subagent","directories":[]}""" + "\n");

        return parent;
    }

    [Test]
    public async Task EnumerateSubagentFiles_FindsNestedFile() {
        using var tmp = new TempDir();
        var parent = WriteParentWithSubagent(tmp.Path, "codebase_investigator");

        var files = GeminiSubagentDiscovery.EnumerateSubagentFiles(parent);

        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(Path.GetFileNameWithoutExtension(files[0])).IsEqualTo(DashedSub);
    }

    [Test]
    public async Task EnumerateSubagentFiles_EmptyWhenNoNestedDir() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");
        var parent = chats.PathTo("session-x.jsonl");
        File.WriteAllText(parent, $$"""{"sessionId":"{{DashedParent}}","kind":"main"}""" + "\n");

        await Assert.That(GeminiSubagentDiscovery.EnumerateSubagentFiles(parent).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveAgentTypes_MapsSubIdToParentAgentName() {
        using var tmp = new TempDir();
        var parent = WriteParentWithSubagent(tmp.Path, "codebase_investigator");

        var types = GeminiSubagentDiscovery.ResolveAgentTypes(parent);

        await Assert.That(types.GetValueOrDefault(DashedSub)).IsEqualTo("codebase_investigator");
    }

    [Test]
    [Arguments("57d9b498-2705-4af5-b060-ebaba4878c96", "57d9b49827054af5b060ebaba4878c96")]
    [Arguments("alreadydashless", "alreadydashless")]
    public async Task CanonicalAgentId_StripsDashes(string input, string expected) {
        await Assert.That(GeminiSubagentDiscovery.CanonicalAgentId(input)).IsEqualTo(expected);
    }

    // ── EnumerateDescendantFiles (D3: recursive grandchild discovery) ──────────────

    const string DashedGrandsub = "8c1a2222-3333-4444-5555-666677778888";

    // Root (with an invoke_agent call spawning Sub) + Sub's OWN dir with a grandsub file
    // (chats/<sub>/<grandsub>.jsonl) whose invocation is recorded in SUB's transcript, not
    // the root's — Sub's own transcript needs its own invoke_agent tool call for the
    // grandchild, using a DIFFERENT agent_name than the root's call, so a test can assert
    // type resolution reads from the immediate parent, not the root.
    static string WriteRootSubGrandsub(string tmp, string subAgentName, string grandsubAgentName) {
        var chats = Path.Combine(tmp, "chats");
        Directory.CreateDirectory(chats);

        var root = Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl");
        File.WriteAllLines(root, new[] {
            $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"{{subAgentName}}","prompt":"p"},"agentId":"{{DashedSub}}","status":"success"}]}"""
        });

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);
        var subFile = Path.Combine(subDir, DashedSub + ".jsonl");
        File.WriteAllLines(subFile, new[] {
            $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","kind":"subagent","directories":[]}""",
            $$"""{"id":"s1","timestamp":"2026-06-22T14:31:07.000Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__y","name":"invoke_agent","args":{"agent_name":"{{grandsubAgentName}}","prompt":"p2"},"agentId":"{{DashedGrandsub}}","status":"success"}]}"""
        });

        // Sub's OWN nested dir (rooted under the ROOT chats/ dir, per Sub's own dashed id) —
        // this is where the grandsub transcript lives.
        var grandsubDir = Path.Combine(chats, DashedSub);
        Directory.CreateDirectory(grandsubDir);
        File.WriteAllText(
            Path.Combine(grandsubDir, DashedGrandsub + ".jsonl"),
            $$"""{"sessionId":"{{DashedGrandsub}}","projectHash":"h","kind":"subagent","directories":[]}""" + "\n");

        return root;
    }

    [Test]
    public async Task EnumerateDescendantFiles_FindsGrandchildUnderSubsOwnDir() {
        using var tmp = new TempDir();
        var root = WriteRootSubGrandsub(tmp.Path, "codebase_investigator", "test_runner");

        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

        await Assert.That(result.DescendantsOmitted).IsEqualTo(0);
        await Assert.That(result.CountTruncated).IsFalse();
        await Assert.That(result.Files.Select(f => (f.DashedId, f.Depth)))
            .IsEquivalentTo(new[] { (DashedSub, 1), (DashedGrandsub, 2) });
    }

    [Test]
    public async Task EnumerateDescendantFiles_ResolvesGrandchildTypeFromImmediateParent_NotRoot() {
        using var tmp = new TempDir();
        var root = WriteRootSubGrandsub(tmp.Path, "codebase_investigator", "test_runner");

        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

        var grandsub = result.Files.Single(f => f.DashedId == DashedGrandsub);
        var sub      = result.Files.Single(f => f.DashedId == DashedSub);

        // The grandsub's immediate parent transcript is SUB's own file (not root's) —
        // resolving types from it must yield "test_runner", the distinct agent_name
        // recorded in Sub's own invoke_agent call, not the root's "codebase_investigator"
        // and not the "subagent" fallback a flat file list would produce.
        var grandsubTypes = GeminiSubagentDiscovery.ResolveAgentTypes(grandsub.ImmediateParentTranscriptPath);
        await Assert.That(grandsubTypes.GetValueOrDefault(DashedGrandsub)).IsEqualTo("test_runner");

        var subTypes = GeminiSubagentDiscovery.ResolveAgentTypes(sub.ImmediateParentTranscriptPath);
        await Assert.That(subTypes.GetValueOrDefault(DashedSub)).IsEqualTo("codebase_investigator");
    }

    [Test]
    public async Task EnumerateDescendantFiles_EmptyWhenNoNestedDir() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");
        var parent = chats.PathTo("session-x.jsonl");
        File.WriteAllText(parent, $$"""{"sessionId":"{{DashedParent}}","kind":"main"}""" + "\n");

        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(parent);

        await Assert.That(result.Files.Count).IsEqualTo(0);
        await Assert.That(result.DescendantsOmitted).IsEqualTo(0);
    }

    [Test]
    public async Task EnumerateDescendantFiles_depth_9_is_omitted_with_diagnostic() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        // A 9-level chain of subagent dirs below the root. Each level's directory is
        // rooted directly under the shared chats/ dir (chats/&lt;thatLevel'sOwnId&gt;/),
        // matching EnumerateDescendantFiles's "always derived from the ROOT chats/ dir,
        // never relative to a nested dir" contract — never chats/&lt;root&gt;/&lt;n1&gt;/&lt;n2&gt;/....
        var prevId = rootId;
        for (var depth = 1; depth <= 9; depth++) {
            var id  = $"00000000-0000-4000-8000-{depth:D12}";
            var dir = chats.PathTo(prevId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
            prevId = id;
        }

        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

        await Assert.That(result.Files.Count).IsEqualTo(8);
        await Assert.That(result.Files.Max(f => f.Depth)).IsEqualTo(8);
        await Assert.That(result.DescendantsOmitted).IsEqualTo(1);
        await Assert.That(result.OmittedDescendantIds.Count).IsEqualTo(1);
        await Assert.That(result.CountTruncated).IsFalse();
    }

    // the walker used to stop AT the boundary child (depth 9) and
    // never look below it, so a chain continuing to depth 10 was still counted as ONE omitted
    // descendant. The walk must now continue (never importing) below the cap to count the
    // WHOLE omitted subtree.
    [Test]
    public async Task EnumerateDescendantFiles_depth_9_and_10_chain_reports_omitted_two_not_one() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        // A 10-level chain of subagent dirs below the root.
        var prevId = rootId;
        for (var depth = 1; depth <= 10; depth++) {
            var id  = $"00000000-0000-4000-8000-{depth:D12}";
            var dir = chats.PathTo(prevId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
            prevId = id;
        }

        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

        await Assert.That(result.Files.Count).IsEqualTo(8);
        await Assert.That(result.Files.Max(f => f.Depth)).IsEqualTo(8);
        // Depths 9 AND 10 are omitted — TWO, not one.
        await Assert.That(result.DescendantsOmitted).IsEqualTo(2);
        await Assert.That(result.OmittedDescendantIds.Count).IsEqualTo(2);
        await Assert.That(result.CountTruncated).IsFalse();
    }

    // ── MaxCountingNodes scope (D3 review fix #4) ───────────────────────────────────
    //
    // The counting ceiling used to gate the WHOLE unified traversal via the shared visited-id
    // set's total size, so a root with a wide IN-CAP fan-out (well within MaxDescendantDepth)
    // could itself get silently truncated once 10,000 total ids had been discovered — corrupting
    // the import set, not merely the below-cap counting walk. It must now apply ONLY to
    // descendants already beyond the import cap.

    [Test]
    public async Task EnumerateDescendantFiles_wide_in_cap_fanout_beyond_the_counting_ceiling_finds_every_child() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        // MaxCountingNodes + 50 direct (depth-1, well within MaxDescendantDepth=8) subagent
        // files — before the fix, the old `visited.Count >= MaxCountingNodes` ceiling (which
        // counted the root + every discovered id, in-cap or not) would silently stop
        // discovery after ~10,000 total visited ids, dropping the tail of this real, in-cap
        // import set.
        var childCount = GeminiSubagentDiscovery.MaxCountingNodes + 50;
        var rootChatsDir = chats.PathTo(rootId);
        Directory.CreateDirectory(rootChatsDir);
        for (var i = 0; i < childCount; i++) {
            var id = $"11111111-{i / 100000000:D4}-4000-8000-{i:D12}";
            File.WriteAllText(Path.Combine(rootChatsDir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
        }

        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

        await Assert.That(result.Files.Count).IsEqualTo(childCount);
        await Assert.That(result.Files.All(f => f.Depth == 1)).IsTrue();
        await Assert.That(result.DescendantsOmitted).IsEqualTo(0);
        await Assert.That(result.CountTruncated).IsFalse();
    }

    [Test]
    public async Task EnumerateDescendantFiles_below_cap_ceiling_hit_does_not_corrupt_in_cap_files() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        // A depth 1..8 chain (in-cap, discovered), then MaxCountingNodes + 50 direct
        // children of the depth-8 node — ALL at depth 9, entirely below the import cap.
        // This pushes the below-cap counting walk past MaxCountingNodes, which must set
        // CountTruncated and stop growing the below-cap subtree WITHOUT touching the in-cap
        // files gathered above.
        var prevId = rootId;
        for (var depth = 1; depth <= 8; depth++) {
            var id  = $"00000000-0000-4000-8000-{depth:D12}";
            var dir = chats.PathTo(prevId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
            prevId = id;
        }

        var belowCapCount = GeminiSubagentDiscovery.MaxCountingNodes + 50;
        var depth8Dir     = Path.Combine(chats, prevId); // prevId is now the depth-8 node's own id
        Directory.CreateDirectory(depth8Dir);
        for (var i = 0; i < belowCapCount; i++) {
            var id = $"22222222-{i / 100000000:D4}-4000-8000-{i:D12}";
            File.WriteAllText(Path.Combine(depth8Dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
        }

        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

        // The in-cap import set (depths 1..8) is complete and uncorrupted.
        await Assert.That(result.Files.Count).IsEqualTo(8);
        await Assert.That(result.Files.Max(f => f.Depth)).IsEqualTo(8);

        // The below-cap counting walk hit the ceiling: truncated, and the omitted count/ids
        // are a lower bound (exactly MaxCountingNodes, not the true belowCapCount), never
        // silently reported as an exact/complete count.
        await Assert.That(result.CountTruncated).IsTrue();
        await Assert.That(result.DescendantsOmitted).IsEqualTo(GeminiSubagentDiscovery.MaxCountingNodes);
        await Assert.That(result.OmittedDescendantIds.Count).IsEqualTo(GeminiSubagentDiscovery.MaxCountingNodes);
    }

    // the ceiling used to bound the RETURNED omitted count/ids, but
    // not the actual WORK — every below-cap node already enqueued before the ceiling was hit
    // (up to MaxCountingNodes of them) still got individually dequeued and directory-enumerated
    // afterward. Once truncation is established, the below-cap frontier must be abandoned
    // outright rather than drained node-by-node, so the walk never probes any of those
    // below-cap nodes' own subdirectories.
    [Test]
    public async Task EnumerateDescendantFiles_below_cap_truncation_stops_expanding_already_queued_below_cap_nodes() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        // A depth 1..8 chain (in-cap), then MaxCountingNodes + 50 direct subagent files
        // under the depth-8 node's own dir — ALL at depth 9 (below cap). A SINGLE directory
        // enumeration of that dir returns this entire cap+50 list, so truncation is
        // established while still inside the IN-CAP walk's processing of the depth-8 node —
        // before the below-cap frontier is ever drained.
        var prevId = rootId;
        for (var depth = 1; depth <= 8; depth++) {
            var id  = $"00000000-0000-4000-8000-{depth:D12}";
            var dir = chats.PathTo(prevId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
            prevId = id;
        }

        var belowCapCount = GeminiSubagentDiscovery.MaxCountingNodes + 50;
        var depth8Dir     = Path.Combine(chats, prevId); // prevId is now the depth-8 node's own id
        Directory.CreateDirectory(depth8Dir);
        for (var i = 0; i < belowCapCount; i++) {
            var id = $"22222222-{i / 100000000:D4}-4000-8000-{i:D12}";
            File.WriteAllText(Path.Combine(depth8Dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
        }

        var dequeuedBelowCapCount = 0;
        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root, () => dequeuedBelowCapCount++);

        // Sanity: same correctness guarantees as the sibling test above.
        await Assert.That(result.Files.Count).IsEqualTo(8);
        await Assert.That(result.CountTruncated).IsTrue();
        await Assert.That(result.DescendantsOmitted).IsEqualTo(GeminiSubagentDiscovery.MaxCountingNodes);

        // The actual WORK is bounded too: truncation is already established from the single
        // directory enumeration of the depth-8 node's dir (an IN-CAP node), so the below-cap
        // frontier — which would hold up to ~10,000 depth-9 nodes — is never drained at all.
        await Assert.That(dequeuedBelowCapCount).IsEqualTo(0);
    }

    // a below-cap parent's single directory read used to always
    // enumerate (and OrderBy-sort) the WHOLE directory before CountTruncated could even be
    // detected — a depth-8 node with (say) a million depth-9 subagent files would list and sort
    // all million entries in one call. The per-parent read must instead be capped to (remaining
    // counting capacity + 1 sentinel), regardless of how many files the directory truly holds.
    [Test]
    public async Task EnumerateDescendantFiles_below_cap_parent_never_touches_more_than_the_counting_cap_in_a_single_read() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        // Depth 1..8 chain (in-cap), then a below-cap depth-9 fan-out FAR larger than
        // MaxCountingNodes under the depth-8 node's own dir — 5x the cap, to make an
        // unbounded read unmistakably distinguishable from a bounded one.
        var prevId = rootId;
        for (var depth = 1; depth <= 8; depth++) {
            var id  = $"00000000-0000-4000-8000-{depth:D12}";
            var dir = chats.PathTo(prevId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
            prevId = id;
        }

        var hugeChildCount = GeminiSubagentDiscovery.MaxCountingNodes * 5;
        var depth8Dir      = Path.Combine(chats, prevId); // prevId is now the depth-8 node's own id
        Directory.CreateDirectory(depth8Dir);
        for (var i = 0; i < hugeChildCount; i++) {
            var id = $"22222222-{i / 100000000:D4}-4000-8000-{i:D12}";
            File.WriteAllText(Path.Combine(depth8Dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
        }

        var maxTouched = 0;
        var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(
            root, onDequeueBelowCapNode: null,
            onBelowCapDirectoryRead: touched => maxTouched = Math.Max(maxTouched, touched));

        await Assert.That(result.CountTruncated).IsTrue();
        await Assert.That(result.DescendantsOmitted).IsEqualTo(GeminiSubagentDiscovery.MaxCountingNodes);

        // The crux of the fix: no single below-cap directory read ever pulled more than
        // (MaxCountingNodes + 1) raw entries from the OS enumeration — proving the read is
        // capped by Take() rather than listing/sorting the true 50,000-file directory before
        // truncating.
        await Assert.That(maxTouched).IsLessThanOrEqualTo(GeminiSubagentDiscovery.MaxCountingNodes + 1);
    }

    // a below-cap parent's bounded directory read caps RAW files
    // via Take(), but non-GUID filtering happens AFTER that — so a junk (non-GUID) *.jsonl file
    // landing inside the sentinel window used to silently consume the sole sentinel slot, filling
    // omittedIds to exactly MaxCountingNodes valid ids while CountTruncated stayed false: a false
    // "complete" report, even though the true descendant count could be larger. The bounded
    // enumerator must now report HitLimit from the RAW touched count (regardless of how many
    // entries turned out to be well-formed <GUID>.jsonl files), so this case is always caught.
    // Qodo review (PR #326, finding #3): an unreadable descendant directory (permissions, I/O
    // error) used to be swallowed by a bare `catch { return null; }`, silently dropping that
    // whole subtree with no counter and no diagnostic. It must now be counted in
    // UnreadableDescendantDirs so the omission is observable, while the walk still completes
    // for every other (readable) branch.
    [Test]
    public async Task EnumerateDescendantFiles_unreadable_in_cap_directory_is_counted_not_silent() {
        if (OperatingSystem.IsWindows()) return; // chmod-based unreadable dir is Unix-only

        using var tmp = new TempDir();
        var root = WriteParentWithSubagent(tmp.Path, "codebase_investigator");

        // Sub's own nested dir (where any grandchildren would live) exists but can't be
        // read — simulating a permissions error or a hostile/corrupt filesystem entry.
        var subDir = tmp.CreateDir("chats", DashedSub);
        File.SetUnixFileMode(subDir, UnixFileMode.None);

        try {
            var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

            // Sub itself (discovered from the ROOT's own chats dir) is still found — only
            // ITS children are unreachable.
            await Assert.That(result.Files.Select(f => f.DashedId)).IsEquivalentTo(new[] { DashedSub });
            await Assert.That(result.UnreadableDescendantDirs).IsEqualTo(1);
            await Assert.That(result.DescendantsOmitted).IsEqualTo(0);
            await Assert.That(result.CountTruncated).IsFalse();
        } finally {
            File.SetUnixFileMode(subDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Same bug, below-cap code path: the depth-8 node's own directory (necessarily below-cap
    // children) is unreadable. Must still be counted, and must not be confused with
    // CountTruncated (that's reserved for "we proved there might be more below the counting
    // cap," not "we couldn't read this directory at all").
    [Test]
    public async Task EnumerateDescendantFiles_unreadable_below_cap_directory_is_counted_not_silent() {
        if (OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        var prevId = rootId;
        for (var depth = 1; depth <= 8; depth++) {
            var id  = $"00000000-0000-4000-8000-{depth:D12}";
            var dir = chats.PathTo(prevId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
            prevId = id;
        }

        // The depth-8 node's own dir would hold only below-cap (depth-9) children — make it
        // unreadable instead of giving it any.
        var depth8Dir = Path.Combine(chats, prevId);
        Directory.CreateDirectory(depth8Dir);
        File.SetUnixFileMode(depth8Dir, UnixFileMode.None);

        try {
            var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

            // The in-cap import set (depths 1..8) is unaffected.
            await Assert.That(result.Files.Count).IsEqualTo(8);
            await Assert.That(result.Files.Max(f => f.Depth)).IsEqualTo(8);
            await Assert.That(result.UnreadableDescendantDirs).IsEqualTo(1);
            await Assert.That(result.DescendantsOmitted).IsEqualTo(0);
            await Assert.That(result.CountTruncated).IsFalse();
        } finally {
            File.SetUnixFileMode(depth8Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task EnumerateDescendantFiles_below_cap_boundary_plus_junk_entry_is_never_falsely_reported_complete() {
        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var rootId = "00000000-0000-4000-8000-000000000000";
        var root   = chats.PathTo("session-root.jsonl");
        File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

        var prevId = rootId;
        for (var depth = 1; depth <= 8; depth++) {
            var id  = $"00000000-0000-4000-8000-{depth:D12}";
            var dir = chats.PathTo(prevId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
            prevId = id;
        }

        var depth8Dir = Path.Combine(chats, prevId); // prevId is now the depth-8 node's own id
        Directory.CreateDirectory(depth8Dir);

        // MaxCountingNodes - 1 valid (GUID-named) below-cap descendant files, plus ONE
        // non-GUID junk *.jsonl file — MaxCountingNodes raw directory entries total, strictly
        // BELOW the bounded fetch's limit (MaxCountingNodes + 1). A definite raw EOF: the
        // whole directory is seen, so this is (correctly) NOT truncated.
        for (var i = 0; i < GeminiSubagentDiscovery.MaxCountingNodes - 1; i++) {
            var id = $"22222222-{i / 100000000:D4}-4000-8000-{i:D12}";
            File.WriteAllText(Path.Combine(depth8Dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
        }
        File.WriteAllText(Path.Combine(depth8Dir, "not-a-guid.jsonl"), """{"sessionId":"junk","kind":"subagent"}""" + "\n");

        var before = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);
        await Assert.That(before.CountTruncated).IsFalse();
        await Assert.That(before.DescendantsOmitted).IsEqualTo(GeminiSubagentDiscovery.MaxCountingNodes - 1);

        // Add ONE more genuinely-new valid descendant file under the SAME dir, pushing its
        // raw entry count to exactly MaxCountingNodes + 1 (MaxCountingNodes valid + 1 junk) —
        // precisely the bounded fetch's sentinel-inflated limit, so the junk file now lands
        // inside the sentinel window.
        var extraId = "33333333-0000-4000-8000-000000000000";
        File.WriteAllText(Path.Combine(depth8Dir, extraId + ".jsonl"), $$"""{"sessionId":"{{extraId}}","kind":"subagent"}""" + "\n");

        var after = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

        // The in-cap import set (depths 1..8) is unaffected either way.
        await Assert.That(after.Files.Count).IsEqualTo(8);

        // Before the fix: the junk entry silently consumed the sole sentinel slot, so exactly
        // MaxCountingNodes valid ids filled omittedIds and CountTruncated stayed false — the
        // genuinely-new descendant added above must change the reported signature:
        // DescendantsOmitted grows from MaxCountingNodes - 1 to MaxCountingNodes AND
        // CountTruncated flips to true (a safe over-report — the raw fetch exhausted its
        // limit, so we can no longer prove there isn't yet another descendant beyond it).
        await Assert.That(after.CountTruncated).IsTrue();
        await Assert.That(after.DescendantsOmitted).IsEqualTo(GeminiSubagentDiscovery.MaxCountingNodes);
        await Assert.That(after.OmittedDescendantIds.Count).IsEqualTo(GeminiSubagentDiscovery.MaxCountingNodes);
    }
}
