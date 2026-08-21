using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// GATED live confirmation of the app-server transport + DTO layer against the REAL <c>codex
/// app-server</c> binary — the wire-shape parity a fake peer cannot prove. Drives the no-model
/// handshake surface (<c>initialize</c> → <c>hooks/list</c> → <c>thread/start</c>) through the
/// production <see cref="CodexAppServerConnection"/> and asserts the responses parse into the exact
/// shapes <see cref="CodexAppServerHostedAgentRuntime"/> reads. It spends NO model turn, so it needs
/// no auth, and runs against an ISOLATED <c>CODEX_HOME</c> so it never touches the operator's real
/// Codex state, hooks, or the running daemon.
///
/// <para><b>Gated</b> behind <c>KCAP_CODEX_APPSERVER_SMOKE=1</c>: shared CI has no <c>codex</c>
/// binary. Re-run it against a new Codex build before recommending that build — a floor-breaking
/// schema drift fails here instead of silently at launch.</para>
/// </summary>
public class CodexAppServerLiveSmokeTests {
    const string GateEnvVar = "KCAP_CODEX_APPSERVER_SMOKE";
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    static void Gate() =>
        Skip.Unless(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"Gated live confirmation of the codex app-server wire layer — set {GateEnvVar}=1 to run "
          + "(needs `codex` on PATH; spends no model turn; runs in an isolated CODEX_HOME).");

    [Test]
    public async Task Real_app_server_initialize_hookslist_and_thread_start_round_trip() {
        Gate();

        using var codexHome = new TempDir();
        using var cwd       = new TempDir();

        var psi = new ProcessStartInfo("codex", ["app-server"]) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.Environment["CODEX_HOME"] = codexHome.Path;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("codex app-server did not start.");
        await using var child = new AcpChildProcess(process, NullLogger<AcpChildProcess>.Instance, vendor: "codex");
        await using var conn = new CodexAppServerConnection(
            process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            NullLogger<CodexAppServerConnection>.Instance);

        using var cts = new CancellationTokenSource();
        var runLoop = conn.RunAsync(cts.Token);

        try {
            // initialize — clientInfo is required; response carries userAgent.
            var init = await conn.RequestAsync("initialize", Element(new JsonObject {
                ["clientInfo"] = new JsonObject { ["name"] = "kcap-daemon", ["version"] = "0.146.0" },
            }), cts.Token).WaitAsync(HangGuard);
            await Assert.That(init.ValueKind).IsEqualTo(JsonValueKind.Object);

            // hooks/list — the runtime flattens data[].hooks[] into CodexHookEntry; assert the shape
            // parses (an isolated CODEX_HOME has no kcap hooks, so the set is empty/managed).
            var hooks = await conn.RequestAsync("hooks/list", Element(new JsonObject {
                ["cwds"] = new JsonArray((JsonNode?) cwd.Path),
            }), cts.Token).WaitAsync(HangGuard);
            await Assert.That(hooks.TryGetProperty("data", out var data)).IsTrue();
            await Assert.That(data.ValueKind).IsEqualTo(JsonValueKind.Array);

            // thread/start with the reviewer posture — no model call happens here, so no auth needed.
            var start = await conn.RequestAsync("thread/start", Element(new JsonObject {
                ["cwd"]               = cwd.Path,
                ["sandbox"]           = CodexAppServerPosture.RenderSandboxMode("read-only"),
                ["approvalPolicy"]    = CodexAppServerPosture.RenderApprovalPolicy("never"),
                ["approvalsReviewer"] = CodexAppServerPosture.ApprovalsReviewer,
            }), cts.Token).WaitAsync(HangGuard);

            // The exact fields the runtime reads: thread.id (deterministic identity) + resolved model.
            await Assert.That(start.TryGetProperty("thread", out var thread)).IsTrue();
            await Assert.That(thread.GetProperty("id").GetString()).IsNotNull();
            await Assert.That(thread.GetProperty("sessionId").GetString()).IsEqualTo(thread.GetProperty("id").GetString());
            await Assert.That(start.GetProperty("model").GetString()).IsNotNull();
        } finally {
            await cts.CancelAsync();
            await child.TerminateAsync(TimeSpan.FromSeconds(5));
            try { await runLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* shutdown */ }
        }
    }

    static JsonElement Element(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}
