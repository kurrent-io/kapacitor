using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// An in-process fake <c>codex app-server</c> peer: it plays the SERVER side of the JSON-RPC stdio
/// wire over duplex pipes, so <see cref="CodexAppServerHostedAgentRuntime"/>'s real
/// <c>StartAsync</c>/turn logic runs against a scriptable protocol partner with no child process.
/// Each instance answers <c>initialize</c> / <c>hooks/list</c> / <c>thread/start</c> /
/// <c>turn/start</c> / <c>turn/interrupt</c> and can inject a server→client approval request and
/// token-usage notifications. Behaviour is configured via the public fields before it is handed to a
/// spawn delegate.
/// </summary>
sealed class FakeCodexAppServer : IAsyncDisposable {
    readonly Pipe   _toServer = new(); // client -> server
    readonly Pipe   _toClient = new(); // server -> client
    Stream? _readClient;
    Stream? _writeClient;

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _writeGate = new(1, 1);
    Task _loop = Task.CompletedTask;
    long _nextServerId = 90_000;
    readonly Dictionary<long, TaskCompletionSource<JsonElement>> _serverPending = new();
    readonly List<Task> _handlers = [];

    // ── Scripted behaviour ─────────────────────────────────────────────────────────────────────
    public string      ThreadId  = "thread-abc";
    public string      Model     = "gpt-5.3-codex";
    public JsonArray   HooksData = TrustedKcapHooks();
    public string      TurnStatus = "completed";
    public bool        InjectApprovalDuringTurn;
    public string      ApprovalMethod = "item/commandExecution/requestApproval";
    public int         Fail32001TimesOnTurnStart;
    public (long input, long output, long total)? EmitUsageOnTurn;

    // ── Observed ───────────────────────────────────────────────────────────────────────────────
    public readonly List<string>       ReceivedMethods    = [];
    public readonly List<string>       InitializeOptOuts  = [];
    public string?                     LastThreadStartSandbox;
    public string?                     LastTurnApprovalPolicy;
    public JsonElement?                ApprovalResponse; // the client's response to the injected request
    int _turnStartCount;

    public CodexAppServerConnection ConnectClient() {
        var conn = new CodexAppServerConnection(
            writeStream: _toServer.Writer.AsStream(),
            readStream:  _toClient.Reader.AsStream(),
            logger: NullLogger<CodexAppServerConnection>.Instance);
        _readClient  = _toServer.Reader.AsStream();
        _writeClient = _toClient.Writer.AsStream();
        _loop = RunServerAsync(_cts.Token);
        return conn;
    }

    async Task RunServerAsync(CancellationToken ct) {
        using var reader = new StreamReader(_readClient!, Encoding.UTF8, false, leaveOpen: true);
        while (!ct.IsCancellationRequested) {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var hasId     = root.TryGetProperty("id", out var idEl);
            var hasMethod = root.TryGetProperty("method", out var methodEl);

            if (hasId && hasMethod) {
                // Dispatch WITHOUT blocking the read loop: a handler (turn/start) may make a nested
                // server→client request whose response arrives on this very loop.
                var cloned = root.Clone();
                _handlers.Add(HandleRequestAsync(idEl.GetInt64(), methodEl.GetString() ?? "", cloned, ct));
            } else if (hasId && !hasMethod) {
                // A response to one of OUR server→client requests.
                if (_serverPending.Remove(idEl.GetInt64(), out var tcs))
                    tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
            }
        }
    }

