using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The decision path proven end to end through the real spawned <c>kcap</c> binary: payload on
/// stdin, decision on stdout, policy event on the wire.
///
/// <para>The emitter is spool-first — <c>PolicyDecisionEmitter</c> only appends to the hook spool,
/// never posts inline, because the decision seam runs ahead of client creation and must answer
/// without the network. So the deciding spawn alone never reaches WireMock; a second, unrelated
/// hook invocation for the same session (<c>Stop</c>) is what drains the backlog and delivers it —
/// exercising <c>ClaudeHookCommand.HandleCore</c>'s own pre-dispatch spool drain, which runs on
/// every client-backed Claude hook and is not subject to the cross-process drain throttle.</para>
/// </summary>
public class PolicyHookDecisionTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    readonly List<Process> _spawned = [];
    public void Dispose() { _server.Stop(); foreach (var p in _spawned) { try { p.Kill(); } catch { } p.Dispose(); } }

    const string Sid = "3f8a2b1c4d5e46f7a8b9c0d1e2f3a4b5";

    void StubServer() {
        // Deterministic "no auth" discovery rather than relying on WireMock's default response to
        // an unmapped GET — this is the same explicit stub PermissionRequestPolicySeamTests uses.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("""{"provider":"None"}"""));
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
    }

    async Task<(int ExitCode, string Stdout)> RunHookAsync(string payload) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "hook", "--claude", "--no-update-check");
        psi.WorkingDirectory = Config.Directory;
        psi.Environment["KCAP_URL"] = _server.Url!;
        psi.Environment["KCAP_RENDERED_AGENT"] = "0";
        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _spawned.Add(process);
        try { await process.StandardInput.WriteAsync(payload); }
        catch (IOException) { }
        finally { try { process.StandardInput.Close(); } catch (IOException) { } }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, stdout);
    }

    [Test]
    public async Task Permission_request_is_denied_end_to_end() {
        StubServer();
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var payload = $$"""
            {"hook_event_name":"PermissionRequest","session_id":"{{Sid}}","tool_name":"Bash",
             "tool_input":{"command":"git push --force"},"cwd":"{{Config.Directory}}"}
            """;
        var (exit, stdout) = await RunHookAsync(payload);
        await Assert.That(exit).IsEqualTo(0);
        var decision = JsonNode.Parse(stdout)!["hookSpecificOutput"]!["decision"]!;
        await Assert.That(decision["behavior"]!.GetValue<string>()).IsEqualTo("deny");

        // Force the drain: delete the cross-process throttle stamp so a second spawn's
        // pre-dispatch drain pass (Program.cs's centralized one) does not skip as "too recent".
        File.Delete(Config.Root.Path("spool", ".last-drain"));
        var stopPayload = $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","cwd":"{{Config.Directory}}"}""";
        var (stopExit, _) = await RunHookAsync(stopPayload);
        await Assert.That(stopExit).IsEqualTo(0);

        var events = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(events.Count).IsEqualTo(1);
        var snapshots = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-snapshot").UsingPost());
        await Assert.That(snapshots.Count).IsEqualTo(1);

        // Route + count alone would pass for a wrong seam, a mangled session_id, or a swapped
        // body — assert the delivered content, not just that something arrived.
        var decisionEvent = JsonNode.Parse(events[0].RequestMessage.Body!)!;
        await Assert.That(decisionEvent["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(decisionEvent["seam"]!.GetValue<string>()).IsEqualTo("claude_permission_request");
        await Assert.That(decisionEvent["effective_outcome"]!.GetValue<string>()).IsEqualTo("deny");

        var snapshotUpload = JsonNode.Parse(snapshots[0].RequestMessage.Body!)!;
        await Assert.That(snapshotUpload["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        var snapshotId = snapshotUpload["snapshot_id"]!.GetValue<string>();
        await Assert.That(snapshotId).IsNotEmpty();

        // The decision must name the snapshot that was actually uploaded, not a stale or
        // mismatched one.
        await Assert.That(decisionEvent["snapshot_id"]!.GetValue<string>()).IsEqualTo(snapshotId);
    }

    [Test]
    public async Task Pre_tool_use_allows_a_fully_covered_command() {
        StubServer();
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var payload = $$"""
            {"hook_event_name":"PreToolUse","session_id":"{{Sid}}","tool_name":"Bash",
             "tool_input":{"command":"git status"},"cwd":"{{Config.Directory}}"}
            """;
        var (exit, stdout) = await RunHookAsync(payload);
        await Assert.That(exit).IsEqualTo(0);
        var hso = JsonNode.Parse(stdout)!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("allow");
    }

    [Test]
    public async Task No_policy_hook_stays_silent() {
        StubServer();
        var payload = $$"""
            {"hook_event_name":"PreToolUse","session_id":"{{Sid}}","tool_name":"Bash",
             "tool_input":{"command":"anything"},"cwd":"{{Config.Directory}}"}
            """;
        var (exit, stdout) = await RunHookAsync(payload);
        await Assert.That(exit).IsEqualTo(0);
        // No policy is loaded (no approvals.yaml written) — the seam returns without writing
        // anything. DoesNotContain rather than exact-empty: PreToolUse never reaches the
        // client-backed lane that could add an auth/systemMessage line, but this stays honest
        // about what it actually pins if that ever changes.
        await Assert.That(stdout).DoesNotContain("hookSpecificOutput");
    }
}
