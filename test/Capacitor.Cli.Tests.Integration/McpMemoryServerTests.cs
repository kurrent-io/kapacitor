using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC handshake tests for <c>kcap mcp memory</c>. Memory previously had
/// only unit coverage of its URL/body builders (<c>McpMemoryServerTests</c> in the unit project);
/// this adds a spawned-process handshake so the server-level <c>instructions</c> preamble
/// and the <c>search_memories</c> routing cue can't be silently dropped. Mirrors the sessions
/// integration harness.
/// </summary>
public class McpMemoryServerTests : IDisposable {
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

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", "memory");
        psi.WorkingDirectory = workingDirectory ?? Tmp.Path;
        psi.Environment["KCAP_URL"] = _server.Url!;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);

        return process;
    }

    [Test]
    public async Task Initialize_returns_kcap_memory_server_info_with_instructions() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-memory");
            // server-level instructions preamble.
            await Assert.That(result["instructions"]?.GetValue<string>()).IsNotNull();
            await Assert.That(result["instructions"]!.GetValue<string>()).IsNotEmpty();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_search_memories_carries_the_routing_cue() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();

            var searchDesc = tools!.First(t => t?["name"]?.GetValue<string>() == "search_memories")!["description"]!.GetValue<string>();
            // Hard gate: the comparative routing cue must be present.
            await Assert.That(searchDesc).Contains("before assuming there's no prior art");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A repository whose <c>origin</c> points at GitHub, handed to the server as its working
    /// directory so the cwd-repo pin resolves. No commit is needed: <c>git branch --show-current</c>
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
    /// never ran; the search that follows then carries the repo hash resolved on demand.
    /// </summary>
    [Test]
    public async Task Repository_is_resolved_by_the_first_tool_call_not_at_startup() {
        using var repo = CwdRepo("acme", "widget");

        _server.Given(Request.Create().WithPath("/api/memories/search").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[]"));

        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            await SendRequest(proc, InitializeRequest(1));
            await SendRequest(proc, ToolsListRequest(2));
            await Assert.That(Directory.Exists(Config.Root.Path("cache"))).IsFalse();

            await SendRequest(proc, ToolsCallRequest(3, "search_memories", new JsonObject { ["query"] = "anything" }));

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/memories/search").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(1);
            await Assert.That(hits[0].RequestMessage.RawQuery ?? "")
                .Contains($"repo={RepoHashHelper.ComputeRepoHash("acme", "widget")}");
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
