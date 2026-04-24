using System.Net;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class HistoryClassifyTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kapacitor-classify-test").FullName;

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    static async Task<string> WriteTranscript(string dir, string sessionId, int lines) {
        var path = Path.Combine(dir, $"{sessionId}.jsonl");
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return path;
    }

    [Test]
    public async Task ClassifyAsync_maps_404_to_New() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var path = await WriteTranscript(_tempDir, "sessionNew", lines: 50);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionNew", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.New);
        await Assert.That(result[0].SessionId).IsEqualTo("sessionNew");
        await Assert.That(result[0].TotalLines).IsEqualTo(50);
    }

    [Test]
    public async Task ClassifyAsync_maps_204_to_AlreadyLoaded() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(204));

        var path = await WriteTranscript(_tempDir, "sessionDone", lines: 50);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionDone", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.AlreadyLoaded);
    }
}