    async Task HandleRequestAsync(long id, string method, JsonElement root, CancellationToken ct) {
        ReceivedMethods.Add(method);
        var @params = root.TryGetProperty("params", out var p) ? p : default;

        switch (method) {
            case "initialize":
                if (@params.ValueKind == JsonValueKind.Object
                    && @params.TryGetProperty("capabilities", out var caps)
                    && caps.TryGetProperty("optOutNotificationMethods", out var opt)
                    && opt.ValueKind == JsonValueKind.Array)
                    foreach (var m in opt.EnumerateArray()) InitializeOptOuts.Add(m.GetString() ?? "");
                await RespondAsync(id, new JsonObject { ["userAgent"] = "codex-fake" }, ct);
                break;

            case "hooks/list":
                await RespondAsync(id, new JsonObject { ["data"] = HooksData.DeepClone() }, ct);
                break;

            case "thread/start":
                if (@params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("sandbox", out var sb))
                    LastThreadStartSandbox = sb.ValueKind == JsonValueKind.String ? sb.GetString() : null;
                await RespondAsync(id, new JsonObject {
                    ["thread"]        = new JsonObject { ["id"] = ThreadId, ["sessionId"] = ThreadId, ["path"] = "/tmp/r.jsonl" },
                    ["model"]         = Model,
                    ["modelProvider"] = "openai",
                }, ct);
                break;

            case "turn/start": {
                _turnStartCount++;
                if (_turnStartCount <= Fail32001TimesOnTurnStart) {
                    await RespondErrorAsync(id, -32001, "bounded ingress", ct);
                    break;
                }
                if (@params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("approvalPolicy", out var ap))
                    LastTurnApprovalPolicy = ap.GetString();

                var turnId = "turn-" + _turnStartCount;
                await RespondAsync(id, new JsonObject {
                    ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "inProgress", ["items"] = new JsonArray() },
                }, ct);

                // Optionally inject an out-of-policy approval request mid-turn.
                if (InjectApprovalDuringTurn) {
                    var resp = await ServerRequestAsync(ApprovalMethod,
                        new JsonObject { ["threadId"] = ThreadId, ["turnId"] = turnId }, ct);
                    ApprovalResponse = resp;
                }

                if (EmitUsageOnTurn is { } u)
                    await NotifyAsync("thread/tokenUsage/updated", new JsonObject {
                        ["threadId"] = ThreadId, ["turnId"] = turnId,
                        ["tokenUsage"] = new JsonObject {
                            ["total"] = new JsonObject {
                                ["inputTokens"] = u.input, ["cachedInputTokens"] = 0, ["outputTokens"] = u.output,
                                ["reasoningOutputTokens"] = 0, ["totalTokens"] = u.total },
                            ["last"] = new JsonObject {
                                ["inputTokens"] = u.input, ["cachedInputTokens"] = 0, ["outputTokens"] = u.output,
                                ["reasoningOutputTokens"] = 0, ["totalTokens"] = u.total },
                        },
                    }, ct);

                await NotifyAsync("turn/completed", new JsonObject {
                    ["threadId"] = ThreadId,
                    ["turn"]     = new JsonObject { ["id"] = turnId, ["status"] = TurnStatus, ["items"] = new JsonArray() },
                }, ct);
                break;
            }

            case "turn/interrupt":
                await RespondAsync(id, new JsonObject(), ct);
                await NotifyAsync("turn/completed", new JsonObject {
                    ["threadId"] = ThreadId,
                    ["turn"]     = new JsonObject { ["id"] = Str(@params, "turnId") ?? "turn-x", ["status"] = "interrupted", ["items"] = new JsonArray() },
                }, ct);
                break;

            default:
                await RespondErrorAsync(id, -32601, "Method not found: " + method, ct);
                break;
        }
    }

    async Task<JsonElement> ServerRequestAsync(string method, JsonNode @params, CancellationToken ct) {
        var id  = Interlocked.Increment(ref _nextServerId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _serverPending[id] = tcs;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params.DeepClone() }, ct);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    Task RespondAsync(long id, JsonNode result, CancellationToken ct) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, ct);

    Task RespondErrorAsync(long id, int code, string message, CancellationToken ct) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, ct);

    Task NotifyAsync(string method, JsonNode @params, CancellationToken ct) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }, ct);

    async Task WriteAsync(JsonNode frame, CancellationToken ct) {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n");
        await _writeGate.WaitAsync(ct);
        try {
            await _writeClient!.WriteAsync(bytes, ct);
            await _writeClient!.FlushAsync(ct);
        } finally {
            _writeGate.Release();
        }
    }

    static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonArray TrustedKcapHooks() => HookData([
        ("sessionStart", "kcap hook --codex", "trusted", "sha256:a"),
        ("stop", "kcap hook --codex", "trusted", "sha256:b"),
        ("permissionRequest", "kcap hook --codex", "trusted", "sha256:c"),
    ]);

    public static JsonArray HookData(IReadOnlyList<(string evt, string cmd, string trust, string? hash)> hooks) {
        var arr = new JsonArray();
        foreach (var (evt, cmd, trust, hash) in hooks)
            arr.Add(new JsonObject {
                ["key"] = $"/home/u/.codex/hooks.json:{evt}:0:0", ["eventName"] = evt,
                ["command"] = cmd, ["currentHash"] = hash, ["trustStatus"] = trust,
            });
        return new JsonArray(new JsonObject { ["cwd"] = "/tmp/wt", ["hooks"] = arr, ["errors"] = new JsonArray(), ["warnings"] = new JsonArray() });
    }

    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        try { await Task.WhenAll(_handlers).WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _writeGate.Dispose();
        _cts.Dispose();
    }
}
