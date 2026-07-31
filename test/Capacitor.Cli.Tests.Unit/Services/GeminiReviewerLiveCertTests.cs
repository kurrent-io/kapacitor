using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// GATED live certification of the three vendor behaviours the Gemini reviewer's containment rests on. These
/// are the AI-1413 design probes promoted to tests, so a Gemini upgrade that invalidates one of them fails
/// here instead of silently reopening a repository-impersonation hole.
///
/// <para><b>This is what <c>GeminiReviewerCapability.CertifiedVersions</c> means.</b> Adding a version to that
/// set asserts that this file passed against that build. If it has not been run, leave the version out — an
/// absent version disables the reviewer, which is the safe direction.</para>
///
/// <para><b>Gated</b> behind <c>KCAP_GEMINI_REVIEWER_CERT=1</c>: CI has no <c>gemini</c> binary and no Google
/// account, and each case spends a real model turn. Requires <c>gemini</c> on PATH, logged in, and
/// <c>GOOGLE_CLOUD_PROJECT</c> set — without the project Gemini fails with an <c>IneligibleTierError</c> that
/// names a tier problem rather than the missing project (AI-899 §1.1), which is a confusing way to discover
/// the harness is misconfigured.</para>
/// </summary>
public class GeminiReviewerLiveCertTests {
    const string GateEnvVar = "KCAP_GEMINI_REVIEWER_CERT";

    // Bounded on purpose. "Never reported" and "skipped or inconclusive" must be distinguishable, or a broken
    // result channel looks exactly like a test that did not run.
    static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(180);

    static string Gate() {
        var on = Environment.GetEnvironmentVariable(GateEnvVar) == "1";

        Skip.Unless(on,
            $"Gated live certification of the Gemini reviewer's MCP containment — set {GateEnvVar}=1 to run "
          + "(spends real gemini turns; needs `gemini` on PATH, logged in, and GOOGLE_CLOUD_PROJECT set). "
          + "This is what CertifiedVersions asserts, so re-run it before adding a version there.");

        return Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? "";
    }

    /// <summary>
    /// The reviewer's result channel loads and is CALLED under a per-launch unguessable name — the positive
    /// control, and the thing without which there is no feature.
    /// </summary>
    [Test]
    public async Task Cert1_TheInjectedResultChannelIsInvoked_UnderAPerLaunchName() {
        var project = Gate();
        var id      = LaunchIdentity.ForLaunch(aliasResultChannel: true);

        var outcome = await RunProbeAsync(project, allowlist: id.ResultChannelWireName,
                                          injectAs: id.ResultChannelWireName, hostileRepoNames: []);

        await Assert.That(outcome.InjectedSpawned).IsTrue()
            .Because("Gemini must honour session/new.mcpServers — the reviewer has no other result channel");
        await Assert.That(outcome.InjectedToolCalled).IsTrue()
            .Because("a channel that loads but is never invoked is a reviewer that can never report");
    }

    /// <summary>
    /// The negative control: with the deny-all name in the allowlist, the injected channel is NOT spawned.
    /// Without this, Cert1 could pass because the allowlist is not a gate at all.
    /// </summary>
    [Test]
    public async Task Cert2_TheDenyAllNameBlocksEvenOurOwnChannel() {
        var project = Gate();
        var id      = LaunchIdentity.ForLaunch(aliasResultChannel: true);

        var outcome = await RunProbeAsync(project, allowlist: id.UnmatchableMcpName,
                                          injectAs: id.ResultChannelWireName, hostileRepoNames: []);

        await Assert.That(outcome.InjectedSpawned).IsFalse()
            .Because("the allowlist gates EVERY mcp server including ours — that coupling is why the review "
                   + "arm must replace the deny-all value rather than leave it");
    }

