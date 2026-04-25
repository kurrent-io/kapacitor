using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class HistoryImportChainsTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    // TUnit creates a new class instance per test, so _tempDir is always unique.
    readonly string _tempDir = Directory.CreateTempSubdirectory("kapacitor-import-chains-test").FullName;

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    void StubAllHookEndpoints() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    HistoryCommand.SessionClassification MakeNew(string id, int lines) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new HistoryCommand.SessionClassification {
            SessionId  = id,
            FilePath   = path,
            EncodedCwd = "-tmp-proj",
            Meta       = new SessionMetadata { Cwd = "/tmp/proj" },
            Status     = HistoryCommand.ClassificationStatus.New,
            TotalLines = lines,
        };
    }

    // No-git variant: both Meta.Cwd AND EncodedCwd must be empty so
    // ImportSingleSessionAsync resolves cwd to null and skips DetectRepositoryAsync.
    // A non-empty EncodedCwd would still decode to a valid cwd, triggering repo
    // detection and polluting parallelism-timing measurements.
    HistoryCommand.SessionClassification MakeNewNoGit(string id, int lines) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new HistoryCommand.SessionClassification {
            SessionId  = id,
            FilePath   = path,
            EncodedCwd = "",                      // DecodeCwdFromDirName returns null on empty input
            Meta       = new SessionMetadata(),   // Cwd = null
            Status     = HistoryCommand.ClassificationStatus.New,
            TotalLines = lines,
        };
    }

    [Test]
    public async Task ImportChainsAsync_counts_loaded_sessions() {
        StubAllHookEndpoints();

        var chains = new List<List<HistoryCommand.SessionClassification>> {
            new() { MakeNew("cnt-s1", 50) },
            new() { MakeNew("cnt-s2", 50) },
            new() { MakeNew("cnt-s3", 50) },
        };

        var completedLines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var events = new HistoryCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, c, _, _) => completedLines.Add($"Loading {c.SessionId}..."),
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        await Assert.That(result.Loaded).IsEqualTo(3);
        await Assert.That(result.Errored).IsEqualTo(0);
        await Assert.That(completedLines.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ImportChainsAsync_processes_within_chain_in_order() {
        StubAllHookEndpoints();

        // Single chain of 3 sessions. They must complete in order s1 → s2 → s3.
        var chain = new List<HistoryCommand.SessionClassification> {
            MakeNew("ord-s1", 50), MakeNew("ord-s2", 50), MakeNew("ord-s3", 50),
        };
        var chains = new List<List<HistoryCommand.SessionClassification>> { chain };

        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var events = new HistoryCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, c, _, _) => order.Enqueue($"Loading {c.SessionId}..."),
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await HistoryCommand.ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        var lines = order.ToArray();
        await Assert.That(lines[0]).Contains("ord-s1");
        await Assert.That(lines[1]).Contains("ord-s2");
        await Assert.That(lines[2]).Contains("ord-s3");
    }

    [Test]
    public async Task ImportChainsAsync_dispatches_independent_chains_in_parallel() {
        // WireMock adds a 200ms server-side delay to every transcript POST.
        // If chains run serially: 4 × 200ms = 800ms minimum.
        // If chains run in parallel (4 workers): all 4 transcript POSTs arrive at the server
        // within a short window; the spread between first and last POST arrival should be
        // well under 200ms (they all start roughly simultaneously).
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromMilliseconds(200)));
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // 4 independent chains, 1 session each.
        // MakeNewNoGit omits Meta.Cwd so DetectRepositoryAsync is never called —
        // git process startup would pollute the arrival-time spread.
        var chains = Enumerable.Range(0, 4)
            .Select(i => new List<HistoryCommand.SessionClassification> { MakeNewNoGit($"par{i}", 50) })
            .ToList();

        var events = new HistoryCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await HistoryCommand.ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        // Verify all 4 transcript POSTs arrived at the server.
        var transcriptEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .OrderBy(e => e.RequestMessage.DateTime)
            .ToList();
        await Assert.That(transcriptEntries.Count).IsEqualTo(4);

        // If chains ran in parallel, all 4 transcript requests arrived close together.
        // The spread between first and last arrival should be under 160ms
        // (200ms serial gap × 0.8 margin — any overlap proves parallelism).
        var firstArrival = transcriptEntries.First().RequestMessage.DateTime;
        var lastArrival  = transcriptEntries.Last().RequestMessage.DateTime;
        var spreadMs     = (lastArrival - firstArrival).TotalMilliseconds;

        // If serial, spread ≥ 3 × 200ms = 600ms (each chain waits for the previous).
        // If parallel, spread < 100ms (all chains start simultaneously).
        await Assert.That(spreadMs).IsLessThan(160);
    }
}
