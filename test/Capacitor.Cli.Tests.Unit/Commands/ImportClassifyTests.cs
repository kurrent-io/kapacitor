using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportClassifyTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public ImportClassifyTests() => _tempDir = _tmp.Path;

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
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

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(result[0].SessionId).IsEqualTo("sessionNew");
        // TotalLines is only populated for TooShort; other statuses early-exit before counting.
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

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
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

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(43);
    }

    [Test]
    public async Task ClassifyAsync_maps_short_transcript_to_TooShort() {
        // TooShort is decided after the probe, only for sessions that would otherwise be
        // New or Partial, so AlreadyLoaded re-runs don't pay for scanning huge transcripts.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var path = await WriteTranscript(_tempDir, "tiny", lines: 5);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("tiny", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
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

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
        await Assert.That(result[0].ProbeErrorReason).IsEqualTo("HTTP 500");
    }

    [Test]
    public async Task ClassifyAsync_identifies_kcap_subsession() {
        // Nested under _tempDir so Dispose cleans it up.
        var subagentDir = Path.Combine(_tempDir, "kcap-sub");
        Directory.CreateDirectory(subagentDir);
        var path = Path.Combine(subagentDir, "agent-title-abc123.jsonl");
        // \n must be JSON-escaped so the parser sees a literal newline in the prompt content.
        var queueOpLine = """{"type":"queue-operation","operation":"enqueue","content":"<role>\nYou label coding-session transcripts. You are NOT the assistant being addressed"}""";
        await File.WriteAllLinesAsync(path, [queueOpLine]);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("title-abc123", path, "-tmp-sub")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: null,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.InternalSubSession);
    }

    [Test]
    public async Task ClassifyAsync_tags_ExcludedRepoKey_for_new_sessions_in_excluded_repos() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        // cwd must be a real repo so DetectRepositoryAsync can query its remote.
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/secret.git");

        var transcriptPath = Path.Combine(_tempDir, "sessionX.jsonl");

        await File.WriteAllLinesAsync(
            transcriptPath,
            Enumerable.Range(0, 50)
                .Select(_ =>
                    $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"{{{repo.Path.Replace("\\", @"\\")}}}","message":{"content":"x"}}"""
                )
        );

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionX", transcriptPath, repo.Path.Replace('/', '-'))
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 15,
            excludedRepos: ["acme/secret"],
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
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

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
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
        // last_line_number=49 covers all 50 local lines (indices 0..49), so despite
        // the 200 response this is a false Partial and must reclassify to AlreadyLoaded.
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

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client, _server.Url!, transcripts,
            minLines: 15, excludedRepos: null, CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
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

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client, _server.Url!, transcripts,
            minLines: 15, excludedRepos: null, CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(50);
    }

    [Test]
    public async Task ClassifyAsync_does_not_set_ExcludedRepoKey_when_reclassified_to_AlreadyLoaded() {
        // Pins ordering: reclassification (Partial -> AlreadyLoaded) must run before the
        // excluded-repo block, which only fires for New|Partial. Otherwise ExcludedRepoKey
        // would get set even though there's no new work to prompt about.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 49}""")
            );

        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/any/repo.git");

        var transcriptPath = Path.Combine(_tempDir, "excludedNoNew.jsonl");

        await File.WriteAllLinesAsync(
            transcriptPath,
            Enumerable.Range(0, 50)
                .Select(i =>
                    $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"{{{repo.Path.Replace("\\", @"\\")}}}","message":{"content":"line-{{{i}}}"}}"""
                )
        );

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("excludedNoNew", transcriptPath, repo.Path.Replace('/', '-'))
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client, _server.Url!, transcripts,
            minLines: 15,
            excludedRepos: ["any/repo"],
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(result[0].ExcludedRepoKey).IsNull();
    }

}