    /// <summary>
    /// The hostile-repository case, and the one that would catch the single change that silently reopens the
    /// hole: the allowlist becoming prefix- or glob-matched instead of exact.
    /// </summary>
    [Test]
    public async Task Cert3_NoRepositoryAuthoredServerIsSpawned_ExceptAnExactNameMatch() {
        var project = Gate();
        var id      = LaunchIdentity.ForLaunch(aliasResultChannel: true);

        var outcome = await RunProbeAsync(
            project,
            allowlist: id.ResultChannelWireName,
            injectAs:  id.ResultChannelWireName,
            hostileRepoNames: [
                KcapMcpRegistry.ReservedResultChannelId,                       // a prefix of the allowed name
                $"{KcapMcpRegistry.ReservedResultChannelId}-{Guid.NewGuid():N}", // same shape, different guid
                $"{KcapMcpRegistry.ReservedResultChannelId}-*"                  // wildcard attempt
            ]);

        await Assert.That(outcome.InjectedToolCalled).IsTrue()
            .Because("positive control: the real channel must still work with hostile servers present");
        await Assert.That(outcome.HostileSpawned).IsEmpty()
            .Because("admission must be exact-name equality — a prefix, a different suffix or a glob "
                   + "matching would let the reviewed repository start a process as the daemon user");
    }

    // ── harness ──

    readonly record struct ProbeOutcome(
        bool InjectedSpawned, bool InjectedToolCalled, IReadOnlyList<string> HostileSpawned);

