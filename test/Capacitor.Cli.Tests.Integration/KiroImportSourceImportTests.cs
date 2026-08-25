using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Kiro;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Kiro has no subagent handling at all on its routed <c>AlreadyLoaded</c> path —
/// <c>SentChildContent</c> stays at its safe default <c>false</c> via the implicit
/// <see cref="ImportOutcome"/> conversion. A real Kiro AlreadyLoaded replay must be recognized
/// by <see cref="ImportCommand.IsLifecycleOnlyRoutedReplay"/> (vendor-neutral) and suppressed
/// so it doesn't double-count on top of the classify-time AlreadyLoaded bucket.
/// </summary>
public class KiroImportSourceImportTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public KiroImportSourceImportTests() => _tempDir = _tmp.Path;

    const string DashedSid = "11111111-2222-3333-4444-555555555555";

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string WriteSession() {
        var path = Path.Combine(_tempDir, DashedSid + ".jsonl");
        File.WriteAllLines(path, new[] {
            """{"version":"v1","kind":"Prompt","data":{"message_id":"m1","content":[{"kind":"text","data":"hi"}]}}""",
            """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"m2","content":[{"kind":"text","data":"hello back"}]}}"""
        });
        File.WriteAllText(Path.Combine(_tempDir, DashedSid + ".json"), """{"cwd":"/work/a","title":"t","created_at":"2026-06-10T20:23:49.371Z","updated_at":"2026-06-10T20:23:50.000Z"}""");
        return _tempDir;
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_replay_is_a_no_op_suppressed_by_the_vendor_neutral_gate() {
        var root = WriteSession();

        // Server already covers every importable line → AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));
        foreach (var route in new[] { "/hooks/session-start/kiro", "/hooks/set-title", "/hooks/session-end/kiro" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new KiroImportSource(Config.Root, 
            sessionsDirOverride: root,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        // Kiro never touches a child/subagent stream — SentChildContent stays false.
        await Assert.That(result.SentChildContent).IsFalse();

        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();

        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsNull();
    }
}
