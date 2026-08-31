using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Gemini;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Wire-contract test for Gemini subagent import. Gemini records subagents
/// in nested files (<c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>); the parent's
/// <c>invoke_agent</c> tool call persists <c>agentId == subId</c> + <c>args.agent_name</c>.
/// This drives the full discover → classify → import path and asserts the subagent
/// lifecycle the server expects: <c>/hooks/subagent-start</c> and <c>/hooks/subagent-stop</c>
/// carry the CANONICAL (dashless) <c>agent_id</c> and the <c>agent_type</c> resolved from
/// the parent <c>agent_name</c>, and the subagent transcript batch is tagged
/// <c>vendor=gemini</c> with that <c>agent_id</c> so the server routes it to
/// <c>AgentSubsession-*</c>.
/// </summary>
public class GeminiSubagentImportTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public GeminiSubagentImportTests() => _tempDir = _tmp.Path;

    const string DashedParent   = "0a900000-0000-4000-8000-000000000903";
    const string DashlessParent = "0a900000000040008000000000000903";
    const string DashedSub      = "57d9b498-2705-4af5-b060-ebaba4878c96";
    const string DashlessSub     = "57d9b49827054af5b060ebaba4878c96";

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    // Writes a parent main session (with an invoke_agent call) + the nested subagent file,
    // and returns the tmp-dir override (the dir that holds per-project subdirectories).
    void WriteFixture() {
        var chats = Path.Combine(_tempDir, "proj", "chats");
        Directory.CreateDirectory(chats);

        File.WriteAllLines(Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl"), new[] {
            $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","lastUpdated":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            """{"id":"u1","timestamp":"2026-06-22T14:31:01.000Z","type":"user","content":[{"text":"delegate it"}]}""",
            $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","tokens":{"input":5,"output":2,"total":7},"model":"gemini-3-flash-preview","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"codebase_investigator","prompt":"list files"},"agentId":"{{DashedSub}}","status":"success"}]}"""
        });

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);
        File.WriteAllLines(Path.Combine(subDir, DashedSub + ".jsonl"), new[] {
            $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","startTime":"2026-06-22T14:31:06.000Z","lastUpdated":"2026-06-22T14:31:06.000Z","kind":"subagent","directories":[]}""",
            """{"id":"s1","timestamp":"2026-06-22T14:31:07.000Z","type":"gemini","content":"calc.py, README.md","tokens":{"input":3,"output":4,"total":7},"model":"gemini-3-flash-preview"}"""
        });
    }

    [Test]
    public async Task ImportSession_imports_nested_subagent_with_canonical_id_and_resolved_type() {
        WriteFixture();

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] {
            "/hooks/session-start/gemini", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/gemini"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new GeminiImportSource(_tempDir);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        // The nested subagent file must NOT be discovered as its own session.
        await Assert.That(discovered.Count).IsEqualTo(1);
        await Assert.That(discovered[0].SessionId).IsEqualTo(DashlessParent);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);

        var result = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        // The subagent DID send real content, so SentChildContent is threaded through as true.
        await Assert.That(result.SentChildContent).IsTrue();

        var posts = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path)
            .ToList();

        // Main lifecycle + the subagent lifecycle + two transcript batches (main + subagent).
        await Assert.That(posts).IsEquivalentTo(new[] {
            "/hooks/session-start/gemini", "/hooks/transcript", "/hooks/subagent-start",
            "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/gemini"
        });
        // subagent-start lands before subagent-stop (fail-closed lifecycle ordering).
        await Assert.That(posts.IndexOf("/hooks/subagent-start"))
            .IsLessThan(posts.IndexOf("/hooks/subagent-stop"));

        // subagent-start carries the canonical (dashless) id + the agent_name-derived type.
        var startBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(startBody).Contains($"\"agent_id\":\"{DashlessSub}\"");
        await Assert.That(startBody).Contains("\"agent_type\":\"codebase_investigator\"");

        // The subagent transcript batch is tagged vendor=gemini and carries the agent_id.
        var subTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!)
            .Single(b => b.Contains(DashlessSub));
        await Assert.That(subTranscript).Contains("\"vendor\":\"gemini\"");

        var stopBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-stop").RequestMessage.Body!;
        await Assert.That(stopBody).Contains($"\"agent_id\":\"{DashlessSub}\"");
    }

    // --- Gemini's SentChildContent signal, partial-plumbing with a documented residual (no
    // per-child watermark — see ImportSubagentsAsync's doc comment). ---

    [Test]
    public async Task ImportSession_AlreadyLoaded_without_a_subagent_reports_no_sent_child_content() {
        // A subagent-free session's AlreadyLoaded replay never touches a child/subagent stream
        // at all — SentChildContent correctly reports false.
        var chats = Path.Combine(_tempDir, "proj", "chats");
        Directory.CreateDirectory(chats);
        File.WriteAllLines(Path.Combine(chats, "session-2026-06-22T14-31-0a900001.jsonl"), new[] {
            $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","lastUpdated":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            """{"id":"u1","timestamp":"2026-06-22T14:31:01.000Z","type":"user","content":[{"text":"hi"}]}""",
            """{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"hello"}"""
        });

        // High watermark → AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] { "/hooks/session-start/gemini", "/hooks/session-end/gemini" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new GeminiImportSource(_tempDir);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsFalse();

        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_with_a_subagent_reports_sent_child_content_true() {
        // Documented residual (D1): ImportSubagentsAsync has no per-child watermark, so it
        // resends the whole subagent transcript on every re-run — an AlreadyLoaded parent whose
        // subagent is still present reports SentChildContent=true, whether or not the subagent
        // content is actually new. That's the accepted, conservative trade-off: this reads true
        // on essentially every re-run of a subagent-bearing session, closing the common
        // subagent-free case for free while leaving the subagent-bearing case genuinely
        // uncounted-as-suppressed (never proven no-work) rather than risking a silently dropped
        // real subagent update.
        WriteFixture();

        // High watermark for BOTH the parent and the subagent → AlreadyLoaded, but
        // ImportSubagentsAsync always resends the subagent transcript regardless.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] {
            "/hooks/session-start/gemini", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/gemini"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new GeminiImportSource(_tempDir);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsTrue();

        // No override needed for Gemini — Resumed never engages IsSkippedChildContentOverride
        // (it requires outcome == Skipped exactly), so this is NOT suppressed: it resolves via
        // the ordinary outcome path.
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsEqualTo(ImportOutcome.Resumed);
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_with_a_subagent_whose_transcript_post_is_rejected_reports_no_sent_child_content() {
        // Regression for a Qodo-flagged false positive: SendTranscriptBatches returns a
        // positive line count even when the server REJECTS the batch (failOnError defaults
        // false), so without failOnError:true here SentChildContent would flip true for a
        // batch the server never accepted. Mirrors
        // ImportSession_AlreadyLoaded_with_a_subagent_reports_sent_child_content_true, except
        // the subagent's /hooks/transcript POST now returns 500.
        WriteFixture();

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] { "/hooks/session-start/gemini", "/hooks/subagent-start", "/hooks/session-end/gemini" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }
        // The server rejects the subagent's content batch (the parent, AlreadyLoaded, never
        // reaches /hooks/transcript at all — its startLine already sits at TotalLines).
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var client = new HttpClient();
        var source = new GeminiImportSource(_tempDir);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        // The rejected batch must NOT be reported as having sent child content — the exact
        // bug under test.
        await Assert.That(result.SentChildContent).IsFalse();

        // The walk continues non-fatally past the rejected batch (caught by the surrounding
        // try/catch): subagent-start was posted, but subagent-stop is left unsent (a
        // re-import retries), and the parent's own lifecycle still completes.
        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(posts.Contains("/hooks/subagent-start")).IsTrue();
        await Assert.That(posts.Contains("/hooks/subagent-stop")).IsFalse();
        await Assert.That(posts.Contains("/hooks/session-start/gemini")).IsTrue();
        await Assert.That(posts.Contains("/hooks/session-end/gemini")).IsTrue();

        // A routed AlreadyLoaded replay with a false signal is SUPPRESSED for counting, not
        // counted Loaded — the false positive Qodo flagged.
        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();

        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsNull();
    }

    // --- D3: recursive grandchild/descendant discovery. ---

    const string DashedGrandsub   = "8c1a2222-3333-4444-5555-666677778888";
    const string DashlessGrandsub = "8c1a2222333344445555666677778888";

    // Root (invoke_agent → Sub) + Sub's OWN chats/<sub>/ dir with a grandsub file whose
    // invocation is recorded in SUB's own transcript (a DIFFERENT agent_name than the root's
    // call) — matches EnumerateDescendantFiles' context-carrying walk (D3).
    void WriteThreeLevelFixture() {
        var chats = Path.Combine(_tempDir, "proj", "chats");
        Directory.CreateDirectory(chats);

        File.WriteAllLines(Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl"), new[] {
            $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","lastUpdated":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            """{"id":"u1","timestamp":"2026-06-22T14:31:01.000Z","type":"user","content":[{"text":"delegate it"}]}""",
            $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","tokens":{"input":5,"output":2,"total":7},"model":"gemini-3-flash-preview","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"codebase_investigator","prompt":"list files"},"agentId":"{{DashedSub}}","status":"success"}]}"""
        });

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);
        File.WriteAllLines(Path.Combine(subDir, DashedSub + ".jsonl"), new[] {
            $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","startTime":"2026-06-22T14:31:06.000Z","lastUpdated":"2026-06-22T14:31:06.000Z","kind":"subagent","directories":[]}""",
            """{"id":"s1","timestamp":"2026-06-22T14:31:07.000Z","type":"user","content":[{"text":"go deeper"}]}""",
            // Sub's OWN invoke_agent call, with a DISTINCT agent_name from the root's call.
            $$"""{"id":"s2","timestamp":"2026-06-22T14:31:08.000Z","type":"gemini","content":"","tokens":{"input":3,"output":2,"total":5},"model":"gemini-3-flash-preview","toolCalls":[{"id":"invoke_agent__y","name":"invoke_agent","args":{"agent_name":"test_runner","prompt":"run tests"},"agentId":"{{DashedGrandsub}}","status":"success"}]}"""
        });

        // Grandsub's dir is rooted directly under chats/, keyed by Sub's own id — never nested
        // under Sub's own directory (which lives at chats/<parent>/<sub>.jsonl, a FILE not a
        // dir). Matches "child directories always derived from the ROOT chats/ dir".
        var grandsubDir = Path.Combine(chats, DashedSub);
        Directory.CreateDirectory(grandsubDir);
        File.WriteAllLines(Path.Combine(grandsubDir, DashedGrandsub + ".jsonl"), new[] {
            $$"""{"sessionId":"{{DashedGrandsub}}","projectHash":"h","startTime":"2026-06-22T14:31:09.000Z","lastUpdated":"2026-06-22T14:31:09.000Z","kind":"subagent","directories":[]}""",
            """{"id":"g1","timestamp":"2026-06-22T14:31:10.000Z","type":"gemini","content":"all green","tokens":{"input":2,"output":3,"total":5},"model":"gemini-3-flash-preview"}"""
        });
    }

    [Test]
    public async Task ImportSession_imports_a_grandchild_subagent_as_direct_subagent_of_root_with_correct_type() {
        // a subagent's own subagent (chats/<sub>/<grandsub>.jsonl) must import —
        // previously silently dropped by the single-level EnumerateSubagentFiles scan — as a
        // DIRECT subagent of the top-level root, with its type resolved from its IMMEDIATE
        // parent (Sub's own transcript), not the root's, and not falling back to "subagent".
        WriteThreeLevelFixture();

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] {
            "/hooks/session-start/gemini", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/gemini"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new GeminiImportSource(_tempDir);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1); // neither nested file is its own top-level session

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);

        var result = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        // Both descendants (Sub + Grandsub) sent real content, aggregated up through the
        // recursive walk in ImportSubagentsAsync.
        await Assert.That(result.SentChildContent).IsTrue();

        var starts = _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/subagent-start")
            .Select(e => e.RequestMessage.Body!).ToList();
        var stops = _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/subagent-stop")
            .Select(e => e.RequestMessage.Body!).ToList();

        // Both descendants got their own subagent lifecycle, each carrying the ROOT parent
        // session id (flattened as direct subagents of the top-level root).
        await Assert.That(starts.Count).IsEqualTo(2);
        await Assert.That(stops.Count).IsEqualTo(2);
        await Assert.That(starts.All(b => b.Contains($"\"session_id\":\"{DashlessParent}\""))).IsTrue();

        // Sub resolves from the ROOT's invoke_agent call.
        await Assert.That(starts.Any(b =>
            b.Contains($"\"agent_id\":\"{DashlessSub}\"") && b.Contains("\"agent_type\":\"codebase_investigator\""))).IsTrue();

        // Grandsub resolves from SUB's OWN invoke_agent call — a DISTINCT agent_name from the
        // root's — not the "subagent" fallback a flat, root-transcript-only lookup would give.
        await Assert.That(starts.Any(b =>
            b.Contains($"\"agent_id\":\"{DashlessGrandsub}\"") && b.Contains("\"agent_type\":\"test_runner\""))).IsTrue();

        // Final session-end lands after both descendants' lifecycles are posted.
        var paths = _server.LogEntries.OrderBy(e => e.RequestMessage.DateTime).Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(paths.LastIndexOf("/hooks/subagent-stop")).IsLessThan(paths.IndexOf("/hooks/session-end/gemini"));
    }
}
