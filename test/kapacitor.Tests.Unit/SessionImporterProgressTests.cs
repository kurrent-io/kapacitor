using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class SessionImporterProgressTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SendTranscriptBatches_fires_BatchFlushed_per_100_lines() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // Write 250 non-blank JSONL lines → expect 3 flushes: 100, 100, 50
        var path = Path.GetTempFileName();
        try {
            await File.WriteAllLinesAsync(path, Enumerable.Range(0, 250).Select(i =>
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"line-{{{i}}}"}}"""
            ));

            var events = new List<ImportProgress>();
            var progress = new Progress<ImportProgress>(events.Add);

            using var client = new HttpClient();
            var totalSent = await SessionImporter.SendTranscriptBatches(
                client, _server.Url!, sessionId: "test", filePath: path,
                agentId: null, startLine: 0, progress: progress
            );

            await Assert.That(totalSent).IsEqualTo(250);

            // Progress<T> marshals via SynchronizationContext; give it a tick
            await Task.Delay(50);

            var flushes = events.OfType<BatchFlushed>().ToList();
            await Assert.That(flushes.Count).IsEqualTo(3);
            await Assert.That(flushes[0].LinesAdded).IsEqualTo(100);
            await Assert.That(flushes[1].LinesAdded).IsEqualTo(100);
            await Assert.That(flushes[2].LinesAdded).IsEqualTo(50);
            await Assert.That(flushes.All(f => f.AgentId == null)).IsTrue();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ImportSessionAsync_fires_subagent_events_around_inline_import() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var tmp = Directory.CreateTempSubdirectory("kapacitor-import-test");
        try {
            var sessionName = Guid.NewGuid().ToString("N");
            var sessionPath = Path.Combine(tmp.FullName, $"{sessionName}.jsonl");
            var agentsDir   = Path.Combine(tmp.FullName, sessionName, "subagents");
            Directory.CreateDirectory(agentsDir);

            var agentId = Guid.NewGuid().ToString("N");
            var agentPath = Path.Combine(agentsDir, $"agent-{agentId}.jsonl");

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
            var progress = new Progress<ImportProgress>(events.Add);

            using var client = new HttpClient();
            var result = await SessionImporter.ImportSessionAsync(
                client, _server.Url!, sessionPath, sessionId: "s1",
                new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
                progress: progress
            );

            await Task.Delay(50);

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
        } finally {
            tmp.Delete(recursive: true);
        }
    }
}
