using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class HistoryClassifyTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kapacitor-classify-test").FullName;

    public void Dispose() {
        _server.Stop();

        try { Directory.Delete(_tempDir, recursive: true); } catch {
            /* best effort */
        }
    }

    static async Task<string> WriteTranscript(string dir, string sessionId, int lines) {
        var path = Path.Combine(dir, $"{sessionId}.jsonl");

        await File.WriteAllLinesAsync(
            path,
            Enumerable.Range(0, lines)
                .Select(i =>
                    $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
                )
        );

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
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.New);
        await Assert.That(result[0].SessionId).IsEqualTo("sessionNew");
        // TotalLines is only populated for TooShort; classification uses a bounded
        // count that early-exits once the file clears the minLines threshold.
        await Assert.That(result[0].TotalLines).IsEqualTo(0);
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
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.AlreadyLoaded);
    }

    [Test]
    public async Task ClassifyAsync_maps_200_with_last_line_to_Partial() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 42}""")
            );

        var path = await WriteTranscript(_tempDir, "sessionPartial", lines: 100);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionPartial", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await HistoryCommand.ClassifyAsync(
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(43);
    }

    [Test]
    public async Task ClassifyAsync_maps_short_transcript_to_TooShort() {
        // TooShort is decided AFTER the probe, only for sessions that would otherwise
        // be New or Partial. This avoids scanning huge transcripts for AlreadyLoaded
        // sessions on re-runs.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var path = await WriteTranscript(_tempDir, "tiny", lines: 5);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("tiny", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await HistoryCommand.ClassifyAsync(
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

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
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.ProbeError);
        await Assert.That(result[0].ProbeErrorReason).IsEqualTo("HTTP 500");
    }

    [Test]
    public async Task ClassifyAsync_identifies_kapacitor_subsession() {
        // IsKapacitorSubSession detects headless claude -p sessions by reading the file:
        // the first lines must contain a queue-operation entry whose content starts with
        // a known kapacitor prompt prefix (title generation or what's-done summary).
        // Nested under _tempDir so Dispose cleans it up.
        var subagentDir = Path.Combine(_tempDir, "kapacitor-sub");
        Directory.CreateDirectory(subagentDir);
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
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.InternalSubSession);
    }

    [Test]
    public async Task ClassifyAsync_tags_ExcludedRepoKey_for_new_sessions_in_excluded_repos() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        // Make a transcript whose cwd is a real git repo with an "excluded" remote.
        // git init + git remote add produces a real repo that DetectRepositoryAsync can query.
        // Nested under _tempDir so Dispose cleans it up.
        var repoDir = Path.Combine(_tempDir, "kapacitor-excl");
        Directory.CreateDirectory(repoDir);
        await RunGitAsync("init", repoDir);
        await RunGitAsync("remote add origin https://github.com/acme/secret.git", repoDir);

        var transcriptPath = Path.Combine(_tempDir, "sessionX.jsonl");

        await File.WriteAllLinesAsync(
            transcriptPath,
            Enumerable.Range(0, 50)
                .Select(i =>
                    $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"{{{repoDir.Replace("\\", "\\\\")}}}","message":{"content":"x"}}"""
                )
        );

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionX", transcriptPath, repoDir.Replace('/', '-'))
        };

        using var client = new HttpClient();

        var result = await HistoryCommand.ClassifyAsync(
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: ["acme/secret"],
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.New);
        await Assert.That(result[0].ExcludedRepoKey).IsEqualTo("acme/secret");
    }

    [Test]
    public async Task ClassifyAsync_invokes_onProbed_callback_once_per_transcript() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var paths = new List<(string SessionId, string FilePath, string EncodedCwd)>();
        for (var i = 0; i < 5; i++) {
            var path = await WriteTranscript(_tempDir, $"cb-{i}", lines: 50);
            paths.Add(($"cb-{i}", path, "-tmp-proj"));
        }

        var probedCount = 0;
        using var client = new HttpClient();

        var result = await HistoryCommand.ClassifyAsync(
            client,
            _server.Url!,
            paths,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None,
            onProbed: () => Interlocked.Increment(ref probedCount)
        );

        await Assert.That(result.Count).IsEqualTo(5);
        await Assert.That(probedCount).IsEqualTo(5);
    }

    [Test]
    public async Task ClassifyAsync_reclassifies_Partial_to_AlreadyLoaded_when_no_new_lines() {
        // Server says last_line_number = 49 (50 lines stored: indices 0..49).
        // Local transcript is exactly 50 lines (indices 0..49). resumeFromLine
        // would be 50 — but there are no lines past index 49, so this is a
        // false Partial that should be reclassified as AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 49}""")
            );

        var path = await WriteTranscript(_tempDir, "noNewLines", lines: 50);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("noNewLines", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts,
            minLines: 15, excludedRepos: null, CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(0);
    }

    [Test]
    public async Task ClassifyAsync_keeps_Partial_when_local_transcript_has_new_lines() {
        // Server says last_line_number = 49. Local transcript is 60 lines —
        // there are 10 new lines past index 49, so Partial is correct.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 49}""")
            );

        var path = await WriteTranscript(_tempDir, "hasNewLines", lines: 60);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("hasNewLines", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts,
            minLines: 15, excludedRepos: null, CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(50);
    }

    [Test]
    public async Task ClassifyAsync_does_not_set_ExcludedRepoKey_when_reclassified_to_AlreadyLoaded() {
        // A "Partial" probe in an excluded repo, but the local transcript has no new
        // lines. We must NOT prompt the user to "include this excluded repo" for work
        // that does not exist — ExcludedRepoKey must be null.
        //
        // This fixture uses a real git-initialised temp directory whose remote matches
        // the excluded list, so DetectRepositoryAsync returns a repo key of "any/repo".
        // With that setup, the only way ExcludedRepoKey can remain null is if
        // reclassification (Partial → AlreadyLoaded) has already moved status out of
        // New|Partial before the excluded-repo block runs. If a future refactor swaps
        // those two blocks, DetectRepositoryAsync will resolve "any/repo" and
        // ExcludedRepoKey will be set — causing this test to fail.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 49}""")
            );

        // Create a real git repo under _tempDir so DetectRepositoryAsync can resolve it.
        var repoDir = Path.Combine(_tempDir, "kapacitor-reclass-excl");
        Directory.CreateDirectory(repoDir);
        await RunGitAsync("init", repoDir);
        await RunGitAsync("remote add origin https://github.com/any/repo.git", repoDir);

        // Write a 50-line transcript whose cwd points at the repo above.
        // ExtractSessionMetadata reads "cwd" from the JSONL lines, so
        // DetectRepositoryAsync will be called with repoDir.
        var transcriptPath = Path.Combine(_tempDir, "excludedNoNew.jsonl");

        await File.WriteAllLinesAsync(
            transcriptPath,
            Enumerable.Range(0, 50)
                .Select(i =>
                    $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"{{{repoDir.Replace("\\", "\\\\")}}}","message":{"content":"line-{{{i}}}"}}"""
                )
        );

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("excludedNoNew", transcriptPath, repoDir.Replace('/', '-'))
        };

        using var client = new HttpClient();

        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts,
            minLines: 15,
            excludedRepos: new[] { "any/repo" },
            CancellationToken.None
        );

        // last_line_number=49 with exactly 50 local lines means no new lines to send,
        // so the session is reclassified from Partial to AlreadyLoaded.
        // The excluded-repo block only fires for New|Partial, so ExcludedRepoKey must
        // be null even though the repo key matches the excluded list.
        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(result[0].ExcludedRepoKey).IsNull();
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
