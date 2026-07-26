using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

public class MemoryIndexUrlTests {
    [Test]
    public async Task No_repo_or_machine_omits_query() {
        var url = ClaudeHookCommand.BuildMemoryIndexUrl("http://srv", repoHash: null, machineId: null);
        await Assert.That(url).IsEqualTo("http://srv/api/memories/index");
    }

    [Test]
    public async Task Includes_repo_and_machine_when_present() {
        var url = ClaudeHookCommand.BuildMemoryIndexUrl("http://srv", "abcd1234", "mach-01");
        await Assert.That(url).IsEqualTo("http://srv/api/memories/index?repo=abcd1234&machine=mach-01");
    }

    [Test]
    public async Task Repo_only_omits_machine_param() {
        var url = ClaudeHookCommand.BuildMemoryIndexUrl("http://srv", "abcd1234", machineId: null);
        await Assert.That(url).IsEqualTo("http://srv/api/memories/index?repo=abcd1234");
    }

    [Test]
    public async Task Url_encodes_parameter_values() {
        var url = ClaudeHookCommand.BuildMemoryIndexUrl("http://srv", "a b", "m/1");
        await Assert.That(url).Contains("repo=a%20b");
        await Assert.That(url).Contains("machine=m%2F1");
    }
}

public class MemoryIndexEmitterTests {
    static JsonArray Index(params (string slug, string audience, string description)[] items) {
        var arr = new JsonArray();
        foreach (var (slug, audience, description) in items) {
            arr.Add(new JsonObject {
                ["memory_id"]   = $"id-{slug}",
                ["slug"]        = slug,
                ["audience"]    = audience,
                ["description"] = description,
                ["kind"]        = "feedback"
            });
        }
        return arr;
    }

