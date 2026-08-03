using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Integration;

[NotInParallel]
public class McpReviewContextServerIntegrationTests {
    [Test]
    public async Task Daemon_context_mode_starts_without_backend_and_performs_one_exact_get() {
        var token = "0123456789abcdef0123456789abcdef";
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var capability = $"http://127.0.0.1:{port}/{token}/review-context/workspace-mcp-configs";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");
        listener.Start();
        var manifest = "{\"schemaVersion\":1,\"entries\":[]}";
        var configDir = Directory.CreateTempSubdirectory("kcap-context-config-").FullName;
        var requests = 0;
        var serve = Task.Run(async () => {
            var context = await listener.GetContextAsync();
            Interlocked.Increment(ref requests);
            await Assert.That(context.Request.HttpMethod).IsEqualTo("GET");
            await Assert.That(context.Request.RawUrl)
                .IsEqualTo($"/{token}/review-context/workspace-mcp-configs");
            var bytes = Encoding.UTF8.GetBytes(manifest);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var process = Spawn(capability, configDir);
        try {
            var initialize = await Send(process, new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JsonObject()
            });
            await Assert.That(initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>())
                .IsEqualTo("kcap-review-context");
            await Assert.That(requests).IsEqualTo(0);

            var listed = await Send(process, new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list",
                ["params"] = new JsonObject()
            });
            var tools = listed["result"]!["tools"]!.AsArray();
            await Assert.That(tools.Count).IsEqualTo(1);
            await Assert.That(tools[0]!["name"]!.GetValue<string>())
                .IsEqualTo("get_branch_authored_mcp_configs");
            await Assert.That(requests).IsEqualTo(0);

            var called = await Send(process, new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call",
                ["params"] = new JsonObject {
                    ["name"] = "get_branch_authored_mcp_configs",
                    ["arguments"] = new JsonObject()
                }
            });
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(called["result"]!["content"]![0]!["text"]!.GetValue<string>())
                .IsEqualTo(manifest);
            await Assert.That(requests).IsEqualTo(1);
            await Assert.That(Directory.GetFileSystemEntries(configDir)).IsEmpty()
                .Because("context mode must bypass auth/config and update-check state");
        } finally {
            try { process.StandardInput.Close(); } catch { }
            if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
            try { Directory.Delete(configDir, true); } catch { }
        }
    }

    static Process Spawn(string capability, string configDir) {
        var binary = CliBinary();
        var info = new ProcessStartInfo(binary, "mcp review") {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = {
                ["KCAP_REVIEW_CONTEXT_MODE"] = "1",
                ["KCAP_REVIEW_CONTEXT_URL"] = capability,
                ["KCAP_URL"] = "not-a-backend-url",
                ["KCAP_CONFIG_DIR"] = configDir
            }
        };
        return Process.Start(info) ?? throw new InvalidOperationException("Failed to start kcap");
    }

    static async Task<JsonObject> Send(Process process, JsonObject request) {
        await process.StandardInput.WriteLineAsync(request.ToJsonString());
        await process.StandardInput.FlushAsync();
        var line = await process.StandardOutput.ReadLineAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        if (line is null)
            throw new InvalidOperationException(
                "MCP process exited: " + await process.StandardError.ReadToEndAsync());
        return JsonNode.Parse(line)!.AsObject();
    }

    static string CliBinary() {
        var asmDir = Path.GetDirectoryName(typeof(McpReviewContextServerIntegrationTests).Assembly.Location)!;
        var binDir = Path.GetDirectoryName(asmDir)!;
        var configuration = Path.GetFileName(binDir);
        var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(binDir)!)!;
        var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(projectDir)!)!;
        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", configuration,
            "net10.0", OperatingSystem.IsWindows() ? "kcap.exe" : "kcap");
    }
}
