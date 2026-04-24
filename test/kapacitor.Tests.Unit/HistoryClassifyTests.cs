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

    [Test]
    public async Task ClassifyAsync_maps_200_with_last_line_to_Partial() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"last_line_number": 42}"""));

        var path = await WriteTranscript(_tempDir, "sessionPartial", lines: 100);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionPartial", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(43);
    }

    [Test]
    public async Task ClassifyAsync_maps_short_transcript_to_TooShort_without_probing() {
        // No WireMock stub — if ClassifyAsync probes, this will fail on a bare 404 default.
        // But since we classify TooShort before the probe, there's no request at all.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500)); // sabotage: would cause ProbeError if used

        var path = await WriteTranscript(_tempDir, "tiny", lines: 5);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("tiny", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.TooShort);
        await Assert.That(result[0].TotalLines).IsEqualTo(5);
    }

    [Test]
    public async Task ClassifyAsync_maps_server_error_to_ProbeError() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var path = await WriteTranscript(_tempDir, "sessionErr", lines: 50);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionErr", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.ProbeError);
        await Assert.That(result[0].ProbeErrorReason).IsEqualTo("HTTP 500");
    }

    [Test]
    public async Task ClassifyAsync_identifies_kapacitor_subsession() {
        // IsKapacitorSubSession detects headless claude -p sessions by reading the file:
        // the first lines must contain a queue-operation entry whose content starts with
        // a known kapacitor prompt prefix (title generation or what's-done summary).
        var subagentDir = Directory.CreateTempSubdirectory("kapacitor-sub").FullName;
        var path = Path.Combine(subagentDir, "agent-title-abc123.jsonl");
        // The title prompt starts with "<role>\nYou label coding-session transcripts. "
        // The \n must be JSON-escaped (\n literal in JSON string) for the parser to see a newline in the value.
        var queueOpLine = """{"type":"queue-operation","operation":"enqueue","content":"<role>\nYou label coding-session transcripts. You are NOT the assistant being addressed"}""";
        await File.WriteAllLinesAsync(path, [queueOpLine]);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("title-abc123", path, "-tmp-sub")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.InternalSubSession);
    }

    [Test]
    public async Task ClassifyAsync_tags_ExcludedRepoKey_for_new_sessions_in_excluded_repos() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        // Make a transcript whose cwd is a real git repo with an "excluded" remote.
        // git init + git remote add produces a real repo that DetectRepositoryAsync can query.
        var repoDir = Directory.CreateTempSubdirectory("kapacitor-excl").FullName;
        await RunGitAsync("init", repoDir);
        await RunGitAsync("remote add origin https://github.com/acme/secret.git", repoDir);

        var transcriptPath = Path.Combine(_tempDir, "sessionX.jsonl");
        await File.WriteAllLinesAsync(transcriptPath, Enumerable.Range(0, 50).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"{{{repoDir.Replace("\\", "\\\\")}}}","message":{"content":"x"}}"""
        ));

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionX", transcriptPath, repoDir.Replace('/', '-'))
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15,
            excludedRepos: ["acme/secret"], CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.New);
        await Assert.That(result[0].ExcludedRepoKey).IsEqualTo("acme/secret");
    }

    static async Task RunGitAsync(string arguments, string workingDir) {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments) {
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) {
            var err = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"git {arguments} failed: {err}");
        }
    }
}
