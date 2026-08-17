using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Cursor;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Task C2 (D1, CLI side) — the CLI already posts <c>session-start/cursor</c> (carrying
/// <c>repository</c> when detected) before ever branching on classification status, so an
/// <c>AlreadyLoaded</c> (suppression-simulating) re-import still ships the repository payload the
/// server's D1 suppressed-path handler (Task C1) needs to backfill attribution. This pins that
/// contract: the server-side suppression (not the CLI) is what prevents the duplicate lifecycle
/// append — the CLI must keep sending the hook regardless. Models on <see cref="CursorImportPrTests"/>.
/// </summary>
public class CursorSuppressedRepoImportTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly TempDir        _tmp     = new();

    string ProjectsDir         => _tmp.PathTo(".cursor", "projects");
    string WorkspaceStorageDir => _tmp.PathTo("workspaceStorage");

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    const string SessionId = "11111111-1111-1111-1111-111111111111";

    string WriteOneCursorSessionWithWorkspace() {
        Directory.CreateDirectory(ProjectsDir);
        Directory.CreateDirectory(WorkspaceStorageDir);

        var wsDir = Path.Combine(WorkspaceStorageDir, "hash-shared");
        Directory.CreateDirectory(wsDir);
        File.WriteAllText(Path.Combine(wsDir, "workspace.json"), """{"folder":"file:///Users/me/dev/shared"}""");

        var dir = Path.Combine(ProjectsDir, "Users-me-dev-shared", "agent-transcripts", SessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SessionId + ".jsonl"), "{\"a\":1}\n{\"b\":2}\n");

        return ProjectsDir;
    }

    [Test]
    public async Task Suppression_simulating_reimport_still_posts_repository_and_reports_success() {
        // Server already has both non-blank lines (last_line_number 1, 0-indexed) — classification
        // must come back AlreadyLoaded, simulating an already-captured session being re-run
        // (the server's D1 suppressed path, Task C1, is what actually no-ops the lifecycle append).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(
            WriteOneCursorSessionWithWorkspace(),
            WorkspaceStorageDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(
                new RepositoryPayload { Owner = "acme", RepoName = "widgets" }));

        using var client = new HttpClient();

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified.Count).IsEqualTo(1);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var outcome = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        // Success — never Failed. The suppression-simulating stub above (an already-loaded server)
        // must not surface as an import failure.
        await Assert.That(outcome.Outcome).IsNotEqualTo(ImportOutcome.Failed);

        var startBodies = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/session-start/cursor")
            .Select(e => e.RequestMessage.Body!)
            .ToArray();
        await Assert.That(startBodies.Length).IsEqualTo(1);
        await Assert.That(startBodies.Any(b => b.Contains("\"repository\"") && b.Contains("\"repo_name\""))).IsTrue();
        await Assert.That(startBodies[0]).Contains("\"owner\":\"acme\"");
    }
}
