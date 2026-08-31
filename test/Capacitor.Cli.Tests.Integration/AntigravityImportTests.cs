using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Antigravity;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Wire-contract test for Antigravity historical import. Drives the full
/// discover → classify → import path over an on-disk brain tree (a root conversation plus
/// a subagent conversation linked via an INVOKE_SUBAGENT step in the parent's
/// transcript_full.jsonl — the spawn-time signal, see
/// <c>AntigravitySubagents.BuildParentMap</c>) and asserts the lifecycle the server expects:
/// session-start/antigravity → parent transcript → subagent-start → child transcript (routed
/// by agent_id) → subagent-stop → session-end/antigravity, all tagged vendor=antigravity.
/// </summary>
public class AntigravityImportTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _home;

    public AntigravityImportTests() => _home = _tmp.Path;

    const string Root  = "11110000-0000-4000-8000-000000000001";
    const string Child = "22220000-0000-4000-8000-000000000002";

    // The brain-dir conversation id is dashed on disk, but the id surfaced to the server
    // (session id + subagent agent_id) is the dashless canonical form — matching live capture
    // so live + imported captures of one conversation land on the same stream.
    static string Dashless(string id) => Guid.Parse(id).ToString("N");

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string BrainDir(string productSub, string convId) =>
        Path.Combine(_home, ".gemini", productSub, "brain", convId);

    void WriteTranscript(string convId, string firstUserText) => WriteTranscript("antigravity", convId, firstUserText);

    void WriteTranscript(string productSub, string convId, string firstUserText) {
        var dir = Path.Combine(BrainDir(productSub, convId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-07-02T19:00:00Z","content":"<USER_REQUEST>{{firstUserText}}</USER_REQUEST>"}""",
            """{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-07-02T19:00:05Z","content":"done"}"""
        });
    }

    // Appends an INVOKE_SUBAGENT step to Root's transcript naming Child as the spawned
    // conversation — the spawn-time linkage BuildParentMap reads (messages/*.json is
    // no longer consulted). Root's transcript must already exist (WriteTranscript(Root, ...)
    // is called before this at every call site).
    void WriteLinkage() => WriteLinkage("antigravity");

    void WriteLinkage(string productSub) {
        var dir = Path.Combine(BrainDir(productSub, Root), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"{{Child}}\"}"}"""
        });
    }

    [Test]
    public async Task ImportSession_imports_root_with_nested_subagent() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(new(new(_home), ""));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        // The subagent conversation must NOT be discovered as its own top-level session.
        await Assert.That(discovered.Count).IsEqualTo(1);
        await Assert.That(discovered[0].SessionId).IsEqualTo(Dashless(Root));

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var posts = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path)
            .ToList();

        await Assert.That(posts).IsEquivalentTo(new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript", "/hooks/subagent-start",
            "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        });
        // Fail-closed ordering: subagent registered before its content, stopped after.
        await Assert.That(posts.IndexOf("/hooks/subagent-start")).IsLessThan(posts.IndexOf("/hooks/subagent-stop"));

        // The session lifecycle carries the dashless session id (matching live capture).
        var sessionStartBody = _server.LogEntries
            .Single(e => e.RequestMessage.Path == "/hooks/session-start/antigravity").RequestMessage.Body!;
        await Assert.That(sessionStartBody).Contains($"\"session_id\":\"{Dashless(Root)}\"");

        // subagent-start carries the DASHLESS child conversation id as agent_id (server
        // canonicalizes agent_id to dashless on both ingest and watermark read, so this matches
        // live routing/correlation — mirrors GeminiImportSource).
        var startBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(startBody).Contains($"\"agent_id\":\"{Dashless(Child)}\"");

        // The child transcript batch is tagged vendor=antigravity and routed by the dashless agent_id.
        // Matched via "agent_id":"<Dashless(Child)>" rather than a bare Contains(Dashless(Child)),
        // because the root's own transcript batch also contains the Child id embedded in its
        // INVOKE_SUBAGENT step.
        var subTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!)
            .Single(b => b.Contains($"\"agent_id\":\"{Dashless(Child)}\""));
        await Assert.That(subTranscript).Contains("\"vendor\":\"antigravity\"");
    }

    // G1 end-to-end: the SAME nested-subagent import driven over the agy CLI root
    // (~/.gemini/antigravity-cli/brain). Exercises the one path the unit tests can't drive to the
    // server — ImportChildrenAsync resolving the child transcript under the session's product root
    // rather than defaulting to the GUI's. Before dual-root discovery this imported nothing.
    [Test]
    public async Task ImportSession_over_the_agy_CLI_root_imports_root_with_nested_subagent() {
        WriteTranscript("antigravity-cli", Root, "build it");
        WriteTranscript("antigravity-cli", Child, "sub task");
        WriteLinkage("antigravity-cli");

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(new(new(_home), ""));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);
        await Assert.That(discovered[0].SessionId).IsEqualTo(Dashless(Root));

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var posts = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path)
            .ToList();

        // The full nested lifecycle fired — proving the child transcript resolved under the CLI root.
        await Assert.That(posts).IsEquivalentTo(new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript", "/hooks/subagent-start",
            "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        });
        var subTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!)
            .Single(b => b.Contains($"\"agent_id\":\"{Dashless(Child)}\""));
        await Assert.That(subTranscript).Contains("\"vendor\":\"antigravity\"");
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_root_repairs_a_missing_child() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        // Parent (no agentId) fully ingested → AlreadyLoaded; the child (agentId=dashless Child)
        // was never imported (404). The AlreadyLoaded branch must STILL run the child-repair pass,
        // else a once-skipped subagent is lost forever.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", Dashless(Child)).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(new(new(_home), ""));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        // Outcome stays hardcoded Skipped, but SentChildContent threads through the repair's
        // actual work: this repair attaches brand-new child content (the child was previously
        // missing entirely), so it must report true.
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Skipped);
        await Assert.That(result.SentChildContent).IsTrue();

        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path).ToList();

        // The parent transcript is not re-sent, but lifecycle IS re-asserted (repair — a prior
        // run may have failed session-end) and the missing child is repaired: session-start →
        // subagent-start → child transcript → subagent-stop → session-end.
        await Assert.That(posts.Contains("/hooks/subagent-start")).IsTrue();
        await Assert.That(posts.Contains("/hooks/subagent-stop")).IsTrue();
        await Assert.That(posts.Contains("/hooks/session-start/antigravity")).IsTrue();
        await Assert.That(posts.Contains("/hooks/session-end/antigravity")).IsTrue();

        var childTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!).Single();
        await Assert.That(childTranscript).Contains($"\"agent_id\":\"{Dashless(Child)}\"");
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_root_child_transcript_rejected_reports_no_sent_child_content_and_suppresses() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        // Parent (no agentId) fully ingested → AlreadyLoaded; the child (agentId=dashless Child)
        // was never imported (404), so the repair pass attempts to resend its content — but the
        // server REJECTS that batch (500). Regression for a Qodo-flagged false positive:
        // SentChildContent must reflect only server-ACCEPTED batches (failOnError: true), so a
        // rejected batch must NOT flip the signal to true.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", Dashless(Child)).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }
        // The server rejects the child's content batch.
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var client = new HttpClient();
        var source = new AntigravityImportSource(new(new(_home), ""));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        // Outcome stays hardcoded Skipped, but the rejected batch must NOT be reported as
        // having sent child content — the exact bug under test.
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Skipped);
        await Assert.That(result.SentChildContent).IsFalse();

        // The walk continues non-fatally past the rejected batch (caught by the surrounding
        // try/catch): subagent-start was posted, but subagent-stop is left unsent (a re-import
        // retries), and the parent's own lifecycle (session-start/session-end) still completes.
        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(posts.Contains("/hooks/subagent-start")).IsTrue();
        await Assert.That(posts.Contains("/hooks/subagent-stop")).IsFalse();
        await Assert.That(posts.Contains("/hooks/session-start/antigravity")).IsTrue();
        await Assert.That(posts.Contains("/hooks/session-end/antigravity")).IsTrue();

        // A routed AlreadyLoaded+Skipped replay with a false signal is SUPPRESSED for counting,
        // not reported as Loaded — the false-positive Qodo flagged.
        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();

        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task ImportChildren_fullyIngestedChild_reposts_idempotent_start_and_stop_without_resending_content() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        // Both parent and child are fully ingested (high HWM). The child's CONTENT must not be
        // re-sent, but subagent-start THEN subagent-stop are re-posted (both idempotent): the
        // server's stop no-ops without an active mark, and that mark is lost on a restart, so the
        // start re-establishes it before the stop appends the completion event. Without this a
        // prior run's stop that failed after content leaves the subagent uncompleted forever
        // (see the resume-from-line-position tests below for the exact edge cases).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(new(new(_home), ""));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        // The child's only action here is the lifecycle-only repair (start+stop, no content
        // resend) — that's a repair, not new work, so SentChildContent stays false. (Outcome
        // stays Skipped.)
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Skipped);
        await Assert.That(result.SentChildContent).IsFalse();

        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path).ToList();

        // Lifecycle repaired (start re-marks active so stop can append completion), but no content
        // re-send.
        await Assert.That(posts.Contains("/hooks/subagent-start")).IsTrue();
        await Assert.That(posts.Contains("/hooks/subagent-stop")).IsTrue();
        await Assert.That(posts.Contains("/hooks/transcript")).IsFalse();
        // Ordering: start precedes stop so the server has the active mark when stop lands.
        await Assert.That(posts.IndexOf("/hooks/subagent-start")).IsLessThan(posts.IndexOf("/hooks/subagent-stop"));

        // Repair lifecycle POSTs are strict, so a server-side start failure surfaces as non-2xx
        // (the server otherwise 200s even when RecordAgentStartAsync rolls back the active mark).
        var startBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(startBody).Contains("\"strict\":true");

        // The strict stop payload MUST carry the full SubagentStopHook shape, else the server
        // rejects it at binding before strict is honored (the repair would loop forever).
        var stopBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-stop").RequestMessage.Body!;
        await Assert.That(stopBody).Contains("\"strict\":true");
        await Assert.That(stopBody).Contains("\"stop_hook_active\"");
        await Assert.That(stopBody).Contains("\"agent_transcript_path\"");
        await Assert.That(stopBody).Contains("\"last_assistant_message\"");
    }

    [Test]
    public async Task ImportChildren_resumes_by_line_position_without_shifting_numbers() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        // Parent not yet imported (404) → New; child partially imported up to line 0 → resume from
        // file line 1, numbered by TRUE position (1). The pre-fix code fed the HWM as
        // lineNumberOffset and re-sent the whole file as [1,2].
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", Dashless(Child)).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(new(new(_home), ""));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);

        await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        var childTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!)
            .Single(b => b.Contains($"\"agent_id\":\"{Dashless(Child)}\""));

        // Only file line 1 is re-sent, numbered 1 — not the whole file shifted to [1,2].
        await Assert.That(childTranscript).Contains("\"line_numbers\":[1]");
    }

    [Test]
    public async Task ImportSession_second_run_is_AlreadyLoaded() {
        WriteTranscript(Root, "build it");

        // Server reports a high watermark → the transcript's last relevant line is already ingested.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        // AlreadyLoaded still re-asserts lifecycle (idempotent repair), so stub those POSTs.
        foreach (var route in new[] { "/hooks/session-start/antigravity", "/hooks/session-end/antigravity" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(new(new(_home), ""));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Skipped);

        // No parent transcript re-send, but lifecycle IS re-asserted (repair path).
        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(posts.Contains("/hooks/session-start/antigravity")).IsTrue();
        await Assert.That(posts.Contains("/hooks/session-end/antigravity")).IsTrue();
        await Assert.That(posts.Contains("/hooks/transcript")).IsFalse();
    }
}
