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
        } finally {
            File.Delete(path);
        }
    }
}
