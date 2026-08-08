using Capacitor.Cli.Core.Antigravity;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravitySubagents"/>. The child→parent map is
/// built from the parent transcript's <c>INVOKE_SUBAGENT</c> steps (the spawn-time signal) rather
/// than the child-reports-back <c>messages/*.json</c> scan used previously, which
/// missed children that never reported back.
/// </summary>
public class AntigravitySubagentsTests {
    static string NewHome() {
        var home = Path.Combine(Path.GetTempPath(), "kcap-agsub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    static void MakeBrainDir(string home, string convId) =>
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity", "brain", convId, ".system_generated", "logs"));

    // on-disk brain layout for the INVOKE_SUBAGENT-based BuildParentMap, one temp
    // home per test, auto-cleaned on dispose (mirrors the NewHome/MakeBrainDir pattern above,
    // but for transcript-based fixtures rather than messages/*.json).
    sealed class TempBrain : IDisposable {
        public string Home { get; } = NewHome();

        public void WriteTranscript(string convId, params string[] lines) {
            var dir = Path.Combine(Home, ".gemini", "antigravity", "brain", convId, ".system_generated", "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "transcript_full.jsonl"), lines);
        }

        public void Dispose() {
            try { Directory.Delete(Home, recursive: true); } catch { /* best effort */ }
        }
    }

    // the discovery scan is O(history) IO and must be interruptible.
    // Still valid under the transcript-based rewrite (scan is still O(history) IO).
    [Test]
    public async Task BuildParentMap_honors_cancellation() {
        var home = NewHome();
        try {
            MakeBrainDir(home, "P"); // at least one brain dir so the scan loop runs
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.That(() => AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "", ct: cts.Token))
                .Throws<OperationCanceledException>();
        } finally { Directory.Delete(home, recursive: true); }
    }

    // BuildParentMap now derives child→parent from the parent transcript's
    // INVOKE_SUBAGENT steps (spawn-time signal) instead of the child-reports-back
    // messages/*.json scan (the old signal was unreliable for children that never reported back —
    // see the d9956b89 case below).
    [Test]
    public async Task BuildParentMap_from_invoke_maps_children_to_the_invoking_parent() {
        using var tmp = new TempBrain();
        tmp.WriteTranscript("aaaaaaaa-0000-0000-0000-000000000001" /* parent1 */,
            """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"bbbbbbbb-0000-0000-0000-000000000001\"}"}""");
        tmp.WriteTranscript("bbbbbbbb-0000-0000-0000-000000000001" /* child1 */,
            """{"type":"USER_INPUT","content":"hi"}""");

        var map = AntigravitySubagents.BuildParentMap(home: tmp.Home, geminiCliHome: "");
        await Assert.That(map["bbbbbbbb-0000-0000-0000-000000000001"]).IsEqualTo("aaaaaaaa-0000-0000-0000-000000000001");
    }

    // BuildParentMapUnder scans an EXPLICIT brain root — the seam the dual-root import uses so an
    // agy-CLI-root chain maps as completely as a GUI one. Points it at the antigravity-cli brain.
    [Test]
    public async Task BuildParentMapUnder_maps_a_chain_in_the_agy_CLI_brain_root() {
        using var tmp = new TempBrain();
        const string parent = "dddddddd-0000-0000-0000-00000000d001";
        const string child  = "eeeeeeee-0000-0000-0000-00000000e001";
        var cliBrain = Path.Combine(tmp.Home, ".gemini", "antigravity-cli", "brain");
        var pDir = Path.Combine(cliBrain, parent, ".system_generated", "logs");
        var cDir = Path.Combine(cliBrain, child,  ".system_generated", "logs");
        Directory.CreateDirectory(pDir);
        Directory.CreateDirectory(cDir);
        File.WriteAllLines(Path.Combine(pDir, "transcript_full.jsonl"),
            [$$"""{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"{{child}}\"}"}"""]);
        File.WriteAllLines(Path.Combine(cDir, "transcript_full.jsonl"),
            ["""{"type":"USER_INPUT","content":"hi"}"""]);

        var map = AntigravitySubagents.BuildParentMapUnder(cliBrain);
        await Assert.That(map[child]).IsEqualTo(parent);

        // And the GUI-root overload sees nothing here — proving the roots are scanned independently.
        var guiMap = AntigravitySubagents.BuildParentMap(home: tmp.Home, geminiCliHome: "");
        await Assert.That(guiMap.ContainsKey(child)).IsFalse();
    }

    [Test]
    public async Task BuildParentMap_direction_is_from_the_invoker_not_the_message_sender() {
        // Regression for the messages-scan inversion (7f8d9d93 → a1204f98): the ROOT invokes the
        // child, even though a "Message from Root Agent" would have inverted it under the old
        // heuristic.
        using var tmp = new TempBrain();
        tmp.WriteTranscript("cccccccc-0000-0000-0000-000000000ccc" /* root */,
            """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"deadbeef-0000-0000-0000-00000000dead\"}"}""");
        tmp.WriteTranscript("deadbeef-0000-0000-0000-00000000dead" /* leaf */,
            """{"type":"USER_INPUT","content":"x"}""");

        var map = AntigravitySubagents.BuildParentMap(home: tmp.Home, geminiCliHome: "");
        await Assert.That(map["deadbeef-0000-0000-0000-00000000dead"]).IsEqualTo("cccccccc-0000-0000-0000-000000000ccc");
        await Assert.That(map.ContainsKey("cccccccc-0000-0000-0000-000000000ccc")).IsFalse(); // root has no parent
    }

    [Test]
    public async Task BuildParentMap_captures_children_that_never_reported_back() {
        // d9956b89 case: a child was invoked but only errored (no clean report). INVOKE still
        // maps it — this is precisely the gap the messages-scan heuristic couldn't cover.
        using var tmp = new TempBrain();
        tmp.WriteTranscript("eeeeeeee-0000-0000-0000-00000000000e" /* p */,
            """{"type":"INVOKE_SUBAGENT","content":"[{\"conversationId\":\"e4404400-0000-0000-0000-0000000e4404\"}]"}""");
        // no transcript for the errored child dir on disk beyond the invoke — still mapped
        var map = AntigravitySubagents.BuildParentMap(home: tmp.Home, geminiCliHome: "");
        await Assert.That(map["e4404400-0000-0000-0000-0000000e4404"]).IsEqualTo("eeeeeeee-0000-0000-0000-00000000000e");
    }

    [Test]
    public async Task BuildParentMap_ignores_self_invocations_and_unreadable_transcripts() {
        using var tmp = new TempBrain();
        // Self-guard: a conversation "inviting" itself must not create a self-edge.
        tmp.WriteTranscript("11111111-0000-0000-0000-000000000001",
            """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"11111111-0000-0000-0000-000000000001\"}"}""");
        // A brain dir with no transcript_full.jsonl at all is skipped, not an error.
        MakeBrainDir(tmp.Home, "22222222-0000-0000-0000-000000000002");

        var map = AntigravitySubagents.BuildParentMap(home: tmp.Home, geminiCliHome: "");
        await Assert.That(map.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BuildParentMap_is_empty_when_no_antigravity_data() {
        var home = NewHome();
        try {
            await Assert.That(AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "").Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task BuildParentMap_is_deterministic_when_a_child_maps_to_multiple_parents() {
        using var tmp = new TempBrain();
        // Pathological non-tree data: C is invoked by both "Pz" and "Pa". The
        // lexicographically-smallest parent wins regardless of directory enumeration order.
        tmp.WriteTranscript("6a000000-0000-0000-0000-00000000000a" /* Pa */,
            """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"cc000000-0000-0000-0000-0000000000cc\"}"}""");
        tmp.WriteTranscript("6a000000-0000-0000-0000-00000000000b" /* Pz, sorts after Pa */,
            """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"cc000000-0000-0000-0000-0000000000cc\"}"}""");

        var map = AntigravitySubagents.BuildParentMap(home: tmp.Home, geminiCliHome: "");
        await Assert.That(map["cc000000-0000-0000-0000-0000000000cc"]).IsEqualTo("6a000000-0000-0000-0000-00000000000a");
    }

    [Test]
    public async Task ResolveParent_reads_through_to_the_invoke_based_map() {
        using var tmp = new TempBrain();
        tmp.WriteTranscript("3a000000-0000-0000-0000-00000000003a" /* parent */,
            """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"3b000000-0000-0000-0000-00000000003b\"}"}""");

        await Assert.That(AntigravitySubagents.ResolveParent(
            "3b000000-0000-0000-0000-00000000003b", home: tmp.Home, geminiCliHome: "")).IsEqualTo("3a000000-0000-0000-0000-00000000003a");
        await Assert.That(AntigravitySubagents.ResolveParent(
            "3a000000-0000-0000-0000-00000000003a", home: tmp.Home, geminiCliHome: "")).IsNull();
    }

    [Test]
    public async Task ResolveTopLevelAncestor_walks_a_deep_chain_to_the_root() {
        // P ← C ← G : the grandchild resolves to P, not C.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["C"] = "P", ["G"] = "C" };
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("G", map)).IsEqualTo("P");
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("C", map)).IsEqualTo("P");
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("P", map)).IsEqualTo("P");
        // An unknown/unlinked conversation is its own root.
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("X", map)).IsEqualTo("X");
    }

    [Test]
    public async Task ResolveTopLevelAncestor_is_cycle_safe() {
        // A ↔ B cycle: each member resolves to itself rather than looping forever / vanishing.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "B", ["B"] = "A" };
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("A", map)).IsEqualTo("A");
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("B", map)).IsEqualTo("B");
    }

