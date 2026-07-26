using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC handshake tests for <c>kcap mcp analytics</c> — mirrors the
/// memory integration harness: spawn the real binary, drive initialize/tools-list over
/// stdio, and pin the server info + the call-schema-first routing cue.
/// </summary>
public class McpAnalyticsServerTests : IDisposable {
    readonly WireMockServer _server           = WireMockServer.Start();
    readonly string         _cfgDir           = Path.Combine(Path.GetTempPath(), $"kcap-mcp-an-cfg-{Guid.NewGuid():N}");
    readonly string         _cwdDir           = Path.Combine(Path.GetTempPath(), $"kcap-mcp-an-cwd-{Guid.NewGuid():N}");
    readonly List<Process>  _spawnedProcesses = [];

    public McpAnalyticsServerTests() {
        Directory.CreateDirectory(_cfgDir);
        Directory.CreateDirectory(_cwdDir);
    }

    public void Dispose() {
        foreach (var p in _spawnedProcesses) {
            try {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.Dispose();
            } catch {
                // best-effort cleanup
            }
        }

        _server.Stop();
        try { Directory.Delete(_cfgDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cwdDir, recursive: true); } catch { /* best effort */ }
    }

    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(McpAnalyticsServerTests).Assembly.Location)!;
        var binDir      = Path.GetDirectoryName(asmDir)!;
        var config      = Path.GetFileName(binDir);
        var testBin     = Path.GetDirectoryName(binDir)!;
        var testProjDir = Path.GetDirectoryName(testBin)!;
        var testRoot    = Path.GetDirectoryName(testProjDir)!;
        var repoRoot    = Path.GetDirectoryName(testRoot)!;
        var binaryName  = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";

        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }

    Process SpawnMcpServer(string provider = "None") {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"provider":"{{provider}}"}"""));

        var binary = GetCliBinaryPath();
        if (!File.Exists(binary)) {
            throw new FileNotFoundException(
                $"kcap binary not found at {binary}. Build it first: " +
                "dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj",
                binary
            );
        }

        var psi = new ProcessStartInfo(binary, "mcp analytics") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = _cwdDir,
            Environment = {
                ["KCAP_URL"]        = _server.Url!,
                ["KCAP_CONFIG_DIR"] = _cfgDir
            }
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);

        return process;
    }

    [Test]
    public async Task Initialize_returns_kcap_analytics_server_info_with_instructions() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-analytics");
            await Assert.That(result["instructions"]?.GetValue<string>()).IsNotNull();
            await Assert.That(result["instructions"]!.GetValue<string>()).Contains("governed read-only SQL");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_exposes_schema_first_routing_cue() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();
            await Assert.That(tools!.Select(t => t?["name"]?.GetValue<string>()).ToArray())
                .IsEquivalentTo(new[] { "get_analytics_schema", "query_analytics" });

            // Hard gate: agents must be steered to fetch the schema before writing SQL.
            var schemaDesc = tools!.First(t => t?["name"]?.GetValue<string>() == "get_analytics_schema")!["description"]!.GetValue<string>();
            await Assert.That(schemaDesc).Contains("before writing SQL");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    static async Task<JsonObject> SendRequest(Process proc, JsonObject request, TimeSpan? timeout = null) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        var line      = await proc.StandardOutput.ReadLineAsync(cts.Token);

        if (line is null) {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"MCP server closed stdout without responding. Stderr: {stderr}");
        }

        return JsonNode.Parse(line)?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse response as JSON object: {line}");
    }

    static async Task ShutdownAsync(Process proc) {
        try { proc.StandardInput.Close(); } catch { /* already closed */ }
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);
        } catch {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    static JsonObject InitializeRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "initialize",
        ["params"]  = new JsonObject()
    };

    static JsonObject ToolsListRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/list",
        ["params"]  = new JsonObject()
    };
}