    [Test]
    public async Task Groups_by_audience_with_headers_and_instruction() {
        var index = Index(
            ("org-rule",  "org",  "org fact"),
            ("team-rule", "team", "team fact"),
            ("my-rule",   "user", "my fact")
        );

        var fragment = MemoryIndexEmitter.BuildFragment(index, disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("## Team memory");
        await Assert.That(fragment!).Contains("get_memory");
        await Assert.That(fragment!).Contains("search_memories");
        await Assert.That(fragment!).Contains("### Org");
        await Assert.That(fragment!).Contains("- org-rule: org fact");
        await Assert.That(fragment!).Contains("### Team");
        await Assert.That(fragment!).Contains("- team-rule: team fact");
        await Assert.That(fragment!).Contains("### Yours");
        await Assert.That(fragment!).Contains("- my-rule: my fact");
    }

    [Test]
    public async Task Groups_render_in_org_then_team_then_user_order_regardless_of_input_order() {
        // Deliberately feed user → team → org; output must still be Org, Team, Yours.
        var index = Index(
            ("my-rule",   "user", "my fact"),
            ("team-rule", "team", "team fact"),
            ("org-rule",  "org",  "org fact")
        );

        var fragment = MemoryIndexEmitter.BuildFragment(index, disabled: false)!;

        var org  = fragment.IndexOf("### Org", StringComparison.Ordinal);
        var team = fragment.IndexOf("### Team", StringComparison.Ordinal);
        var user = fragment.IndexOf("### Yours", StringComparison.Ordinal);

        await Assert.That(org).IsGreaterThanOrEqualTo(0);
        await Assert.That(org).IsLessThan(team);
        await Assert.That(team).IsLessThan(user);
    }

    [Test]
    public async Task Preserves_server_order_within_a_group() {
        // Server returns most-recently-updated first; the emitter must not reorder within a bucket.
        var index = Index(
            ("newest", "org", "a"),
            ("middle", "org", "b"),
            ("oldest", "org", "c")
        );

        var fragment = MemoryIndexEmitter.BuildFragment(index, disabled: false)!;

        var newest = fragment.IndexOf("- newest:", StringComparison.Ordinal);
        var middle = fragment.IndexOf("- middle:", StringComparison.Ordinal);
        var oldest = fragment.IndexOf("- oldest:", StringComparison.Ordinal);

        await Assert.That(newest).IsLessThan(middle);
        await Assert.That(middle).IsLessThan(oldest);
    }

    [Test]
    public async Task Renders_only_the_groups_that_have_entries() {
        var fragment = MemoryIndexEmitter.BuildFragment(Index(("my-rule", "user", "my fact")), disabled: false)!;

        await Assert.That(fragment).Contains("### Yours");
        await Assert.That(fragment).DoesNotContain("### Org");
        await Assert.That(fragment).DoesNotContain("### Team");
    }

    [Test]
    public async Task Returns_null_when_disabled() =>
        await Assert.That(MemoryIndexEmitter.BuildFragment(Index(("x", "org", "y")), disabled: true)).IsNull();

    [Test]
    public async Task Returns_null_when_index_is_empty_array() =>
        await Assert.That(MemoryIndexEmitter.BuildFragment(new JsonArray(), disabled: false)).IsNull();

    [Test]
    public async Task Returns_null_when_index_is_null() =>
        await Assert.That(MemoryIndexEmitter.BuildFragment(null, disabled: false)).IsNull();

    [Test]
    public async Task Returns_null_when_index_is_object_not_array() {
        var body = JsonNode.Parse("""{ "slug": "x", "audience": "org", "description": "y" }""");
        await Assert.That(MemoryIndexEmitter.BuildFragment(body, disabled: false)).IsNull();
    }

    [Test]
    public async Task Skips_entries_missing_slug_or_description() {
        var index = new JsonArray(
            new JsonObject { ["slug"] = "good", ["audience"] = "org", ["description"] = "ok" },
            new JsonObject {                     ["audience"] = "org", ["description"] = "no slug" },
            new JsonObject { ["slug"] = "blank", ["audience"] = "org", ["description"] = "   " },
            new JsonObject { ["slug"] = "nodesc",["audience"] = "org" }
        );

        var fragment = MemoryIndexEmitter.BuildFragment(index, disabled: false)!;

        await Assert.That(fragment).Contains("- good: ok");
        await Assert.That(fragment.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Skips_entries_with_unknown_or_missing_audience() {
        // Denial branch: only the three known buckets render. An unknown audience must be
        // dropped, not grouped under a made-up heading — and if ALL entries are unknown the
        // whole block collapses to null.
        var index = new JsonArray(
            new JsonObject { ["slug"] = "weird", ["audience"] = "everyone", ["description"] = "d" },
            new JsonObject { ["slug"] = "none",                            ["description"] = "d" }
        );

        await Assert.That(MemoryIndexEmitter.BuildFragment(index, disabled: false)).IsNull();
    }

    [Test]
    public async Task Collapses_newlines_in_description_to_keep_one_line_per_memory() {
        // The server validates descriptions single-line, but the CLI must not depend on that:
        // a stray newline would otherwise split one memory across bullets and distort grouping.
        var index = new JsonArray(
            new JsonObject { ["slug"] = "multi", ["audience"] = "org", ["description"] = "line one\nline two\r\n\tline three" }
        );

        var fragment = MemoryIndexEmitter.BuildFragment(index, disabled: false)!;

        await Assert.That(fragment).Contains("- multi: line one line two line three");
        // Exactly one bullet — the description did not spill onto extra lines.
        await Assert.That(fragment.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Fragment_is_not_a_json_envelope() {
        var fragment = MemoryIndexEmitter.BuildFragment(Index(("x", "org", "y")), disabled: false)!;

        await Assert.That(fragment.TrimStart().StartsWith('{')).IsFalse();
        await Assert.That(fragment).DoesNotContain("hookSpecificOutput");
    }

    [Test]
    [NotInParallel]
    public async Task Typed_emitter_keeps_fragment_size_accounting_linear() {
        var entries = Enumerable.Range(0, SessionStartMemoryConstants.MaxEntries)
            .Select(i => new SessionStartMemoryEntry(
                $"id-{i}", $"slug-{i}", "org", "x", "feedback"))
            .ToArray();

        _ = MemoryIndexEmitter.BuildFragment(entries[..1]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var fragment = MemoryIndexEmitter.BuildFragment(entries);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(fragment).IsNotNull();
        await Assert.That(allocated).IsLessThan(400_000);
    }
}