    [Test]
    public async Task BuildRootDescendants_groups_transitive_descendants_under_each_root() {
        // Tree: P ← C ← G, and standalone S. Root P owns {C, G}; S owns nothing.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["C"] = "P", ["G"] = "C" };
        var byRoot = AntigravitySubagents.BuildRootDescendants(new[] { "P", "C", "G", "S" }, map);

        await Assert.That(byRoot.ContainsKey("P")).IsTrue();
        await Assert.That(byRoot["P"].OrderBy(x => x).ToList()).IsEquivalentTo(new List<string> { "C", "G" });
        await Assert.That(byRoot.ContainsKey("S")).IsTrue();
        await Assert.That(byRoot["S"].Count).IsEqualTo(0);
        // Children are NOT keys (they import under their root, not as sessions).
        await Assert.That(byRoot.ContainsKey("C")).IsFalse();
        await Assert.That(byRoot.ContainsKey("G")).IsFalse();
    }

    [Test]
    public async Task BuildRootDescendants_imports_cycle_members_standalone() {
        // A ↔ B cycle plus a real root R with child C. Cycle members each become their own root
        // (with no descendants) rather than being lost.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "B", ["B"] = "A", ["C"] = "R" };
        var byRoot = AntigravitySubagents.BuildRootDescendants(new[] { "A", "B", "R", "C" }, map);

        await Assert.That(byRoot.ContainsKey("A")).IsTrue();
        await Assert.That(byRoot["A"].Count).IsEqualTo(0);
        await Assert.That(byRoot.ContainsKey("B")).IsTrue();
        await Assert.That(byRoot["B"].Count).IsEqualTo(0);
        await Assert.That(byRoot["R"]).IsEquivalentTo(new List<string> { "C" });
    }

