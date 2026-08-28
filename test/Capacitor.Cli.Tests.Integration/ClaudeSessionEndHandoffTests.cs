using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Real-binary proof that the Claude SessionEnd hook returns inside Claude Code's 1.5 s grace
/// while the session-end still reaches the server. The server stalls <c>/hooks/session-end</c>
/// for longer than the hook is allowed to live, so the only way the POST can arrive is from a
/// process that outlived the hook.
/// </summary>
public class ClaudeSessionEndHandoffTests : IDisposable {
    static readonly TimeSpan ServerStall = TimeSpan.FromSeconds(3);

    [TempDir]         public required TempDir         Tmp     { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task Hook_returns_at_once_and_the_detached_continuation_posts_session_end() {
        var sid        = Guid.NewGuid().ToString("N");
        var transcript = Tmp.CreateFile("t.jsonl", """{"type":"user","uuid":"u1","message":{"role":"user","content":"hi"}}""" + "\n");

        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(ServerStall));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"processed":0}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "hook", "--claude", "--no-update-check");
        psi.WorkingDirectory        = Tmp.Path;
        psi.Environment["KCAP_URL"] = _server.Url!;

        var payload = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sid,
            ["transcript_path"] = transcript,
            ["cwd"]             = Tmp.Path,
            ["reason"]          = "exit",
        }.ToJsonString();

        var clock = Stopwatch.StartNew();
        using var hook = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap");
        await hook.StandardInput.WriteAsync(payload);
        hook.StandardInput.Close();
        var stdout = hook.StandardOutput.ReadToEndAsync();
        var stderr = hook.StandardError.ReadToEndAsync();
        await hook.WaitForExitAsync();
        clock.Stop();

        await Assert.That(hook.ExitCode).IsEqualTo(0).Because(await stderr);
        await Assert.That(await stdout).IsEmpty();
        // The ticket's bar: well inside the 1.5 s grace, on a session with a transcript to drain.
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(1)).Because($"stderr: {await stderr}");

        // The hook is gone; the continuation has today's 15 s budget and must wait out the stall.
        var posted = await WaitForSessionEndAsync(TimeSpan.FromSeconds(20));

        await Assert.That(posted).IsNotNull().Because("no /hooks/session-end reached the server");
        await Assert.That(posted!["session_id"]?.GetValue<string>()).IsEqualTo(sid);
        await Assert.That(posted["reason"]?.GetValue<string>()).IsEqualTo("exit");
        await Assert.That(posted["ended_at"]?.GetValue<string>()).IsNotNull();

        // The continuation's output lands in the session log, where the watcher's already goes.
        var log = Config.PathTo("logs", $"{sid}.log");
        await Assert.That(File.Exists(log)).IsTrue();
        await Assert.That(File.ReadAllTextShared(log)).Contains($"Inline drain for {sid}");
    }

    async Task<JsonNode?> WaitForSessionEndAsync(TimeSpan budget) {
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline) {
            var entries = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end").UsingPost());
            if (entries.Count > 0) return JsonNode.Parse(entries[0].RequestMessage.Body!);
            await Task.Delay(100);
        }

        return null;
    }
}
