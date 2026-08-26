using System.Net;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// Task F1/F2 (D5, CLI side): the subagent correlator's INPUT is widened to
/// same-workspace discovery (ignoring --session/--cwd/--since/scope), so a filtered/scoped
/// import still stamps a correlated child even when its parent falls outside this run's slice —
/// and such an orphaned child must import STANDALONE rather than being silently dropped.
/// Models the parent/child fixture in <see cref="CursorImportSourceTests"/>.
/// </summary>
public class CursorOrphanedChildStandaloneTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ParentId = "11111111111111111111111111111111";
    const string ChildId  = "22222222222222222222222222222222";

    // Same shape as CursorImportSourceTests.SetupParentChildAsync: a parent session that Tasks a
    // prompt, and a child session whose first user_query is byte-identical to that prompt.
    static void WriteParentAndChild(ProjectsDirFixture fx) {
        const string prompt = "EXPLORE the repo and report back";
        var childUserText = JsonSerializer.Serialize("<user_query>\n" + prompt + "\n</user_query>");
        var taskPrompt    = JsonSerializer.Serialize(prompt);

        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"go\"}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Task\",\"input\":{\"description\":\"d\",\"prompt\":" + taskPrompt + ",\"subagent_type\":\"generalPurpose\"}}]}}\n");
        fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":" + childUserText + "}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n");
    }

    [Test]
    public async Task classify_correlates_child_to_parent_even_when_parent_is_outside_the_filtered_slice() {
        // Task F1: `--session <child>` narrows DiscoverAsync's own output to just the child, but
        // ClassifyAsync must still widen its correlator input to the whole same-workspace
        // agent-transcripts/ dir (ignoring the filter) so it can see the parent's Task prompt.
        using var fx = new ProjectsDirFixture();
        WriteParentAndChild(fx);

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var discovered = await src.DiscoverAsync(
            Filters(filterSession: ChildId),
            CancellationToken.None);

        // Only the child is part of THIS run's slice — the parent is excluded by --session.
        await Assert.That(discovered.Count).IsEqualTo(1);
        await Assert.That(discovered[0].SessionId).IsEqualTo(ChildId);

        var classified = await src.ClassifyAsync(discovered, Ctx(client), CancellationToken.None);

        await Assert.That(classified.Count).IsEqualTo(1);
        var childClass = classified[0];
        await Assert.That(childClass.SourceMeta!.ContainsKey("IsSubagentChild")).IsTrue();
        await Assert.That((bool)childClass.SourceMeta!["IsSubagentChild"]!).IsTrue();
        await Assert.That(childClass.SourceMeta!.ContainsKey("ParentSessionId")).IsTrue();
        await Assert.That((string)childClass.SourceMeta!["ParentSessionId"]!).IsEqualTo(ParentId);
    }

    [Test]
    public async Task classify_does_not_correlate_when_parent_and_child_are_in_different_workspaces() {
        // Guard against over-widening: the same-workspace scan must not reach into an unrelated
        // sanitized directory that the filtered slice never touched.
        using var fx = new ProjectsDirFixture();
        const string prompt = "EXPLORE the repo and report back";
        var childUserText = JsonSerializer.Serialize("<user_query>\n" + prompt + "\n</user_query>");
        var taskPrompt    = JsonSerializer.Serialize(prompt);

        fx.AddSession("Users-me-other-proj", "11111111-1111-1111-1111-111111111111",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"go\"}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Task\",\"input\":{\"description\":\"d\",\"prompt\":" + taskPrompt + ",\"subagent_type\":\"generalPurpose\"}}]}}\n");
        fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":" + childUserText + "}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var discovered = await src.DiscoverAsync(Filters(filterSession: ChildId), CancellationToken.None);
        var classified = await src.ClassifyAsync(discovered, Ctx(client), CancellationToken.None);

        await Assert.That(classified.Count).IsEqualTo(1);
        await Assert.That(classified[0].SourceMeta!.ContainsKey("IsSubagentChild")).IsFalse();
    }

    [Test]
    public async Task orphaned_child_imports_standalone_when_parent_is_not_in_the_routed_plan() {
        // Task F2: once F1 has stamped the child as a subagent child whose parent is absent from
        // `routed` (simulating a `--session <child>` run), ImportCommand's reconciliation must
        // clear the flags so the child's own ImportSessionAsync call runs the ordinary standalone
        // lifecycle — never Skipped/dropped, and never a subagent-start/-stop against the parent.
        using var fx = new ProjectsDirFixture();
        WriteParentAndChild(fx);

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var getHandler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var getClient  = new HttpClient(getHandler);

        var discovered = await src.DiscoverAsync(Filters(filterSession: ChildId), CancellationToken.None);
        var classified = await src.ClassifyAsync(discovered, Ctx(getClient), CancellationToken.None);
        var childClass = classified.Single(c => c.SessionId == ChildId);
        await Assert.That(childClass.SourceMeta!.ContainsKey("IsSubagentChild")).IsTrue(); // sanity (F1)

        // The parent never got classified in this run (excluded by --session), so `routed`
        // contains only the child.
        var routed = new List<ImportCommand.SessionClassification> { childClass };

        var reconciled     = ImportCommand.ReconcileOrphanedCursorSubagentChildren(routed);
        var reconciledChild = reconciled.Single();

        await Assert.That(reconciledChild.SourceMeta!.ContainsKey("IsSubagentChild")).IsFalse();
        await Assert.That(reconciledChild.SourceMeta!.ContainsKey("ParentSessionId")).IsFalse();

        var posted = new List<string>();
        using var postHandler = new StubHandler(
            postCapture: (req, _) => { posted.Add(req.RequestUri!.AbsolutePath); return new HttpResponseMessage(HttpStatusCode.OK); });
        using var postClient  = new HttpClient(postHandler);

        var outcome = await src.ImportSessionAsync(
            reconciledChild,
            new ImportContext(postClient, "http://localhost", ForcePrivate: false),
            CancellationToken.None);

        // Standalone lifecycle — not Skipped, not dropped.
        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(posted).Contains("/hooks/session-start/cursor");
        await Assert.That(posted).Contains("/hooks/transcript");
        await Assert.That(posted).Contains("/hooks/session-end/cursor");

        // Never triggers subagent lifecycle against the un-planned parent.
        await Assert.That(posted).DoesNotContain("/hooks/subagent-start");
        await Assert.That(posted).DoesNotContain("/hooks/subagent-stop");
    }

    [Test]
    public async Task reconciliation_prunes_a_child_excluded_from_the_routed_plan_off_the_parents_subagent_children() {
        // the reverse direction of F1/F2. `--session <parent>` puts the
        // PARENT in this run's routed plan but never classifies the child (excluded by the
        // --session filter) — yet ClassifyAsync's widened same-workspace scan still correlates and
        // stamps SubagentChildren=[child] onto the parent. Attaching that child in
        // ImportSessionAsync would WIDEN the plan (contrary to D5) and ship a session the user
        // explicitly filtered out. Reconciliation must prune it: the parent imports without ever
        // sending the excluded child's subagent-start/-stop or transcript.
        using var fx = new ProjectsDirFixture();
        WriteParentAndChild(fx);

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var getHandler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var getClient  = new HttpClient(getHandler);

        // Only the PARENT is part of this run's slice — the child is excluded by --session.
        var discovered = await src.DiscoverAsync(Filters(filterSession: ParentId), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);
        await Assert.That(discovered[0].SessionId).IsEqualTo(ParentId);

        var classified = await src.ClassifyAsync(discovered, Ctx(getClient), CancellationToken.None);
        var parentClass = classified.Single(c => c.SessionId == ParentId);

        // Sanity: the widened same-workspace scan still correlated the child onto the parent
        // (this is F1's widening, working exactly as intended for link DISCOVERY).
        await Assert.That(parentClass.SourceMeta!.ContainsKey("SubagentChildren")).IsTrue();

        // This run's routed plan contains only the parent — the child was never classified.
        var routed = new List<ImportCommand.SessionClassification> { parentClass };

        var reconciled     = ImportCommand.ReconcileOrphanedCursorSubagentChildren(routed);
        var reconciledParent = reconciled.Single();

        // The out-of-plan child must be pruned off — never attached for import here.
        await Assert.That(reconciledParent.SourceMeta!.ContainsKey("SubagentChildren")).IsFalse();

        var posted = new List<string>();
        using var postHandler = new StubHandler(
            postCapture: (req, _) => { posted.Add(req.RequestUri!.AbsolutePath); return new HttpResponseMessage(HttpStatusCode.OK); });
        using var postClient  = new HttpClient(postHandler);

        var outcome = await src.ImportSessionAsync(
            reconciledParent,
            new ImportContext(postClient, "http://localhost", ForcePrivate: false),
            CancellationToken.None);

        // The parent still imports normally...
        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(posted).Contains("/hooks/session-start/cursor");
        await Assert.That(posted).Contains("/hooks/transcript");
        await Assert.That(posted).Contains("/hooks/session-end/cursor");

        // ...but never sends the excluded child's subagent lifecycle or transcript.
        await Assert.That(posted).DoesNotContain("/hooks/subagent-start");
        await Assert.That(posted).DoesNotContain("/hooks/subagent-stop");
    }

    [Test]
    public async Task reconciliation_leaves_nested_child_untouched_when_parent_is_in_the_routed_plan() {
        // Regression: the common case (both parent and child are part of this run) must keep
        // importing nested — reconciliation is a no-op when the parent IS in `routed`.
        using var fx = new ProjectsDirFixture();
        WriteParentAndChild(fx);

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var getHandler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var getClient  = new HttpClient(getHandler);

        var discovered = await src.DiscoverAsync(Filters(), CancellationToken.None); // no --session filter
        var classified = await src.ClassifyAsync(discovered, Ctx(getClient), CancellationToken.None);

        var routed = classified.ToList();
        var reconciled = ImportCommand.ReconcileOrphanedCursorSubagentChildren(routed);

        var childClass = reconciled.Single(c => c.SessionId == ChildId);
        await Assert.That(childClass.SourceMeta!.ContainsKey("IsSubagentChild")).IsTrue();
        await Assert.That((string)childClass.SourceMeta!["ParentSessionId"]!).IsEqualTo(ParentId);

        var parentClass = reconciled.Single(c => c.SessionId == ParentId);
        await Assert.That(parentClass.SourceMeta!.ContainsKey("SubagentChildren")).IsTrue();
    }

    static DiscoveryFilters Filters(string? filterCwd = null, string? filterSession = null, DateOnly? since = null, int minLines = 0) =>
        new(FilterCwd: filterCwd, FilterSession: filterSession, Since: since, MinLines: minLines);

    static ClassifyContext Ctx(HttpClient http, int minLines = 0) =>
        new(http, "http://localhost", minLines, ExcludedRepos: null, ExcludedPaths: null);

    sealed class ProjectsDirFixture : IDisposable {
        public string Root                { get; }
        public string ProjectsDir         => Path.Combine(Root, ".cursor", "projects");
        public string WorkspaceStorageDir => Path.Combine(Root, "workspaceStorage");

        public ProjectsDirFixture() {
            Root = Path.Combine(Path.GetTempPath(), $"kcap-cursor-orphan-{Guid.NewGuid():N}");
            Directory.CreateDirectory(ProjectsDir);
            Directory.CreateDirectory(WorkspaceStorageDir);
        }

        public string AddSession(string sanitized, string sessionId, string jsonlContent) {
            var dir = Path.Combine(ProjectsDir, sanitized, "agent-transcripts", sessionId);
            Directory.CreateDirectory(dir);
            var jsonl = Path.Combine(dir, sessionId + ".jsonl");
            File.WriteAllText(jsonl, jsonlContent);

            return jsonl;
        }

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    sealed class StubHandler(
            Func<HttpRequestMessage, HttpResponseMessage>?         getResponse = null,
            Func<HttpRequestMessage, string, HttpResponseMessage>? postCapture = null
        )
        : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Get)
                return getResponse?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            return postCapture?.Invoke(request, body) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
