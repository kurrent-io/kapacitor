using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Copilot;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Copilot has no subagent handling at all on its routed <c>AlreadyLoaded</c> path —
/// <c>SentChildContent</c> stays at its safe default <c>false</c> via the implicit
/// <see cref="ImportOutcome"/> conversion. A real Copilot AlreadyLoaded replay must be
/// recognized by <see cref="ImportCommand.IsLifecycleOnlyRoutedReplay"/> (vendor-neutral) and
/// suppressed so it doesn't double-count on top of the classify-time AlreadyLoaded bucket.
/// </summary>
public class CopilotImportSourceImportTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server  = WireMockServer.Start();
    readonly TempDir        _tmp     = new();
    readonly string         _tempDir;

    public CopilotImportSourceImportTests() => _tempDir = _tmp.Path;

    /// <summary>Copilot's layout rooted at the throwaway dir, so the session lands under the real
    /// <c>session-state/</c> name discovery walks.</summary>
    CopilotPaths CopilotLayout => new(new UserHome(_tempDir), copilotHome: _tempDir);

    const string DashedSid = "11111111-2222-3333-4444-555555555555";

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string WriteSession() {
        var dir = Path.Combine(CopilotLayout.SessionStateDir, DashedSid);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "events.jsonl"), new[] {
            $$"""{"type":"session.start","data":{"sessionId":"{{DashedSid}}"},"id":"e1","timestamp":"2026-06-10T20:23:49.371Z","parentId":null}""",
            """{"type":"user.message","data":{"text":"hello"},"id":"e2","timestamp":"2026-06-10T20:23:50.000Z","parentId":"e1"}""",
            """{"type":"assistant.message","data":{"text":"hi there"},"id":"e3","timestamp":"2026-06-10T20:23:51.000Z","parentId":"e2"}"""
        });
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: /work/a\nname: proj\n");
        return _tempDir;
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_replay_is_a_no_op_suppressed_by_the_vendor_neutral_gate() {
        var root = WriteSession();

        // Server already covers every importable line → AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        foreach (var route in new[] { "/hooks/session-start/copilot", "/hooks/set-title", "/hooks/session-end/copilot" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new CopilotImportSource(Config.Root, CopilotLayout,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null));

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        // Copilot never touches a child/subagent stream — SentChildContent stays false.
        await Assert.That(result.SentChildContent).IsFalse();

        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();

        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsNull();
    }
}
