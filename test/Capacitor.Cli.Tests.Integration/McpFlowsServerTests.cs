using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC tests for <c>kcap mcp flows</c>.
/// Spawns the freshly-built CLI binary, points it at a WireMock-stubbed
/// Capacitor server (via <c>KCAP_URL</c>), seeds an isolated config
/// directory (via <c>KCAP_CONFIG_DIR</c>) so token/profile state never
/// leaks between tests, and asserts on the wire-level JSON-RPC envelopes
/// the server emits plus the HTTP calls WireMock observed.
/// </summary>
public class McpFlowsServerTests : IDisposable {
    readonly WireMockServer _server           = WireMockServer.Start();
    readonly string         _cfgDir           = Path.Combine(Path.GetTempPath(), $"kcap-flows-cfg-{Guid.NewGuid():N}");
    readonly string         _cwdDir           = Path.Combine(Path.GetTempPath(), $"kcap-flows-cwd-{Guid.NewGuid():N}");
    readonly List<Process>  _spawnedProcesses = [];

    public McpFlowsServerTests() {
        Directory.CreateDirectory(_cfgDir);
        Directory.CreateDirectory(_cwdDir);

        // Initialize a git repo in _cwdDir so GitRepository.FindRoot returns a path,
        // and create a subdirectory to verify requesting_cwd vs requesting_repo_root.
        InitGitRepo(_cwdDir);
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

    /// <summary>
    /// Initialises a minimal git repo (git init + git remote add origin) so
    /// <c>GitRepository.FindRoot</c> finds a root and <c>RepositoryDetection</c>
    /// can parse an owner/name from the remote URL.
    /// </summary>
    static void InitGitRepo(string dir) {
        RunGit(dir, "init");
        RunGit(dir, "remote add origin https://github.com/test-owner/test-repo.git");
    }

    static void RunGit(string cwd, string args) {
        using var p = Process.Start(new ProcessStartInfo("git", args) {
            WorkingDirectory      = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        })!;
        p.WaitForExit(5000);
    }

    /// <summary>
    /// Resolves the path of the built `kcap` CLI binary in the source tree.
    /// Mirrors the approach in <see cref="McpSessionsServerTests"/>.
    /// </summary>
    static string GetCliBinaryPath() {
        var asmDir      = Path.GetDirectoryName(typeof(McpFlowsServerTests).Assembly.Location)!;
        var binDir      = Path.GetDirectoryName(asmDir)!;
        var config      = Path.GetFileName(binDir);
        var testBin     = Path.GetDirectoryName(binDir)!;
        var testProjDir = Path.GetDirectoryName(testBin)!;
        var testRoot    = Path.GetDirectoryName(testProjDir)!;
        var repoRoot    = Path.GetDirectoryName(testRoot)!;
        var binaryName  = OperatingSystem.IsWindows() ? "kcap.exe" : "kcap";
        return Path.Combine(repoRoot, "src", "Capacitor.Cli", "bin", config, "net10.0", binaryName);
    }

    /// <summary>
    /// Spawns <c>kcap mcp flows</c> as a child process with WireMock as the backend.
    /// <paramref name="urlOverride"/> replaces the WireMock URL (used to exercise the
    /// invalid-server_url path).
    /// </summary>
    Process SpawnMcpServer(string provider = "None", string? workingDirectory = null, string? urlOverride = null) {
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

        var psi = new ProcessStartInfo(binary, "mcp flows") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? _cwdDir,
            Environment = {
                ["KCAP_URL"]        = urlOverride ?? _server.Url!,
                ["KCAP_CONFIG_DIR"] = _cfgDir
            }
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
    }

    /// <summary>
    /// Spawns the server with KCAP_SESSION_ID set so requester context includes a session ID.
    /// </summary>
    Process SpawnMcpServerWithSession(string sessionId, string provider = "None", string? workingDirectory = null) {
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

        var psi = new ProcessStartInfo(binary, "mcp flows") {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDirectory ?? _cwdDir,
            Environment = {
                ["KCAP_URL"]          = _server.Url!,
                ["KCAP_CONFIG_DIR"]   = _cfgDir,
                ["KCAP_SESSION_ID"]   = sessionId
            }
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
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
        ["params"]  = new JsonObject {
            ["name"]      = name,
            ["arguments"] = arguments
        }
    };

    [Test]
    public async Task Initialize_returns_kcap_flows_server_info() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-flows");
            await Assert.That(result["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");
            // flows deliberately gets NO server-level instructions (we don't want more
            // routing to a paid hosted reviewer) — the field must be omitted.
            await Assert.That(result["instructions"]).IsNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Handshake probe: clients that send <c>resources/list</c> / <c>prompts/list</c> /
    /// <c>ping</c> before treating the server as ready must get empty-but-successful responses,
    /// not <c>-32601 Method not found</c> — and the negotiated protocolVersion must echo back a
    /// client-requested version we support, not always the hardcoded baseline.
    /// </summary>
    [Test]
    public async Task ResourcesList_and_PromptsList_return_empty_not_method_not_found() {
        using var proc = SpawnMcpServer();
        try {
            var init = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 1,
                ["method"]  = "initialize",
                ["params"]  = new JsonObject { ["protocolVersion"] = "2025-06-18" }
            });
            await Assert.That(init["result"]?["protocolVersion"]?.GetValue<string>()).IsEqualTo("2025-06-18");

            var resources = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 2,
                ["method"]  = "resources/list"
            });
            await Assert.That(resources["error"]).IsNull();
            await Assert.That(resources["result"]?["resources"]?.AsArray()?.Count).IsEqualTo(0);

            var prompts = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 3,
                ["method"]  = "prompts/list"
            });
            await Assert.That(prompts["error"]).IsNull();
            await Assert.That(prompts["result"]?["prompts"]?.AsArray()?.Count).IsEqualTo(0);

            var ping = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 4,
                ["method"]  = "ping"
            });
            await Assert.That(ping["error"]).IsNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Malformed-initialize survival probe: a client sending a non-string
    /// <c>protocolVersion</c> (e.g. a bare JSON number) must not crash <c>McpProtocol.NegotiateVersion</c> —
    /// the initialize dispatch arm has no try/catch, so an uncaught exception there would kill the
    /// whole stdio server. The server must fall back to the baseline version and stay responsive.
    /// </summary>
    [Test]
    public async Task Initialize_with_non_string_protocol_version_falls_back_and_server_survives() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 1,
                ["method"]  = "initialize",
                ["params"]  = new JsonObject { ["protocolVersion"] = 2025 }
            });

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");

            // Server survived the malformed request — a follow-up still gets a response.
            var again = await SendRequest(proc, ToolsListRequest(2));
            await Assert.That(again["result"]?["tools"]).IsNotNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_returns_eight_flow_tools() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();
            await Assert.That(tools!.Count).IsEqualTo(8);

            var names = tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
            await Assert.That(names.Contains("start_review_flow")).IsTrue();
            await Assert.That(names.Contains("submit_review_round")).IsTrue();
            await Assert.That(names.Contains("get_review_flow_status")).IsTrue();
            await Assert.That(names.Contains("close_review_flow")).IsTrue();
            await Assert.That(names.Contains("start_flow")).IsTrue();
            await Assert.That(names.Contains("send_to_participant")).IsTrue();
            await Assert.That(names.Contains("get_flow_status")).IsTrue();
            await Assert.That(names.Contains("close_flow")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Pins the four review-tool schemas byte-stably: definition_id/participant/message must
    /// NEVER leak into these schemas — old clients (and old skills) depend on the exact
    /// property/required sets that shipped before the generic tools were added (D-b).
    /// </summary>
    [Test]
    public async Task Review_tool_schemas_are_unchanged() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));
            var tools    = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();

            var byName = tools!.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);

            await AssertSchema(
                byName["start_review_flow"],
                properties: ["kind", "target_kind", "target_ref", "target_title", "context", "instructions", "mode", "vendor", "model"],
                required:   ["kind", "target_kind", "target_ref", "target_title", "context"]
            );

            await AssertSchema(
                byName["submit_review_round"],
                properties: ["flow_run_id", "context", "instructions"],
                required:   ["flow_run_id", "context"]
            );

            await AssertSchema(
                byName["get_review_flow_status"],
                properties: ["flow_run_id"],
                required:   ["flow_run_id"]
            );

            await AssertSchema(
                byName["close_review_flow"],
                properties: ["flow_run_id"],
                required:   ["flow_run_id"]
            );
        } finally {
            await ShutdownAsync(proc);
        }
    }

    static async Task AssertSchema(JsonNode tool, string[] properties, string[] required) {
        var schema = tool["inputSchema"]?.AsObject();
        await Assert.That(schema).IsNotNull();

        var propNames = schema!["properties"]?.AsObject().Select(kv => kv.Key).ToHashSet() ?? [];
        var reqNames  = schema["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToHashSet() ?? [];

        await Assert.That(propNames.SetEquals(properties)).IsTrue();
        await Assert.That(reqNames.SetEquals(required)).IsTrue();
    }

    /// <summary>
    /// Generic alias for start_review_flow: definition_id maps onto the wire "kind" field so the
    /// server (which treats kind == definition id, phase C) doesn't need to know about
    /// the generic tool name at all.
    /// </summary>
    [Test]
    public async Task Start_flow_posts_kind_from_definition_id() {
        const string flowRunId = "flow-generic-1";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-1",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "generic flow result",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["definition_id"] = "my-custom-flow",
                ["target_kind"]   = "pr",
                ["target_ref"]    = "https://github.com/x/y/pull/1",
                ["target_title"]  = "My PR",
                ["context"]       = "please look at this"
            };

            var response = await SendRequest(proc, ToolsCallRequest(50, "start_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("generic flow result")).IsTrue();

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["kind"]?.GetValue<string>()).IsEqualTo("my-custom-flow");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// send_to_participant is the generic alias for submit_review_round: "message" maps onto the
    /// wire "context" field and "participant" is a new field the server validates against the
    /// flow definition (Phase D flows have a single participant: "reviewer").
    /// </summary>
    [Test]
    public async Task Send_to_participant_posts_participant_and_message_as_context() {
        const string flowRunId = "flow-generic-2";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-2",
              "round_number": 2,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "round two",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["participant"] = "reviewer",
                ["message"]     = "ctx2"
            };

            var response = await SendRequest(proc, ToolsCallRequest(51, "send_to_participant", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["context"]?.GetValue<string>()).IsEqualTo("ctx2");
            await Assert.That(bodyNode["participant"]?.GetValue<string>()).IsEqualTo("reviewer");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// send_to_participant declares an optional "async" arg (kept for symmetry with
    /// submit_review_round's own Async field) — pin that it actually flows through onto the
    /// wire. Stubs a terminal (non-"running") POST response so ResolveRoundResultAsync takes
    /// the no-poll path and no GET calls happen.
    /// </summary>
    [Test]
    public async Task Send_to_participant_async_false_posts_async_false() {
        const string flowRunId = "flow-generic-async-false";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-2",
              "round_number": 2,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "sync round result",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["participant"] = "reviewer",
                ["message"]     = "ctx2",
                ["async"]       = false
            };

            var response = await SendRequest(proc, ToolsCallRequest(51, "send_to_participant", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["async"]?.GetValue<bool>()).IsFalse();
            await Assert.That(bodyNode["context"]?.GetValue<string>()).IsEqualTo("ctx2");
            await Assert.That(bodyNode["participant"]?.GetValue<string>()).IsEqualTo("reviewer");

            // No polling should have happened — the POST response was already terminal.
            var getHits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet());
            await Assert.That(getHits.Count).IsEqualTo(0);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A non-boolean JSON "async" (e.g. an LLM caller passing the string "yes") must NOT crash
    /// the request with an uncaught GetValue&lt;bool&gt;() exception — it must surface as a clean
    /// isError:true tool result, and the stdio loop must stay alive for the next request (Qodo
    /// finding, D-b). No WireMock stub is needed: the bad arg is rejected before any HTTP
    /// call is made, mirroring Submit_review_round_without_flow_run_id_returns_error above.
    /// </summary>
    [Test]
    public async Task Send_to_participant_non_boolean_async_returns_clean_error() {
        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = "flow-bad-async",
                ["participant"] = "reviewer",
                ["message"]     = "ctx",
                ["async"]       = "yes"
            };

            var response = await SendRequest(proc, ToolsCallRequest(51, "send_to_participant", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("async must be a boolean")).IsTrue();

            // No POST should have fired — the bad arg is rejected before the HTTP call.
            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/flow-bad-async/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(0);

            // The stdio loop must still be alive: a follow-up request gets a normal response.
            var followUp = await SendRequest(proc, ToolsCallRequest(52, "submit_review_round", new JsonObject {
                ["context"] = "no flow id, expect a clean error too"
            }));
            var followUpResult = followUp["result"]?.AsObject();
            await Assert.That(followUpResult).IsNotNull();
            await Assert.That(followUpResult!["isError"]?.GetValue<bool>()).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// The review alias must stay byte-compatible with old servers that don't know about
    /// "participant" — the field is null-omitted, so the POST body must not carry the key at all.
    /// </summary>
    [Test]
    public async Task Submit_review_round_body_has_no_participant_key() {
        const string flowRunId = "flow-generic-3";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-3",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "ok",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["context"]     = "addressed all feedback"
            };

            var response = await SendRequest(proc, ToolsCallRequest(52, "submit_review_round", args));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var body     = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!.ContainsKey("participant")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Guardrail errors (max_rounds, wrong participant, etc.) come back from the server as a
    /// ProblemDetails 400 body — it must surface verbatim in the tool's error text, same as the
    /// review tools do (McpFlowsServer.cs:146-147/165-166/345-347).
    /// </summary>
    [Test]
    public async Task Guardrail_400_body_surfaces_in_tool_error() {
        const string flowRunId = "flow-generic-4";

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(400)
                    .WithHeader("Content-Type", "application/problem+json")
                    .WithBody("""{"detail":"max_rounds (2) reached for this run — close the flow."}""")
            );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["participant"] = "reviewer",
                ["message"]     = "one more please"
            };

            var response = await SendRequest(proc, ToolsCallRequest(53, "send_to_participant", args));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("max_rounds (2)")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// get_flow_status / close_flow are pure aliases: they must hit the exact same endpoints as
    /// get_review_flow_status / close_review_flow (McpFlowsServer.cs dispatch switch).
    /// </summary>
    [Test]
    public async Task Get_flow_status_and_close_flow_hit_same_endpoints_as_review_tools() {
        const string flowRunId = "flow-generic-5";

        var stubbedStatus = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "definition_id": "my-custom-flow",
              "status": "completed",
              "target_title": "Generic target",
              "round_count": 1,
              "last_result_kind": "APPROVED",
              "last_result_text": "Looks good."
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedStatus));

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"closed"}""")
            );

        using var proc = SpawnMcpServer();
        try {
            var statusArgs     = new JsonObject { ["flow_run_id"] = flowRunId };
            var statusResponse = await SendRequest(proc, ToolsCallRequest(54, "get_flow_status", statusArgs));
            var statusResult   = statusResponse["result"]?.AsObject();
            await Assert.That(statusResult).IsNotNull();
            await Assert.That(statusResult!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var statusText = statusResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(statusText).IsNotNull();
            await Assert.That(statusText!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(statusText.Contains("Looks good.")).IsTrue();

            var getHits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet());
            await Assert.That(getHits.Count).IsEqualTo(1);

            var closeArgs     = new JsonObject { ["flow_run_id"] = flowRunId };
            var closeResponse = await SendRequest(proc, ToolsCallRequest(55, "close_flow", closeArgs));
            var closeResult   = closeResponse["result"]?.AsObject();
            await Assert.That(closeResult).IsNotNull();
            await Assert.That(closeResult!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var closeText = closeResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(closeText).IsNotNull();
            await Assert.That(closeText!.Contains("status: closed")).IsTrue();

            var closeHits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost());
            await Assert.That(closeHits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Regression: kcap-flows auto-registers via the Claude plugin, so Claude Code
    /// spawns `kcap mcp flows` for every session. initialize / tools/list must stay local-only —
    /// the authenticated client (and its GET /auth/config round-trip + re-auth stderr hint) is
    /// created lazily on the first tools/call, so sessions that never use a flows tool pay
    /// nothing. Mirrors McpSessionsServer.
    /// </summary>
    [Test]
    public async Task Initialize_and_tools_list_do_not_consult_auth() {
        using var proc = SpawnMcpServer(provider: "GitHub");
        try {
            await SendRequest(proc, InitializeRequest(1));
            await SendRequest(proc, ToolsListRequest(2));

            var authHits = _server.FindLogEntries(Request.Create().WithPath("/auth/config").UsingGet());
            await Assert.That(authHits.Count).IsEqualTo(0);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A scheme-less server_url would otherwise reach EnsureAbsolute inside the lazy auth-client
    /// factory and hard-exit the process (Environment.Exit(2)) mid-request. The startup URL
    /// guard turns it into a graceful JSON-RPC tool error, and the server keeps serving.
    /// </summary>
    [Test]
    public async Task Tool_call_with_invalid_server_url_returns_error_and_server_survives() {
        using var proc = SpawnMcpServer(urlOverride: "not-a-valid-url");
        try {
            await SendRequest(proc, InitializeRequest(1));

            var response = await SendRequest(proc, ToolsCallRequest(2, "get_review_flow_status", new JsonObject()));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            // Server survived the bad request — a follow-up still gets a response.
            var again = await SendRequest(proc, ToolsListRequest(3));
            await Assert.That(again["result"]?["tools"]).IsNotNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Core scenario: verifies that start_review_flow posts to /api/flows/review/start/v2,
    /// that the POST body includes the resolved requester context (requesting_repo_root = git root,
    /// requesting_session_id from KCAP_SESSION_ID), and that the MCP tool response surfaces
    /// both the flow_run_id/status envelope and the FINDINGS result text from the server.
    /// </summary>
    [Test]
    public async Task Start_review_flow_posts_requester_context_and_returns_plain_text_result() {
        const string flowRunId  = "flow-abc-123";
        const string roundId    = "round-001";
        const string sessionId  = "claude-session-aabbccdd";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "{{roundId}}",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "## Review findings\n\nThe spec looks good.",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath("/api/flows/review/start/v2")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedResponse)
        );

        // Create a subdirectory inside _cwdDir so the server starts there — we can then
        // verify requesting_repo_root points to _cwdDir (the git root) while requesting_cwd
        // points to the subdirectory.
        var subdir = Path.Combine(_cwdDir, "src", "feature");
        Directory.CreateDirectory(subdir);

        using var proc = SpawnMcpServerWithSession(sessionId, workingDirectory: subdir);
        try {
            var args = new JsonObject {
                ["kind"]         = "spec-review",
                ["target_kind"]  = "spec",
                ["target_ref"]   = "docs/feature.md",
                ["target_title"] = "Feature spec",
                ["context"]      = "Please review this spec for completeness."
            };

            var response = await SendRequest(proc, ToolsCallRequest(3, "start_review_flow", args));

            // Assert the MCP response is a success
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            // Assert the response text contains the envelope fields
            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: completed")).IsTrue();
            await Assert.That(text.Contains("result_kind: FINDINGS")).IsTrue();

            // Assert the response text contains the result text from the server
            await Assert.That(text.Contains("## Review findings")).IsTrue();
            await Assert.That(text.Contains("The spec looks good.")).IsTrue();

            // Assert the POST was made to the correct endpoint
            var hits = _server.FindLogEntries(
                Request.Create().WithPath("/api/flows/review/start/v2").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);

            // Verify the POST body includes the requester context fields
            var body = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();

            // requesting_session_id: from KCAP_SESSION_ID (stripped of dashes)
            var reqSessionId = bodyNode!["requesting_session_id"]?.GetValue<string>();
            await Assert.That(reqSessionId).IsNotNull();
            await Assert.That(reqSessionId!.Contains("claudesessionaabbccdd") || reqSessionId.Contains("claude-session-aabbccdd")).IsTrue();

            // requesting_cwd: the subdirectory the server was started in
            var reqCwd = bodyNode["requesting_cwd"]?.GetValue<string>();
            await Assert.That(reqCwd).IsNotNull();
            // cwd should be either subdir or contain "src/feature"
            await Assert.That(
                reqCwd!.Contains(Path.Combine("src", "feature")) ||
                reqCwd.Equals(subdir, StringComparison.OrdinalIgnoreCase)
            ).IsTrue();

            // requesting_repo_root: the git root (= _cwdDir, where git init was run).
            // On macOS directory paths can differ between what the test creates
            // (Path.GetTempPath() / Guid) and what the spawned process sees via
            // Directory.GetCurrentDirectory() (which resolves symlinks on some platforms).
            // We verify the invariant: the repo root is a parent of the subdir, and it
            // contains the unique test-directory name so it can't be an arbitrary system dir.
            var reqRepoRoot = bodyNode["requesting_repo_root"]?.GetValue<string>();
            await Assert.That(reqRepoRoot).IsNotNull();
            // The unique GUID portion of _cwdDir must appear somewhere in the repo root path
            // (both paths point to the same directory, just possibly via different symlink chains).
            var cwdDirName = Path.GetFileName(_cwdDir.TrimEnd(Path.DirectorySeparatorChar));
            await Assert.That(reqRepoRoot!.Contains(cwdDirName, StringComparison.OrdinalIgnoreCase)).IsTrue();

            // kind: matches what we passed
            await Assert.That(bodyNode["kind"]?.GetValue<string>()).IsEqualTo("spec-review");
            await Assert.That(bodyNode["target_kind"]?.GetValue<string>()).IsEqualTo("spec");
            await Assert.That(bodyNode["context"]?.GetValue<string>()).IsEqualTo("Please review this spec for completeness.");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_review_flow_status_calls_correct_endpoint_and_surfaces_envelope() {
        const string flowRunId = "flow-status-xyz";

        var stubbedStatus = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "definition_id": "spec-review",
              "status": "completed",
              "target_title": "My Spec",
              "round_count": 2,
              "last_result_kind": "APPROVED",
              "last_result_text": "Approved with minor comments."
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedStatus)
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(4, "get_review_flow_status", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: completed")).IsTrue();
            await Assert.That(text.Contains("Approved with minor comments.")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()
            );
            await Assert.That(hits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Submit_review_round_posts_to_rounds_endpoint() {
        const string flowRunId = "flow-round-abc";
        const string roundId   = "round-002";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "{{roundId}}",
              "round_number": 2,
              "status": "completed",
              "result_kind": "APPROVED",
              "result_text": "Changes look good now.",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}/rounds")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedResponse)
        );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["context"]     = "I have addressed all feedback."
            };
            var response = await SendRequest(proc, ToolsCallRequest(5, "submit_review_round", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("round_id:")).IsTrue();
            await Assert.That(text.Contains("Changes look good now.")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);

            // Verify the POST body has context
            var body     = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["context"]?.GetValue<string>()).IsEqualTo("I have addressed all feedback.");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Close_review_flow_posts_to_close_endpoint() {
        const string flowRunId = "flow-close-abc";

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}/close")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"closed"}""")
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(6, "close_review_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: closed")).IsTrue();
            // FormatCloseResponse must NOT emit round_id or result_kind lines
            await Assert.That(text.Contains("round_id:")).IsFalse();
            await Assert.That(text.Contains("result_kind:")).IsFalse();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Transient-retry (a): if the GET for a running round returns HTTP 500 once
    /// and then 200 with a terminal result, the poll should survive the transient
    /// error and return the terminal result. Guards PollUntilTerminalAsync's
    /// !IsSuccessStatusCode → continue branch.
    /// </summary>
    [Test]
    public async Task Start_review_flow_async_survives_transient_500_on_poll() {
        const string flowRunId = "flow-retry-500";
        const string scenario  = "retry-500";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET #1: 500 (transient). GET #2: terminal findings.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WillSetStateTo("after-500")
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("Internal Server Error"));
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WhenStateIs("after-500")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"findings","target_title":"t","round_count":1,"round_number":1,"round_status":"findings","round_result_kind":"findings","round_result_text":"FINDINGS:\n- P1"}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r",
                ["target_title"]="t", ["context"]="please review"
            };
            var response = await SendRequest(proc, ToolsCallRequest(30, "start_review_flow", args), TimeSpan.FromSeconds(30));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text!.Contains("FINDINGS:")).IsTrue();
            await Assert.That(text.Contains("result_kind: findings")).IsTrue();

            // Exactly 2 GETs: the 500 and then the terminal 200.
            await Assert.That(_server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()).Count).IsEqualTo(2);
        } finally { await ShutdownAsync(proc); }
    }

    /// <summary>
    /// Run-terminal stop (c): if the GET returns a run-level <c>status: "failed"</c>
    /// while <c>round_status</c> is still "running" (round didn't produce a result),
    /// the poll must stop immediately (not hang until the 8-min cap) and return an
    /// explicit isError:true result — NOT "Review still running" and NOT a partial envelope.
    /// Guards the run-terminal early-exit + Finding #1 (no stale data) in PollUntilTerminalAsync.
    /// </summary>
    [Test]
    public async Task Start_review_flow_async_stops_when_run_level_fails() {
        const string flowRunId = "flow-run-failed";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET always returns run-level "failed" (round_status still "running" — no terminal result).
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"failed","target_title":"t","round_count":1,"round_number":1,"round_status":"running","round_result_kind":null,"round_result_text":null}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r",
                ["target_title"]="t", ["context"]="please review"
            };
            // This must resolve quickly (run-terminal path exits on first "failed" GET),
            // well within 15 s (compared to the 8-min cap if we polled indefinitely).
            var response = await SendRequest(proc, ToolsCallRequest(31, "start_review_flow", args), TimeSpan.FromSeconds(15));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            var text = result!["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            // Fix #1: run failed without producing a terminal round — must be isError:true,
            // must NOT be the graceful-cap message, must mention "failed".
            await Assert.That(result["isError"]?.GetValue<bool>()).IsTrue();
            await Assert.That(text!.Contains("Review still running")).IsFalse();
            await Assert.That(text.Contains("failed")).IsTrue();
        } finally { await ShutdownAsync(proc); }
    }

    // NOTE: graceful-cap behaviour (poll exceeds 8-min PollCap → returns
    // "Review still running … call get_review_flow_status" message) is exercised
    // manually only. The 8-min cap has no injectable test seam in the current
    // McpFlowsServer implementation, so a CI test would either run for 8 minutes
    // (unacceptable) or require source changes out of scope for this task.
    // Manual e2e: start a flow against a server that never completes the round,
    // wait 8 min, assert the graceful-cap message appears in the MCP tool output.

    /// <summary>
    /// Finding #1: when run-level status is "failed" but the projected round_number doesn't match
    /// the round we submitted (e.g. projection still shows prior round 1 when we submitted round 2),
    /// the result MUST be an explicit run-failed error (isError:true), NOT the prior round's findings.
    /// </summary>
    [Test]
    public async Task Run_failed_before_requested_round_returns_explicit_error_not_stale_findings() {
        const string flowRunId = "flow-run-failed-stale";

        // POST submits round 2.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r2","round_number":2,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET returns run-level "failed" but still shows round 1's findings (stale projection).
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"failed","target_title":"t","round_count":2,"round_number":1,"round_status":"findings","round_result_kind":"findings","round_result_text":"FINDINGS:\n- Round 1 stale data"}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["context"]     = "Re-review after fixes."
            };
            var response = await SendRequest(proc, ToolsCallRequest(40, "submit_review_round", args), TimeSpan.FromSeconds(15));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            // Must be an error result (isError:true), NOT the prior round's findings.
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            // Must NOT contain the stale round 1 findings text.
            await Assert.That(text!.Contains("Round 1 stale data")).IsFalse();
            // Must contain an explicit failure message for round 2.
            await Assert.That(text.Contains("failed")).IsTrue();
            await Assert.That(text.Contains('2')).IsTrue(); // round number mentioned
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Finding #2 + #4: persistent 500 on every GET exhausts the transient-retry budget and
    /// returns isError:true well before the 8-min cap. The result must NOT be "Review still running"
    /// (which is only for genuine running-at-cap). Budget is 5 consecutive transient failures.
    /// </summary>
    [Test]
    public async Task Persistent_500_exhausts_retry_budget_and_returns_isError() {
        const string flowRunId = "flow-persistent-500";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // Every GET returns 500.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("Internal Server Error"));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"] = "spec-review", ["target_kind"] = "spec", ["target_ref"] = "r",
                ["target_title"] = "t", ["context"] = "please review"
            };
            // Must complete well before 8 min (expect ~20-30s for 6 GETs at 3s poll + budget logic).
            var response = await SendRequest(proc, ToolsCallRequest(41, "start_review_flow", args), TimeSpan.FromSeconds(60));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            // Must be an error (isError:true), NOT "Review still running" graceful cap.
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("Review still running")).IsFalse();
            await Assert.That(text.Contains("Error:")).IsTrue();

            // Should have hit exactly MaxTransientRetries + 1 GETs before giving up.
            var getCount = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()
            ).Count;
            // Budget is 5; 6th failure (index 5) triggers the error return.
            await Assert.That(getCount).IsEqualTo(6);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Finding #2 + #4: non-transient 403 on GET returns immediate isError:true with no retry.
    /// Similarly for 400. These are non-transient 4xx errors that must fail immediately.
    /// </summary>
    [Test]
    public async Task Non_transient_403_on_poll_returns_immediate_isError() {
        const string flowRunId = "flow-403";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET returns 403 (non-transient 4xx, not 401 which has refresh-retry logic).
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("Forbidden"));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"] = "spec-review", ["target_kind"] = "spec", ["target_ref"] = "r",
                ["target_title"] = "t", ["context"] = "please review"
            };
            // Must complete almost immediately (no retry, no delay loop for 4xx).
            var response = await SendRequest(proc, ToolsCallRequest(42, "start_review_flow", args), TimeSpan.FromSeconds(15));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("403")).IsTrue();

            // Exactly 1 GET (immediate fail on first 403, no retry).
            var getCount = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()
            ).Count;
            await Assert.That(getCount).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Finding #3: 404 that occurs after the grace deadline (anchored to poll start)
    /// must return isError:true. The key invariant is that grace is relative to when
    /// polling began, not when the first 404 was observed.
    ///
    /// Since NotFoundGrace = 10s and PollInterval = 3s, we stub every GET as 404
    /// and wait long enough for the grace deadline to pass (> 10s). The poll must
    /// give up before the 8-min cap.
    /// </summary>
    [Test]
    public async Task NotFound_past_grace_deadline_returns_isError() {
        const string flowRunId = "flow-404-grace";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // Every GET returns 404 indefinitely.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("Not Found"));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"] = "spec-review", ["target_kind"] = "spec", ["target_ref"] = "r",
                ["target_title"] = "t", ["context"] = "please review"
            };
            // NotFoundGrace = 10s, PollInterval = 3s → should fail within ~15s (grace + one more poll).
            // Allow 30s to be safe.
            var response = await SendRequest(proc, ToolsCallRequest(43, "start_review_flow", args), TimeSpan.FromSeconds(30));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("not found")).IsTrue();
            // Must NOT be the 8-min graceful cap message.
            await Assert.That(text.Contains("Review still running")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Submit_review_round_without_flow_run_id_returns_error() {
        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["context"] = "Some context but no flow ID"
            };
            var response = await SendRequest(proc, ToolsCallRequest(7, "submit_review_round", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("flow_run_id")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Start_review_flow_async_polls_until_terminal_findings() {
        const string flowRunId = "flow-poll-1";
        const string scenario  = "poll";

        // POST returns running + round_number 1.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET #1: still running. GET #2: terminal findings.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WillSetStateTo("seen-once")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"running","target_title":"t","round_count":1,"round_number":1,"round_status":"running","round_result_kind":null,"round_result_text":null}"""));
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WhenStateIs("seen-once")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"findings","target_title":"t","round_count":1,"round_number":1,"round_status":"findings","round_result_kind":"findings","round_result_text":"FINDINGS:\n- P1"}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r",
                ["target_title"]="t", ["context"]="please review"
            };
            var response = await SendRequest(proc, ToolsCallRequest(20, "start_review_flow", args), TimeSpan.FromSeconds(30));
            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text!.Contains("FINDINGS:")).IsTrue();
            await Assert.That(text.Contains("result_kind: findings")).IsTrue();

            // The POST carried async:true.
            var postBody = JsonNode.Parse(_server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(postBody!["async"]?.GetValue<bool>()).IsTrue();
            // At least 2 GETs (running then terminal).
            await Assert.That(_server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()).Count >= 2).IsTrue();
        } finally { await ShutdownAsync(proc); }
    }

    [Test]
    public async Task Start_review_flow_uses_terminal_result_from_post_without_polling() {
        const string flowRunId = "flow-old-server";
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"completed","result_kind":"FINDINGS","result_text":"## done","reviewer_agent_id":null,"reviewer_session_id":null}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject { ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r", ["target_title"]="t", ["context"]="c" };
            var response = await SendRequest(proc, ToolsCallRequest(21, "start_review_flow", args));
            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text!.Contains("## done")).IsTrue();
            // No GET polling happened (status was already terminal).
            await Assert.That(_server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()).Count).IsEqualTo(0);
        } finally { await ShutdownAsync(proc); }
    }

    /// <summary>
    /// Writes a non-expired token to the per-test config dir's token store so the
    /// CLI's <c>HttpClientExtensions.CreateAuthenticatedClientAsync</c> attaches a
    /// Bearer header. Exercises the long-lived-server path (the MCP server holds a
    /// single HttpClient for the agent's whole session).
    /// </summary>
    void SeedToken(string accessToken = "seed-token") {
        var tokensDir = Path.Combine(_cfgDir, "tokens");
        Directory.CreateDirectory(tokensDir);
        var tokenJson = $$"""
            {
              "access_token": "{{accessToken}}",
              "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}",
              "github_username": "seed-user",
              "provider": "GitHubApp"
            }
            """;
        File.WriteAllText(Path.Combine(tokensDir, "default.json"), tokenJson);
    }

    /// <summary>
    /// Regression for the "token refresh is never picked up after startup" bug.
    /// The MCP server caches a single <c>HttpClient</c> for the whole agent session;
    /// if the auth header expires mid-session, every tool call returned the friendly
    /// 401 message until the server was restarted. The fix retries once on 401 after
    /// calling <c>TokenStore.GetValidTokensAsync</c>.
    ///
    /// We seed a non-expired token in the per-test config dir and stub WireMock so
    /// the first call returns 401 and the second returns 200. The retry re-reads
    /// the token (which hasn't actually expired, so it's returned as-is) and resends —
    /// proving the retry path runs at all. (Exercising the real refresh-token flow
    /// would require stubbing the GitHub refresh endpoint as well, which is out of
    /// scope for this regression.)
    /// </summary>
    [Test]
    public async Task Refreshed_token_succeeds_after_401() {
        const string flowRunId   = "flow-retry-abc";
        const string stubbedBody = $$"""{"flow_run_id":"{{flowRunId}}","status":"closed"}""";
        const string scenario    = "auth-retry";

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost())
            .InScenario(scenario)
            .WillSetStateTo("after-401")
            .RespondWith(Response.Create().WithStatusCode(401).WithBody(""));

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost())
            .InScenario(scenario)
            .WhenStateIs("after-401")
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(stubbedBody)
            );

        SeedToken();

        using var proc = SpawnMcpServer(provider: "GitHubApp");
        try {
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(8, "close_review_flow", args));

            var result = response["result"]?.AsObject();
            // Must be a success — the 401 was retried and the second call succeeded.
            await Assert.That(result?["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: closed")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(2);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    const string DynamicDefinitionYaml = """
        id: my-dynamic-flow
        participants:
          reviewer:
            vendor: claude
            model: claude-sonnet-4-5
            workspace: none
        """;

    static JsonObject DynamicStartTargetArgs() => new() {
        ["target_kind"]  = "pr",
        ["target_ref"]   = "https://github.com/x/y/pull/1",
        ["target_title"] = "My PR",
        ["context"]      = "please look at this"
    };

    /// <summary>
    /// Dynamic flows: start_flow with definition_yaml posts the YAML doc on the snake_case
    /// definition_yaml wire field and must NOT carry "kind" at all — the server treats the two
    /// as mutually exclusive and rejects a body with both.
    /// </summary>
    [Test]
    public async Task Start_flow_with_definition_yaml_posts_it_and_omits_kind() {
        const string flowRunId = "flow-dynamic-1";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-1",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "dynamic flow result",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = DynamicStartTargetArgs();
            args["definition_yaml"] = DynamicDefinitionYaml;

            var response = await SendRequest(proc, ToolsCallRequest(60, "start_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("dynamic flow result")).IsTrue();

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["definition_yaml"]?.GetValue<string>()).IsEqualTo(DynamicDefinitionYaml);
            await Assert.That(bodyNode.ContainsKey("kind")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// The definition_id / definition_yaml xor can't be expressed in the MCP schema (both are
    /// optional there), so the handler must enforce exactly-one BEFORE any HTTP call and return
    /// a clean isError tool result naming the mutual exclusion.
    /// </summary>
    [Test]
    public async Task Start_flow_with_both_or_neither_id_and_yaml_errors_before_http() {
        using var proc = SpawnMcpServer();
        try {
            var both = DynamicStartTargetArgs();
            both["definition_id"]   = "catalog-flow";
            both["definition_yaml"] = DynamicDefinitionYaml;

            var bothResponse = await SendRequest(proc, ToolsCallRequest(61, "start_flow", both));
            var bothResult   = bothResponse["result"]?.AsObject();
            await Assert.That(bothResult).IsNotNull();
            await Assert.That(bothResult!["isError"]?.GetValue<bool>()).IsTrue();

            var bothText = bothResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(bothText).IsNotNull();
            await Assert.That(bothText!.Contains("exactly one of definition_id")).IsTrue();

            var neitherResponse = await SendRequest(proc, ToolsCallRequest(62, "start_flow", DynamicStartTargetArgs()));
            var neitherResult   = neitherResponse["result"]?.AsObject();
            await Assert.That(neitherResult).IsNotNull();
            await Assert.That(neitherResult!["isError"]?.GetValue<bool>()).IsTrue();

            var neitherText = neitherResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(neitherText).IsNotNull();
            await Assert.That(neitherText!.Contains("exactly one of definition_id")).IsTrue();

            // Neither call may have reached the server.
            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(0);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Dynamic-rejection contract: any non-2xx body carrying a string "error" code plus "message"
    /// is a NEW-server coded rejection — the CLI surfaces the server message verbatim (prefixed
    /// with the code) and must NOT add the "may not support dynamic flows" old-server hint.
    /// </summary>
    [Test]
    public async Task Coded_400_surfaces_server_message_verbatim() {
        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(400)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"error":"model_unpriced","message":"participant 'reviewer': model 'x' has no known pricing — pick a priced model."}""")
            );

        using var proc = SpawnMcpServer();
        try {
            var args = DynamicStartTargetArgs();
            args["definition_yaml"] = DynamicDefinitionYaml;

            var response = await SendRequest(proc, ToolsCallRequest(63, "start_flow", args));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("model_unpriced")).IsTrue();
            await Assert.That(text.Contains("participant 'reviewer': model 'x' has no known pricing — pick a priced model.")).IsTrue();
            await Assert.That(text.Contains("may not support dynamic flows")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// An UNCODED non-2xx on a start that included definition_yaml means the server may predate
    /// dynamic flows — the tool error must carry the upgrade hint plus the raw body. The same
    /// uncoded failure on a definition_id (catalog) start must NOT get the hint.
    /// </summary>
    [Test]
    public async Task Uncoded_500_on_dynamic_start_maps_to_unsupported_server_hint() {
        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("upstream exploded"));
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("upstream exploded"));

        using var proc = SpawnMcpServer();
        try {
            var dynamicArgs = DynamicStartTargetArgs();
            dynamicArgs["definition_yaml"] = DynamicDefinitionYaml;

            var dynamicResponse = await SendRequest(proc, ToolsCallRequest(64, "start_flow", dynamicArgs));
            var dynamicResult   = dynamicResponse["result"]?.AsObject();
            await Assert.That(dynamicResult).IsNotNull();
            await Assert.That(dynamicResult!["isError"]?.GetValue<bool>()).IsTrue();

            var dynamicText = dynamicResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(dynamicText).IsNotNull();
            await Assert.That(dynamicText!.Contains("may not support dynamic flows")).IsTrue();
            await Assert.That(dynamicText.Contains("upstream exploded")).IsTrue();

            var catalogArgs = DynamicStartTargetArgs();
            catalogArgs["definition_id"] = "catalog-flow";

            var catalogResponse = await SendRequest(proc, ToolsCallRequest(65, "start_flow", catalogArgs));
            var catalogResult   = catalogResponse["result"]?.AsObject();
            await Assert.That(catalogResult).IsNotNull();
            await Assert.That(catalogResult!["isError"]?.GetValue<bool>()).IsTrue();

            var catalogText = catalogResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(catalogText).IsNotNull();
            await Assert.That(catalogText!.Contains("upstream exploded")).IsTrue();
            await Assert.That(catalogText.Contains("may not support dynamic flows")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A non-2xx body that IS valid JSON but not an object (e.g. a proxy's quoted scalar string)
    /// must not throw past the coded-rejection check — <c>JsonNode.Parse(...).AsObject()</c> would
    /// throw <see cref="InvalidOperationException"/> on a scalar/array node, which used to escape to
    /// the dispatcher catch-all and replace the useful status/body/hint with a generic internal
    /// error. It must fall through to the uncoded path exactly like non-JSON bodies do.
    /// </summary>
    [Test]
    public async Task NonObject_json_body_falls_through_to_uncoded_path() {
        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(502).WithHeader("Content-Type", "application/json").WithBody("\"Bad Gateway\""));

        using var proc = SpawnMcpServer();
        try {
            var dynamicArgs = DynamicStartTargetArgs();
            dynamicArgs["definition_yaml"] = DynamicDefinitionYaml;

            var response = await SendRequest(proc, ToolsCallRequest(66, "start_flow", dynamicArgs));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("may not support dynamic flows")).IsTrue();
            await Assert.That(text.Contains("\"Bad Gateway\"")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Pins the start_flow schema after the dynamic-flows change: definition_yaml is offered,
    /// required drops definition_id (the xor can't be expressed in the schema — it lives in both
    /// property descriptions and is enforced by the handler), and the definition_yaml description
    /// carries the parser's hard requirements (workspace: none, concrete model).
    /// </summary>
    [Test]
    public async Task Start_flow_schema_offers_definition_yaml_and_requires_neither_definition_arg() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));
            var tools    = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();

            var startFlow = tools!.First(t => t?["name"]?.GetValue<string>() == "start_flow")!;

            await AssertSchema(
                startFlow,
                properties: ["definition_id", "definition_yaml", "target_kind", "target_ref", "target_title", "context", "instructions", "mode", "vendor", "model"],
                required:   ["target_kind", "target_ref", "target_title", "context"]
            );

            var props    = startFlow["inputSchema"]!["properties"]!.AsObject();
            var idDesc   = props["definition_id"]?["description"]?.GetValue<string>() ?? "";
            var yamlDesc = props["definition_yaml"]?["description"]?.GetValue<string>() ?? "";

            await Assert.That(idDesc.Contains("exactly one")).IsTrue();
            await Assert.That(yamlDesc.Contains("exactly one")).IsTrue();
            await Assert.That(yamlDesc.Contains("workspace: none")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }
}
