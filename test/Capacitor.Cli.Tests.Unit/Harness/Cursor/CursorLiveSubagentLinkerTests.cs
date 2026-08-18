using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

public class CursorLiveSubagentLinkerTests {
    static string Write(string dir, string name, string content) {
        var p = Path.Combine(dir, name); File.WriteAllText(p, content); return p;
    }

    [Test]
    public async Task resolves_child_to_parent_by_prompt_hash() {
        using var tmp = new TempDir();
        var parent = Write(tmp.Path, "parent.jsonl",
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Task\",\"input\":{\"prompt\":\"do the thing\",\"subagent_type\":\"researcher\"}}]}}\n");
        var child = Write(tmp.Path, "child.jsonl",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"<user_query>do the thing</user_query>\"}]}}\n");

        var link = CursorLiveSubagentLinker.ResolveParent(
            "child", child, [("parent", parent)]);

        await Assert.That(link).IsNotNull();
        await Assert.That(link!.Value.ParentSessionId).IsEqualTo("parent");
        await Assert.That(link.Value.SubagentType).IsEqualTo("researcher");
    }

    [Test]
    public async Task no_match_returns_null() {
        using var tmp = new TempDir();
        var child = Write(tmp.Path, "child.jsonl",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"unrelated\"}]}}\n");
        var link = CursorLiveSubagentLinker.ResolveParent("child", child, []);
        await Assert.That(link).IsNull();
    }

    // --- DiscoverSiblingTranscripts: bounded scan of the real Cursor layout,
    // `<sanitized>/agent-transcripts/<sid>/<sid>.jsonl` ---

    [Test]
    public async Task discover_siblings_finds_other_session_dirs_under_the_same_agent_transcripts_root() {
        using var tmp = new TempDir();
        var transcripts = tmp.CreateDir("agent-transcripts");
        var childDir = transcripts.PathTo("child-sid");
        Directory.CreateDirectory(childDir);
        var childPath = Write(childDir, "child-sid.jsonl", "{}\n");

        var parentDir = transcripts.PathTo("parent-sid");
        Directory.CreateDirectory(parentDir);
        Write(parentDir, "parent-sid.jsonl", "{}\n");

        var siblings = CursorLiveSubagentLinker.DiscoverSiblingTranscripts(childPath);

        await Assert.That(siblings.Count).IsEqualTo(1);
        await Assert.That(siblings[0].SessionId).IsEqualTo("parentsid");
    }

    [Test]
    public async Task discover_siblings_excludes_its_own_session_dir() {
        using var tmp = new TempDir();
        var transcripts = tmp.CreateDir("agent-transcripts");
        var childDir = transcripts.PathTo("only-sid");
        Directory.CreateDirectory(childDir);
        var childPath = Write(childDir, "only-sid.jsonl", "{}\n");

        var siblings = CursorLiveSubagentLinker.DiscoverSiblingTranscripts(childPath);

        await Assert.That(siblings).IsEmpty();
    }

    [Test]
    public async Task discover_siblings_is_fail_open_for_a_missing_transcripts_root() {
        var siblings = CursorLiveSubagentLinker.DiscoverSiblingTranscripts(
            Path.Combine(Path.GetTempPath(), $"kcap-curs-missing-{Guid.NewGuid():N}", "sid", "sid.jsonl"));

        await Assert.That(siblings).IsEmpty();
    }

    // --- Marker persistence: the cross-invocation state a later hook call for the same
    // session_id needs, since CursorHookCommand is a fresh process per hook ---

    [Test]
    public async Task save_and_load_link_round_trips() {
        var sid = $"marker-{Guid.NewGuid():N}";
        try {
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(sid)).IsNull();

            CursorLiveSubagentLinker.SaveLink(sid, "parent-sid", "researcher");

            var loaded = CursorLiveSubagentLinker.TryLoadLink(sid);
            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Value.ParentSessionId).IsEqualTo("parent-sid");
            await Assert.That(loaded.Value.SubagentType).IsEqualTo("researcher");
        } finally {
            try { File.Delete(Path.Combine(Capacitor.Cli.Core.PathHelpers.ConfigPath("cursor-subagent-links"), sid)); } catch { }
        }
    }

    [Test]
    public async Task load_link_returns_null_for_an_unknown_session() {
        var loaded = CursorLiveSubagentLinker.TryLoadLink($"never-saved-{Guid.NewGuid():N}");
        await Assert.That(loaded).IsNull();
    }

    // --- Live/import parity: ResolveParent must agree with the exact correlation the import
    // path (CursorImportSource.ClassifyAsync -> CursorSubagentCorrelator.Correlate) would
    // compute over the same on-disk transcripts, so a live-then-import of the same session
    // converges on the same parent + subagent_type instead of drifting/duplicating.

    [Test]
    public async Task resolve_parent_agrees_with_the_import_path_correlator_over_the_same_transcripts() {
        using var tmp = new TempDir();
        const string prompt = "survey the auth module and report back";
        var parentId = "11111111111111111111111111111111";
        var childId  = "22222222222222222222222222222222";

        var parentPath = Write(tmp.Path, $"{parentId}.jsonl",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"kick things off\"}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Task\",\"input\":{\"prompt\":\"" + prompt + "\",\"subagent_type\":\"researcher\"}}]}}\n");
        var childPath = Write(tmp.Path, $"{childId}.jsonl",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"<user_query>\\n" + prompt + "\\n</user_query>\"}]}}\n");

        // Live path: only the child + its discovered siblings.
        var liveLink = CursorLiveSubagentLinker.ResolveParent(childId, childPath, [(parentId, parentPath)]);

        // Import path: CursorSubagentCorrelator.Correlate over the FULL discovered set
        // directly, exactly as CursorImportSource.ClassifyAsync calls it.
        var importLinks = CursorSubagentCorrelator.Correlate([(parentId, parentPath), (childId, childPath)]);

        await Assert.That(liveLink).IsNotNull();
        await Assert.That(importLinks.ContainsKey(childId)).IsTrue();
        await Assert.That(liveLink!.Value.ParentSessionId).IsEqualTo(importLinks[childId].ParentSessionId);
        await Assert.That(liveLink.Value.SubagentType).IsEqualTo(importLinks[childId].SubagentType);
    }
}
