using System.Text.Json.Nodes;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

public class MemoryIndexUrlTests {
    static string Url(string? repoHash, string? machineTag) =>
        SessionStartMemoryContextProvider.BuildUrl("http://srv", new SessionStartMemoryScope(repoHash, machineTag));

    [Test]
    public async Task No_repo_or_machine_still_declares_the_projects_capability() {
        var url = Url(repoHash: null, machineTag: null);
        await Assert.That(url).IsEqualTo("http://srv/api/memories/index?include=projects");
    }

    [Test]
    public async Task Includes_repo_and_machine_when_present() {
        var url = Url("abcd1234", "mach-01");
        await Assert.That(url).IsEqualTo("http://srv/api/memories/index?repo=abcd1234&machine=mach-01&include=projects");
    }

    [Test]
    public async Task Repo_only_omits_machine_param() {
        var url = Url("abcd1234", machineTag: null);
        await Assert.That(url).IsEqualTo("http://srv/api/memories/index?repo=abcd1234&include=projects");
    }

    [Test]
    public async Task Url_encodes_parameter_values() {
        var url = Url("a b", "m/1");
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
    public async Task Annotates_project_and_repo_scope_but_leaves_org_untagged() {
        var index = new JsonArray(
            new JsonObject { ["memory_id"] = "i1", ["slug"] = "org-rule",  ["audience"] = "org", ["description"] = "d", ["kind"] = "feedback", ["scope_kind"] = "org" },
            new JsonObject { ["memory_id"] = "i2", ["slug"] = "repo-rule", ["audience"] = "org", ["description"] = "d", ["kind"] = "feedback", ["scope_kind"] = "repo" },
            new JsonObject { ["memory_id"] = "i3", ["slug"] = "proj-rule", ["audience"] = "org", ["description"] = "d", ["kind"] = "feedback", ["scope_kind"] = "project", ["project_slug"] = "capacitor" }
        );

        var fragment = MemoryIndexEmitter.BuildFragment(index, disabled: false)!;

        await Assert.That(fragment).Contains("- org-rule: d");                        // org home ⇒ untagged
        await Assert.That(fragment).Contains("- repo-rule [repo]: d");
        await Assert.That(fragment).Contains("- proj-rule [project: capacitor]: d");  // resolved slug, not the id
    }

    [Test]
    public async Task Missing_scope_renders_untagged_for_an_older_server() {
        // A server that predates scope on the index sends no scope_kind — the line renders exactly as before.
        var fragment = MemoryIndexEmitter.BuildFragment(Index(("legacy", "org", "d")), disabled: false)!;
        await Assert.That(fragment).Contains("- legacy: d");
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
        // The server ranks entries; the emitter must not reorder within a bucket.
        var index = Index(
            ("first", "org", "a"),
            ("second", "org", "b"),
            ("third", "org", "c")
        );

        var fragment = MemoryIndexEmitter.BuildFragment(index, disabled: false)!;

        var first = fragment.IndexOf("- first:", StringComparison.Ordinal);
        var second = fragment.IndexOf("- second:", StringComparison.Ordinal);
        var third = fragment.IndexOf("- third:", StringComparison.Ordinal);

        await Assert.That(first).IsLessThan(second);
        await Assert.That(second).IsLessThan(third);
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
    public async Task Lead_in_names_the_repos_project_above_the_memory_list() {
        var entries = new[] { new SessionStartMemoryEntry("id", "a-rule", "org", "d", "feedback") };

        var fragment = MemoryIndexEmitter.BuildFragment(
            entries, [new SessionStartMemoryProject("capacitor", "Kurrent Capacitor")])!;

        await Assert.That(fragment).Contains(
            "This repo belongs to project \"capacitor\" (Kurrent Capacitor). " +
            "Save learnings that span its repos with project: \"capacitor\".");
        await Assert.That(fragment.IndexOf("This repo belongs", StringComparison.Ordinal))
            .IsLessThan(fragment.IndexOf("## Team memory", StringComparison.Ordinal));
        await Assert.That(fragment.StartsWith(MemoryIndexEmitter.FragmentMarker, StringComparison.Ordinal)).IsTrue();
        await Assert.That(fragment).Contains("- a-rule: d");
    }

    [Test]
    public async Task Lead_in_alone_is_a_fragment_when_the_index_is_empty() {
        // A project with no memories yet is exactly when the agent needs the slug it can save to,
        // so projects alone must produce a fragment where entries alone would produce none.
        var fragment = MemoryIndexEmitter.BuildFragment([], [new SessionStartMemoryProject("capacitor", "Kurrent Capacitor")]);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!.StartsWith(MemoryIndexEmitter.FragmentMarker, StringComparison.Ordinal)).IsTrue();
        await Assert.That(fragment).Contains("This repo belongs to project \"capacitor\"");
        await Assert.That(fragment).DoesNotContain("## Team memory");
    }

    [Test]
    public async Task No_projects_renders_exactly_what_an_older_server_produced() {
        var entries = new[] { new SessionStartMemoryEntry("id", "a-rule", "org", "d", "feedback") };

        var withNone = MemoryIndexEmitter.BuildFragment(entries, []);
        var withNull = MemoryIndexEmitter.BuildFragment(entries);

        await Assert.That(withNone).IsEqualTo(withNull);
        await Assert.That(withNone!).DoesNotContain("This repo belongs");
        await Assert.That(withNone!).StartsWith(MemoryIndexEmitter.FragmentMarker + "\n## Team memory");
    }

    [Test]
    public async Task Several_projects_get_a_line_each() {
        var fragment = MemoryIndexEmitter.BuildFragment([], [
            new SessionStartMemoryProject("capacitor", "Kurrent Capacitor"),
            new SessionStartMemoryProject("platform", "Platform")
        ])!;

        await Assert.That(fragment).Contains("This repo belongs to project \"capacitor\" (Kurrent Capacitor).");
        await Assert.That(fragment).Contains("This repo belongs to project \"platform\" (Platform).");
        await Assert.That(fragment.Split('\n').Count(l => l.StartsWith("This repo belongs", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task Project_name_is_dropped_when_it_adds_nothing_to_the_slug() {
        var same    = MemoryIndexEmitter.BuildFragment([], [new SessionStartMemoryProject("capacitor", "capacitor")])!;
        var missing = MemoryIndexEmitter.BuildFragment([], [new SessionStartMemoryProject("capacitor", null)])!;

        await Assert.That(same).Contains("This repo belongs to project \"capacitor\". Save learnings");
        await Assert.That(missing).Contains("This repo belongs to project \"capacitor\". Save learnings");
    }

    [Test]
    public async Task Projects_without_a_usable_slug_are_skipped() {
        var fragment = MemoryIndexEmitter.BuildFragment([], [
            new SessionStartMemoryProject(null, "No Slug"),
            new SessionStartMemoryProject("   ", "Blank"),
            new SessionStartMemoryProject("real", "Real")
        ])!;

        await Assert.That(fragment).Contains("project \"real\"");
        await Assert.That(fragment.Split('\n').Count(l => l.StartsWith("This repo belongs", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task Only_slugless_projects_and_no_entries_emit_nothing() =>
        await Assert.That(MemoryIndexEmitter.BuildFragment([], [new SessionStartMemoryProject(" ", "Blank")])).IsNull();

    [Test]
    public async Task Lead_in_collapses_newlines_to_keep_one_line_per_project() {
        var fragment = MemoryIndexEmitter.BuildFragment([], [new SessionStartMemoryProject("cap", "One\nTwo\r\nThree")])!;

        await Assert.That(fragment).Contains("project \"cap\" (One Two Three).");
        await Assert.That(fragment.Split('\n').Count(l => l.StartsWith("This repo belongs", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task Lead_in_stops_at_the_project_cap() {
        var many = Enumerable.Range(0, SessionStartMemoryConstants.MaxProjects + 5)
            .Select(i => new SessionStartMemoryProject($"p{i}", $"P{i}"))
            .ToArray();

        var fragment = MemoryIndexEmitter.BuildFragment([], many)!;

        await Assert.That(fragment.Split('\n').Count(l => l.StartsWith("This repo belongs", StringComparison.Ordinal)))
            .IsEqualTo(SessionStartMemoryConstants.MaxProjects);
    }

    [Test]
    public async Task Lead_in_counts_against_the_fragment_budget() {
        // The lead-in sits in the prefix the group appender measures from, so a memory list that
        // overruns the budget must lose entries to it rather than push the fragment past the cap.
        // A full cap of long-named projects is far wider than one entry line, so the displacement
        // does not turn on how much slack the truncated list happened to leave.
        var entries = Enumerable.Range(0, SessionStartMemoryConstants.MaxEntries)
            .Select(i => new SessionStartMemoryEntry($"id-{i}", $"slug-{i}", "org", new string('d', 300), "feedback"))
            .ToArray();
        var projects = Enumerable.Range(0, SessionStartMemoryConstants.MaxProjects)
            .Select(i => new SessionStartMemoryProject($"project-{i}", new string('n', 100)))
            .ToArray();

        var without = MemoryIndexEmitter.BuildFragment(entries)!;
        var with    = MemoryIndexEmitter.BuildFragment(entries, projects)!;

        static int Bullets(string fragment) => fragment.Split('\n').Count(l => l.StartsWith("- slug-", StringComparison.Ordinal));
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(without))
            .IsLessThanOrEqualTo(SessionStartMemoryConstants.MaxFragmentBytes);
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(with))
            .IsLessThanOrEqualTo(SessionStartMemoryConstants.MaxFragmentBytes);
        await Assert.That(Bullets(without) - Bullets(with)).IsGreaterThanOrEqualTo(5);
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