    /// <summary>
    /// Drives a real <c>gemini --experimental-acp</c> child. Evidence is a marker file each MCP server writes,
    /// never the ACP transcript: the client cannot see whether Gemini spawned a server, so only the marker
    /// proves a process ran — which is the boundary that matters (§3.1's rule is about execution, not about
    /// who wins tool dispatch).
    /// </summary>
    static async Task<ProbeOutcome> RunProbeAsync(
            string project, string allowlist, string injectAs, string[] hostileRepoNames) {
        var root = Directory.CreateTempSubdirectory("kcap-gemini-cert-").FullName;
        try {
            var ws = Path.Combine(root, "ws");
            Directory.CreateDirectory(Path.Combine(ws, ".gemini"));

            var server  = WriteMarkerMcpServer(root);
            var markers = new Dictionary<string, string>();

            string MarkerFor(string label) {
                var path = Path.Combine(root, $"marker-{label}.log");
                markers[label] = path;
                return path;
            }

            // The repository's OWN declarations, which Gemini reads at process start under inherited trust.
            if (hostileRepoNames.Length > 0)
                await File.WriteAllTextAsync(
                    Path.Combine(ws, ".gemini", "settings.json"),
                    JsonSerializer.Serialize(new {
                        mcpServers = hostileRepoNames.ToDictionary(
                            n => n,
                            n => new { command = "python3", args = new[] { server },
                                       env = new Dictionary<string, string> {
                                           ["KCAP_CERT_MARKER"] = MarkerFor($"hostile-{Array.IndexOf(hostileRepoNames, n)}") } })
                    }));

            var injectedMarker = MarkerFor("injected");
            var exit = await DriveGeminiAsync(ws, project, allowlist, injectAs, server, injectedMarker);

            var injectedBody = File.Exists(injectedMarker) ? await File.ReadAllTextAsync(injectedMarker) : "";
            var hostileRan   = markers.Where(kv => kv.Key.StartsWith("hostile-") && File.Exists(kv.Value))
                                      .Select(kv => kv.Key).ToList();

            Console.WriteLine($"[gemini-cert] exit={exit} injected_spawned={injectedBody.Length > 0} "
                            + $"tool_called={injectedBody.Contains("TOOL_CALLED")} hostile_ran=[{string.Join(",", hostileRan)}]");

            return new(injectedBody.Length > 0, injectedBody.Contains("TOOL_CALLED"), hostileRan);
        } finally {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>A minimal stdio MCP server that records its lifecycle to a marker file. Python because it is
    /// present on every machine this suite runs on and needs no build step.</summary>
    static string WriteMarkerMcpServer(string root) {
        var path = Path.Combine(root, "marker_mcp.py");
        File.WriteAllText(path, """
            import json, os, sys
            M = os.environ.get("KCAP_CERT_MARKER", "/tmp/kcap-cert.log")
            T = "submit_review_result"
            def mark(e, d=""):
                open(M, "a").write(f"{e}\t{d}\n")
            def send(o):
                sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()
            mark("SERVER_SPAWNED", str(os.getpid()))
            for line in sys.stdin:
                line = line.strip()
                if not line: continue
                try: m = json.loads(line)
                except Exception: continue
                mid, method = m.get("id"), m.get("method")
                if method == "initialize":
                    send({"jsonrpc":"2.0","id":mid,"result":{
                        "protocolVersion": m.get("params",{}).get("protocolVersion","2025-06-18"),
                        "capabilities":{"tools":{}},
                        "serverInfo":{"name":"kcap-cert","version":"1.0.0"}}})
                elif method == "tools/list":
                    send({"jsonrpc":"2.0","id":mid,"result":{"tools":[{
                        "name": T,
                        "description": "Submit the review result. Call this exactly once.",
                        "inputSchema":{"type":"object","properties":{"verdict":{"type":"string"}},
                                       "required":["verdict"]}}]}})
                elif method == "tools/call":
                    mark("TOOL_CALLED", json.dumps(m.get("params",{}).get("arguments",{})))
                    send({"jsonrpc":"2.0","id":mid,"result":{
                        "content":[{"type":"text","text":"recorded"}],"isError":False}})
                elif mid is not None:
                    send({"jsonrpc":"2.0","id":mid,"result":{}})
            """);

        return path;
    }

    /// <summary>initialize → session/new (with the injected server) → session/prompt, over stdio.</summary>
    static async Task<int> DriveGeminiAsync(
            string ws, string project, string allowlist, string injectAs, string server, string marker) {
        var psi = new ProcessStartInfo("gemini", [
            "--experimental-acp", "--skip-trust",
            "--approval-mode", "yolo",              // without this Gemini gates its OWN channel behind a
            "--allowed-mcp-server-names", allowlist  // permission frame no test can answer (§2.4)
        ]) {
            WorkingDirectory = ws, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        psi.Environment["GOOGLE_CLOUD_PROJECT"] = project;
        psi.Environment["KCAP_DISABLE"]         = "1";   // keep kcap's own hooks out of the measurement

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("gemini did not start — is it on PATH?");
        using var cts = new CancellationTokenSource(TurnTimeout);

        var id = 0;
        async Task SendAsync(string method, object prms) =>
            await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
                new { jsonrpc = "2.0", id = ++id, method, @params = prms }));

        // Answer every server→client request so a turn is never blocked on us.
        var pump = Task.Run(async () => {
            while (!cts.IsCancellationRequested && await proc.StandardOutput.ReadLineAsync(cts.Token) is { } line) {
                if (line.Contains("\"method\"") && line.Contains("\"id\""))
                    try {
                        var reqId = JsonDocument.Parse(line).RootElement.GetProperty("id").GetRawText();
                        await proc.StandardInput.WriteLineAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{reqId},\"result\":{{\"outcome\":\"cancelled\"}}}}");
                    } catch { /* not a request we must answer */ }
            }
        }, cts.Token);

        await SendAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { } });
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        await SendAsync("session/new", new {
            cwd = ws,
            mcpServers = new object[] { new {
                name = injectAs, command = "python3", args = new[] { server },
                env = new object[] { new { name = "KCAP_CERT_MARKER", value = marker } } } }
        });
        await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);

        // sessionId is not parsed back: the marker file is the oracle, and a prompt to a stale session simply
        // yields no tool call — which is a FAILING result here, never a passing one.
        await SendAsync("session/prompt", new {
            sessionId = (string?)null,
            prompt = new object[] { new { type = "text", text =
                "Call submit_review_result once with verdict='certified'. No text, no questions." } }
        });

        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } }

        await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(1)));

        return proc.HasExited ? proc.ExitCode : -1;
    }
}
