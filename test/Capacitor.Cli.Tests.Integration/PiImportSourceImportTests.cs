using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Pi;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Routed-import wire-contract tests for <see cref="PiImportSource"/>.
/// Pi classifications carry <c>FilePath = ""</c>, so they run through the
/// routed phase (<c>ImportSessionAsync</c>) rather than the chain worker. These
/// drive the full discover → classify → import path against a stub server and
/// assert the three POSTs the server expects: <c>/hooks/session-start/pi</c>,
/// the transcript batch tagged <c>vendor=pi</c>, and <c>/hooks/session-end/pi</c>
/// — the integration points most likely to drift from the lower-level helpers.
/// </summary>
public class PiImportSourceImportTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public PiImportSourceImportTests() => _tempDir = _tmp.Path;

    const string DashedSid = "11111111-2222-3333-4444-555555555555";

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string WriteSessionFile() {
        var sessionsDir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var path = Path.Combine(sessionsDir, DashedSid + ".jsonl");
        File.WriteAllLines(path, new[] {
            $$"""{"type":"session","version":3,"id":"{{DashedSid}}","timestamp":"2026-06-12T10:00:00.000Z","cwd":"/work/a"}""",
            """{"type":"message","id":"a1","parentId":null,"timestamp":"2026-06-12T10:00:01.000Z","message":{"role":"user","content":"hello"}}""",
            """{"type":"message","id":"a2","parentId":"a1","timestamp":"2026-06-12T10:00:02.000Z","message":{"role":"assistant","content":[{"type":"text","text":"hi there"}],"model":"gpt-5","usage":{"input":10,"output":3}}}"""
        });
        return sessionsDir;
    }

    string WriteSessionFileEndingWithBranchSummary() {
        var sessionsDir = Path.Combine(_tempDir, "sessions-tail");
        Directory.CreateDirectory(sessionsDir);
        var path = Path.Combine(sessionsDir, DashedSid + ".jsonl");
        File.WriteAllLines(path, new[] {
            $$"""{"type":"session","version":3,"id":"{{DashedSid}}","timestamp":"2026-06-12T10:00:00.000Z","cwd":"/work/a"}""",
            """{"type":"message","id":"a1","parentId":null,"timestamp":"2026-06-12T10:00:01.000Z","message":{"role":"user","content":"hello"}}""",
            """{"type":"branch_summary","id":"b1","parentId":"a1","timestamp":"2026-06-12T10:00:01.500Z","fromId":"a1","summary":"branch summary kept for import"}"""
        });
        return sessionsDir;
    }

    [Test]
    public async Task ImportSession_posts_lifecycle_and_transcript_with_pi_vendor() {
        var sessionsDir = WriteSessionFile();

        // Fresh server: the last-line probe 404s, so classification is New.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        var source = new PiImportSource(Config.Root, 
            sessionsDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified.Count).IsEqualTo(1);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(classified[0].Vendor).IsEqualTo("pi");

        var outcome = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        // Lifecycle + transcript routes were all hit, in receipt order
        // (ImportSessionAsync awaits each POST sequentially, and LogEntries
        // preserves arrival order).
        var posts = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path)
            .ToArray();

        await Assert.That(posts).IsEquivalentTo(new[] {
            "/hooks/session-start/pi",
            "/hooks/transcript",
            "/hooks/session-end/pi"
        });

        // The transcript batch is tagged vendor=pi so the server routes it to
        // PiTranscriptNormalizer.
        var transcriptBody = _server.LogEntries
            .Single(e => e.RequestMessage.Path == "/hooks/transcript")
            .RequestMessage.Body;
        await Assert.That(transcriptBody!).Contains("\"vendor\":\"pi\"");

        // The synthesized session-end marks itself as an import (reason pi-import).
        var endBody = _server.LogEntries
            .Single(e => e.RequestMessage.Path == "/hooks/session-end/pi")
            .RequestMessage.Body;
        await Assert.That(endBody!).Contains("pi-import");
    }

    [Test]
    public async Task ImportSession_resumes_from_server_watermark_when_partial() {
        var sessionsDir = WriteSessionFile();

        // Server already has line 0 (the header) → classification is Partial,
        // resume from line 1.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));
        _server.Given(Request.Create().WithPath("/hooks/session-start/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        var source = new PiImportSource(Config.Root, 
            sessionsDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(1);

        var outcome = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Resumed);
    }

    [Test]
    public async Task Classify_treats_trailing_branch_summary_as_import_relevant_when_resuming() {
        var sessionsDir = WriteSessionFileEndingWithBranchSummary();

        // Server already has the user turn on line 1. The only remaining relevant
        // line is a Pi branch_summary, so classification must resume from line 2
        // rather than deciding the transcript is already loaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));

        using var client = new HttpClient();

        var source = new PiImportSource(Config.Root, 
            sessionsDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);

        await Assert.That(classified.Count).IsEqualTo(1);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(2);
    }

    // --- Pi's AlreadyLoaded replay is a genuine no-op (no subagent handling in this vendor at
    // all) — the vendor-neutral gate must suppress this shape. ---

    [Test]
    public async Task ImportSession_AlreadyLoaded_replay_is_a_no_op_suppressed_by_the_vendor_neutral_gate() {
        var sessionsDir = WriteSessionFile();

        // Server already covers every importable line → AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        _server.Given(Request.Create().WithPath("/hooks/session-start/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        var source = new PiImportSource(Config.Root, 
            sessionsDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        // Pi never touches a child/subagent stream on this path — SentChildContent stays at its
        // safe default false.
        await Assert.That(result.SentChildContent).IsFalse();

        // With the vendor parameter dropped, this real (status, outcome, sentChildContent) shape
        // is recognized as a lifecycle-only replay and must be suppressed — not counted as
        // Loaded/Resumed on top of the classify-time AlreadyLoaded bucket.
        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();

        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsNull();
    }
}