    [Test]
    public async Task ChildConversationIds_extracts_all_children_from_one_invoke_step() {
        // One INVOKE_SUBAGENT step listing multiple children (as c8a36fda does on disk).
        const string line = """
            {"type":"INVOKE_SUBAGENT","content":"Created the following subagents:\n[{\"conversationId\":\"9999c82b-0000-0000-0000-000000000001\"},{\"conversationId\":\"189ba558-0000-0000-0000-000000000002\"}]\nThey will report back."}
            """;
        var ids = AntigravitySubagents.ChildConversationIdsFromLine(line);
        await Assert.That(ids).IsEquivalentTo(new List<string> {
            "9999c82b-0000-0000-0000-000000000001", "189ba558-0000-0000-0000-000000000002" });
    }

    [Test]
    public async Task ChildConversationIds_single_child_object_payload() {
        const string line = """
            {"type":"INVOKE_SUBAGENT","content":"Created the following subagents:\n{\"conversationId\":\"6111e615-3caa-4fe8-9d55-b85c43f2cf1f\",\"logAbsoluteUri\":\"file:///x\"}"}
            """;
        var ids = AntigravitySubagents.ChildConversationIdsFromLine(line);
        await Assert.That(ids).IsEquivalentTo(new List<string> { "6111e615-3caa-4fe8-9d55-b85c43f2cf1f" });
    }

    [Test]
    public async Task ChildConversationIds_ignores_conversationId_outside_an_invoke_step() {
        // A conversationId quoted in a non-INVOKE line (e.g. error text) must NOT match — proves
        // structural, content-scoped parsing rather than a whole-line regex.
        const string line = """
            {"type":"PLANNER_RESPONSE","content":"note conversationId 6111e615-3caa-4fe8-9d55-b85c43f2cf1f in passing"}
            """;
        await Assert.That(AntigravitySubagents.ChildConversationIdsFromLine(line)).IsEmpty();
    }

    [Test]
    public async Task ChildConversationIds_drops_non_guid_and_dedupes() {
        const string line = """
            {"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"not-a-guid\"}\n{\"conversationId\":\"6111e615-3caa-4fe8-9d55-b85c43f2cf1f\"}\n{\"conversationId\":\"6111e615-3caa-4fe8-9d55-b85c43f2cf1f\"}"}
            """;
        var ids = AntigravitySubagents.ChildConversationIdsFromLine(line);
        await Assert.That(ids).IsEquivalentTo(new List<string> { "6111e615-3caa-4fe8-9d55-b85c43f2cf1f" });
    }

    [Test]
    public async Task ChildConversationIds_blank_partial_or_malformed_line_is_empty() {
        await Assert.That(AntigravitySubagents.ChildConversationIdsFromLine("")).IsEmpty();
        await Assert.That(AntigravitySubagents.ChildConversationIdsFromLine("{\"type\":\"INVOKE_SUBAGENT\",\"content\":\"trunca")).IsEmpty();
        await Assert.That(AntigravitySubagents.ChildConversationIdsFromLine("not json")).IsEmpty();
    }

    [Test]
    public async Task IsInvokeSubagentLine_true_only_for_invoke_steps() {
        await Assert.That(AntigravitySubagents.IsInvokeSubagentLine("""{"type":"INVOKE_SUBAGENT","content":"x"}""")).IsTrue();
        await Assert.That(AntigravitySubagents.IsInvokeSubagentLine("""{"type":"PLANNER_RESPONSE"}""")).IsFalse();
        await Assert.That(AntigravitySubagents.IsInvokeSubagentLine("nonsense")).IsFalse();
    }
}
