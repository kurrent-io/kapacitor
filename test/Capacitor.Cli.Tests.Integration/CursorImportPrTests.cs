using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Cursor;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Task 5.1: Cursor historical import must attach owner/repo but never a stale PR
/// (importing an old session shouldn't stamp today's PR onto it — an anachronism), and repo
/// detection for a given workspace must run at most once per <c>import</c> invocation even
/// when many sessions share the same cwd. Drives the real discover → classify → import path
/// against a stub server, mirroring <see cref="PiImportSourceImportTests"/>.
/// </summary>
public class CursorImportPrTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server  = WireMockServer.Start();
    readonly TempDir        _tmp     = new();
    readonly string         _tempDir;

    public CursorImportPrTests() => _tempDir = _tmp.Path;

    string ProjectsDir         => Path.Combine(_tempDir, ".cursor", "projects");
    string WorkspaceStorageDir => Path.Combine(_tempDir, "workspaceStorage");

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    /// <summary>
    /// Two distinct Cursor sessions whose sanitized project dir resolves (via a shared
    /// workspace.json) to the SAME cwd — the shape that exercises per-cwd cache reuse.
    /// </summary>
    string WriteTwoCursorSessionsInSameCwd() {
        Directory.CreateDirectory(ProjectsDir);
        Directory.CreateDirectory(WorkspaceStorageDir);

        var wsDir = Path.Combine(WorkspaceStorageDir, "hash-shared");
        Directory.CreateDirectory(wsDir);
        File.WriteAllText(Path.Combine(wsDir, "workspace.json"), """{"folder":"file:///Users/me/dev/shared"}""");

        AddSession("Users-me-dev-shared", "11111111-1111-1111-1111-111111111111");
        AddSession("Users-me-dev-shared", "22222222-2222-2222-2222-222222222222");

        return ProjectsDir;
    }

    void AddSession(string sanitized, string sessionId) {
        var dir = Path.Combine(ProjectsDir, sanitized, "agent-transcripts", sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, sessionId + ".jsonl"),
            "{\"a\":1}\n{\"b\":2}\n"
        );
    }

    [Test]
    public async Task Import_attaches_repo_but_records_no_pull_request() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        // GET /api/sessions/*/last-line is left unmapped — WireMock 404s it, which
        // FetchServerLastLineAsync treats as "no watermark" → classification New.

        var detectCalls = 0;
        var source = new CursorImportSource(Config.Root, 
            WriteTwoCursorSessionsInSameCwd(),
            WorkspaceStorageDir,
            repoDetector: _ => {
                detectCalls++;
                return Task.FromResult<RepositoryPayload?>(
                    new RepositoryPayload { Owner = "acme", RepoName = "widgets" });
            });

        using var client = new HttpClient();

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(2);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified.Count).IsEqualTo(2);

        foreach (var c in classified) {
            var outcome = await source.ImportSessionAsync(
                c,
                new ImportContext(client, _server.Url!, ForcePrivate: false),
                CancellationToken.None);
            await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);
        }

        var startBodies = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/session-start/cursor")
            .Select(e => e.RequestMessage.Body!)
            .ToArray();
        await Assert.That(startBodies.Length).IsEqualTo(2);

        foreach (var body in startBodies) {
            await Assert.That(body).Contains("\"owner\":\"acme\"");
            // BuildRepositoryNode only ever emits these four PR-specific keys — none of
            // them may appear when importing a historical session (no live PR round-trip,
            // and this never stamps today's PR onto an old transcript).
            await Assert.That(body).DoesNotContain("pr_number");
            await Assert.That(body).DoesNotContain("pr_title");
            await Assert.That(body).DoesNotContain("pr_url");
            await Assert.That(body).DoesNotContain("pr_head_ref");
        }

        // Cached per cwd: two sessions sharing the same resolved workspace folder must
        // trigger repo detection exactly once across the whole import.
        await Assert.That(detectCalls).IsEqualTo(1);
    }
}
