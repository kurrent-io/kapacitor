using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionImporterProgressTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    /// <summary>
    /// Synchronous IProgress&lt;T&gt;: appends to the callback in the calling
    /// thread, unlike <see cref="Progress{T}"/> which marshals via the captured
    /// SynchronizationContext and required a 50ms sleep after each test's
    /// await — flaky on Linux CI where the marshalling sometimes slipped past
    /// the deadline.
    /// </summary>
    sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T> {
        public void Report(T value) => onReport(value);
    }

    [Test]
    public async Task SendTranscriptBatches_fires_BatchFlushed_per_100_lines() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // Write 250 non-blank JSONL lines → expect 3 flushes: 100, 100, 50
        using var tmp = TempDir.WithPathTo("transcript.tmp", out var path);
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 250).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"line-{{{i}}}"}}"""
        ));

        var events = new List<ImportProgress>();
        var progress = new SyncProgress<ImportProgress>(events.Add);

        using var client = new HttpClient();
        var totalSent = await SessionImporter.SendTranscriptBatches(
            client, _server.Url!, sessionId: "test", filePath: path,
            agentId: null, startLine: 0, progress: progress
        );

        await Assert.That(totalSent).IsEqualTo(250);

        var flushes = events.OfType<BatchFlushed>().ToList();
        await Assert.That(flushes.Count).IsEqualTo(3);
        await Assert.That(flushes[0].LinesAdded).IsEqualTo(100);
        await Assert.That(flushes[1].LinesAdded).IsEqualTo(100);
        await Assert.That(flushes[2].LinesAdded).IsEqualTo(50);
        await Assert.That(flushes.All(f => f.AgentId == null)).IsTrue();
    }

    // abortDelivery must be re-checked immediately AFTER a
    // POST too, not only before the next one. A transcript small enough to be a SINGLE (final and
    // only) batch never reaches a "next" pre-POST check at all, so before this fix a quarantine
    // marker that appeared while that one-and-only POST was "in flight" was never observed anywhere
    // — the method returned normally with the caller none the wiser.
    [Test]
    public async Task SendTranscriptBatches_observes_quarantine_written_during_the_only_batch_post() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // 30 non-blank lines — a single batch, well under the 100-line flush threshold.
        using var tmp = TempDir.WithPathTo("transcript.tmp", out var path);
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 30).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"line-{{{i}}}"}}"""
        ));

        using var client = new HttpClient();
        var quarantined = false;

        await Assert.ThrowsAsync<SessionImporter.TranscriptDeliveryAbortedException>(async () => {
            await SessionImporter.SendTranscriptBatches(
                client, _server.Url!, sessionId: "test", filePath: path,
                agentId: null, startLine: 0,
                // Not quarantined for the pre-POST check (nothing has posted yet), but flips
                // true the moment it is consulted a SECOND time — simulating a marker written
                // while the one-and-only POST above was "in flight".
                abortDelivery: () => {
                    var wasQuarantined = quarantined;
                    quarantined = true;
                    return wasQuarantined;
                });
        });
    }

    [Test]
    public async Task CountSendableLines_counts_non_blank_lines_from_start() {
        // The per-session progress denominator: must equal the number of
        // lines SendTranscriptBatches/ImportSessionAsync actually POST — non-blank
        // lines from startLine to EOF.
        using var tmp = TempDir.WithPathTo("transcript.tmp", out var path);
        await File.WriteAllLinesAsync(path, ["a", "", "b", "   ", "c"]);

        // Whole file: 3 non-blank lines (a, b, c); the two blank lines don't count.
        await Assert.That(SessionImporter.CountSendableLines(path)).IsEqualTo(3);

        // From line index 2 onward: "b", "   ", "c" → 2 non-blank (b, c).
        await Assert.That(SessionImporter.CountSendableLines(path, startLine: 2)).IsEqualTo(2);

        // A missing file is best-effort 0 (slot stays indeterminate, never errors).
        await Assert.That(SessionImporter.CountSendableLines(path + ".nope")).IsEqualTo(0);
    }

    [Test]
    public async Task ImportSessionAsync_fires_subagent_events_around_inline_import() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var tmp = new TempDir();
        var sessionName = Guid.NewGuid().ToString("N");
        var sessionPath = tmp.PathTo($"{sessionName}.jsonl");
        var agentsDir   = tmp.CreateDir(sessionName, "subagents");

        var agentId = Guid.NewGuid().ToString("N");
        var agentPath = agentsDir.PathTo($"agent-{agentId}.jsonl");

        await File.WriteAllLinesAsync(sessionPath, [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu1","name":"Task","input":{"subagent_type":"code-reviewer"}}]}}""",
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{agentId}}}"}}""",
            """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"after"}}"""
        ]);

        await File.WriteAllLinesAsync(agentPath, [
            """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"a1"}}""",
            """{"type":"assistant","timestamp":"2026-03-15T10:00:01Z","message":{"content":"a2"}}""",
            """{"type":"user","timestamp":"2026-03-15T10:00:02Z","message":{"content":"a3"}}"""
        ]);

        var events   = new List<ImportProgress>();
        var progress = new SyncProgress<ImportProgress>(events.Add);

        using var client = new HttpClient();
        var result = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, sessionPath, sessionId: "s1",
            new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
            progress: progress
        );

        await Assert.That(result.AgentIds).Contains(agentId);

        var ordered = events.ToList();
        var startIdx  = ordered.FindIndex(e => e is SubagentStarted s && s.AgentId == agentId);
        var finishIdx = ordered.FindIndex(e => e is SubagentFinished f && f.AgentId == agentId);

        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(finishIdx).IsGreaterThan(startIdx);
        await Assert.That(((SubagentFinished)ordered[finishIdx]).LinesSent).IsEqualTo(3);

        // Batch flushes between the subagent boundaries must carry the agent id
        // so UI callers can attribute lines to parent vs subagent.
        var subagentBatches = ordered
            .Skip(startIdx)
            .Take(finishIdx - startIdx)
            .OfType<BatchFlushed>()
            .ToList();
        await Assert.That(subagentBatches.Count).IsGreaterThan(0);
        await Assert.That(subagentBatches.All(b => b.AgentId == agentId)).IsTrue();
    }
}
