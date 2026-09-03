using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
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
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    readonly WireMockServer _server           = WireMockServer.Start();
    readonly List<Process>  _spawnedProcesses = [];

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
    }

    Process SpawnMcpServer(string provider = "None", string? workingDirectory = null) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"provider":"{{provider}}"}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", "analytics");
        psi.WorkingDirectory = workingDirectory ?? Tmp.Path;
        psi.Environment["KCAP_URL"] = _server.Url!;

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
            await Assert.That(tools!.Select(t => t?["name"]?.GetValue<string>()!).ToArray())
                .IsEquivalentTo(new[] { "get_analytics_schema", "query_analytics" });

            // Hard gate: agents must be steered to fetch the schema before writing SQL.
            var schemaDesc = tools!.First(t => t?["name"]?.GetValue<string>() == "get_analytics_schema")!["description"]!.GetValue<string>();
            await Assert.That(schemaDesc).Contains("before writing SQL");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A repository whose <c>origin</c> points at GitHub, handed to the server as its working
    /// directory so the cwd-repo scope resolves. No commit is needed: <c>git branch --show-current</c>
    /// reads the symbolic HEAD ref at zero commits.
    /// </summary>
    static GitRepo CwdRepo(string owner, string repoName) {
        var repo = GitRepo.Create();
        repo.AddRemote($"https://github.com/{owner}/{repoName}.git");
        return repo;
    }

    /// <summary>
    /// The server is spawned for every agent session, so the working directory's repository is
    /// resolved by the first tool call, not at startup. Detection is the only startup-time writer
    /// under the config root's cache directory, so its absence after the handshake proves detection
    /// never ran; the query that follows then scopes to the repo hash resolved on demand.
    /// </summary>
    [Test]
    public async Task Repository_is_resolved_by_the_first_tool_call_not_at_startup() {
        using var repo = CwdRepo("acme", "widget");

        _server.Given(Request.Create().WithPath("/api/analytics/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"rows":[]}"""));

        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            await SendRequest(proc, InitializeRequest(1));
            await SendRequest(proc, ToolsListRequest(2));
            await Assert.That(Directory.Exists(Config.Root.Path("cache"))).IsFalse();

            await SendRequest(proc, ToolsCallRequest(3, "query_analytics", new JsonObject { ["sql"] = "select 1" }));

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/analytics/query").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);
            await Assert.That(hits[0].RequestMessage.Body ?? "")
                .Contains(RepoHashHelper.ComputeRepoHash("acme", "widget"));
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

    static JsonObject ToolsCallRequest(int id, string name, JsonObject arguments) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/call",
        ["params"]  = new JsonObject { ["name"] = name, ["arguments"] = arguments }
    };
}
