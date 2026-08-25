using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Task 12 / BLOCKER-2: <c>LifecycleSpoolDrain.PostOnce</c>'s (HttpClient/production
/// overload's) job in Task 3 didn't carry <c>ClaudeHookCommand.ClaudePoster</c>'s
/// <c>generate_whats_done</c> side effect (spawning the what's-done generator). Once this drain
/// runs centrally (Task 12's Program.cs wiring + the daemon-periodic sweep) it can be the one that
/// delivers a spooled session-end for ANY vendor — the server's <c>generate_whats_done</c> signal is
/// vendor-agnostic (see <c>WatchCommand</c>'s parent-exit session-end path, which checks it for
/// whatever <c>vendor</c> it was invoked with) — so dropping the side effect there would silently
/// lose what's-done generation for non-Claude sessions replayed through the generic drain.
///
/// These tests exercise the production <c>RunAsync(HttpClient, ...)</c> overload directly against a
/// WireMock server, asserting the injected <c>onWhatsDoneRequested</c> callback fires exactly when it
/// should: on a successful TERMINAL (session-end) route whose response carries
/// <c>generate_whats_done: true</c> — for Claude's literal <c>"session-end"</c> route AND for a
/// vendor-suffixed route like <c>"session-end/kiro"</c> — and never otherwise.
/// </summary>
public class LifecycleSpoolDrainWhatsDoneTests : IDisposable {
    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    const string Sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public void Dispose() => _server.Stop();

    [Test]
    public async Task fires_for_a_non_claude_vendor_session_end_with_generate_whats_done() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"));
        life.Append(Sid, "session-end/kiro", $$"""{"session_id":"{{Sid}}"}""");

        var calls = new List<(string SessionId, string Vendor)>();
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (sid, vendor) => calls.Add((sid, vendor)));

        await Assert.That(calls).IsEquivalentTo([(Sid, "kiro")]);
    }

    [Test]
    public async Task fires_for_claudes_literal_session_end_route_defaulting_to_claude_vendor() {
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"));
        life.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");

        var calls = new List<(string SessionId, string Vendor)>();
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (sid, vendor) => calls.Add((sid, vendor)));

        await Assert.That(calls).IsEquivalentTo([(Sid, "claude")]);
    }

    [Test]
    public async Task does_not_fire_when_generate_whats_done_is_absent() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"));
        life.Append(Sid, "session-end/kiro", $$"""{"session_id":"{{Sid}}"}""");

        var fired = false;
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (_, _) => fired = true);

        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task does_not_fire_for_a_non_terminal_route_even_with_generate_whats_done_true() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"));
        life.Append(Sid, "session-start/kiro", $$"""{"session_id":"{{Sid}}"}""");

        var fired = false;
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (_, _) => fired = true);

        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task omitting_the_callback_never_throws() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"));
        life.Append(Sid, "session-end/kiro", $$"""{"session_id":"{{Sid}}"}""");

        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // No onWhatsDoneRequested — the daemon's periodic drain and callers that don't need
        // the side effect may omit it; this must never throw.
        await LifecycleSpoolDrain.RunAsync(Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token);

        await Assert.That(life.HasBacklog(Sid)).IsFalse();
    }
}
