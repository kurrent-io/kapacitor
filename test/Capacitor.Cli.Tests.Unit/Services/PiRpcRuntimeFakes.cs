// test/Capacitor.Cli.Tests.Unit/Services/PiRpcRuntimeFakes.cs
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Scripted <see cref="IPiRpcProcess"/> fake — the long-lived-child analogue of
/// <c>FakeAgyTurnProcess</c>. Where agy's fake scripts ONE turn's canned line sequence and then EOFs,
/// this one is driven interactively by the test: <see cref="Push"/> puts a protocol line on the
/// child's "stdout" at any moment, <see cref="Writes"/> captures every command the runtime wrote to
/// its "stdin", and <see cref="EndOfStream"/> is the explicit "the child exited" event. That
/// difference is the whole point — Pi's runtime has one process for the session's whole life, so its
/// interesting behaviours (a response correlating to a command sent earlier, an echo arriving after a
/// prompt, a turn going busy then settling) are all ORDERING facts between reads and writes that a
/// canned script cannot express.
/// </summary>
internal sealed class FakePiRpcProcess : IPiRpcProcess {
    readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    readonly Lock            _gate  = new();
    readonly List<string>    _writes = [];

    /// <summary>When non-null, a <c>get_state</c> command written by the runtime is answered
    /// immediately with this literal line — the ordinary "Pi is healthy and answers its handshake"
    /// setup. Set to <see langword="null"/> for the tests that need the handshake to go UNANSWERED
    /// (the deadline and process-exit barrier faults).</summary>
    public string? AutoStateResponse { get; set; } = PiRpcRuntimeFakes.GetStateResponse();

    /// <summary>Observer for every written command line, invoked inside
    /// <see cref="WriteLineAsync"/> — lets a test answer a correlated command (a <c>prompt</c>'s
    /// response, say) at the exact moment it goes on the wire.</summary>
    public Action<string>? OnWrite { get; set; }

    /// <summary>When true, every <see cref="WriteLineAsync"/> fails — the "the child's stdin is
    /// gone" shape.</summary>
    public bool FailWrites { get; set; }

    public int  Pid            { get; }              = 4242;
    public bool HasExited      { get; private set; }
    public int? ExitCode       { get; private set; }
    public int  TerminateCalls { get; private set; }
    public int  DisposeCalls   { get; private set; }

    /// <summary>Settable so a test can simulate a child that left a stderr trail — the real
    /// <c>PiRpcProcess</c>'s capture, faked here rather than replayed through a scripted stderr
    /// stream this fake has no stdio pipes for.</summary>
    public string? Diagnostics { get; set; }

    /// <summary>Every command line the runtime wrote, in order. A snapshot copy — the runtime writes
    /// from its own threads while a test reads.</summary>
    public IReadOnlyList<string> Writes {
        get { lock (_gate) return _writes.ToArray(); }
    }

    /// <summary>Puts one line on the child's stdout.</summary>
    public void Push(string line) => _lines.Writer.TryWrite(line);

    /// <summary>The child exited: stdout hits EOF and the process reports the exit. This is the
    /// event the runtime must translate into "terminal".</summary>
    public void EndOfStream(int exitCode = 0) {
        HasExited = true;
        ExitCode  = exitCode;
        _lines.Writer.TryComplete();
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (await _lines.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_lines.Reader.TryRead(out var line))
                yield return line;
    }

    public Task WriteLineAsync(string json, CancellationToken ct) {
        if (FailWrites) return Task.FromException(new IOException("fake pi stdin is gone"));

        lock (_gate) _writes.Add(json);

        if (AutoStateResponse is { } response && json.Contains("\"type\":\"get_state\"", StringComparison.Ordinal))
            Push(response);

        OnWrite?.Invoke(json);

        return Task.CompletedTask;
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? timeout = null) {
        TerminateCalls++;
        HasExited = true;
        ExitCode ??= -1;
        _lines.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
        DisposeCalls++;
        _lines.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared line builders and construction helpers for
/// <see cref="PiRpcHostedAgentRuntime"/>'s tests. Every literal here is a Pi JSONL-RPC frame in the
/// pinned upstream shape — kept in ONE place so a protocol correction lands once.</summary>
internal static class PiRpcRuntimeFakes {
    public const string SessionId      = "pi-session-abc123";
    public const string StateModelId   = "anthropic/claude-sonnet-4";
    public const string RequestedModel = "requested-model";

    /// <summary>A <c>get_state</c> response frame. <paramref name="modelId"/> null omits the whole
    /// <c>model</c> object (Pi's own "no model resolved yet" shape), which is what drives the
    /// fallback to the requested model.</summary>
    public static string GetStateResponse(
            string  id          = "init-state",
            string? sessionId   = SessionId,
            string? modelId     = StateModelId,
            bool    isStreaming = false,
            bool    success     = true) {
        var data = new JsonObject {
            ["model"]        = modelId is null
                ? null
                : new JsonObject { ["id"] = modelId, ["provider"] = "anthropic" },
            ["isStreaming"]  = isStreaming,
            ["sessionFile"]  = "/tmp/s.jsonl",
            ["messageCount"] = 3,
        };

        if (sessionId is not null) data["sessionId"] = sessionId;

        return new JsonObject {
            ["type"]    = "response",
            ["command"] = "get_state",
            ["id"]      = id,
            ["success"] = success,
            ["data"]    = data,
        }.ToJsonString();
    }

    public static string PromptResponse(string id, bool success, string? error = null) {
        var frame = new JsonObject {
            ["type"]    = "response",
            ["command"] = "prompt",
            ["id"]      = id,
            ["success"] = success,
        };

        if (success) frame["data"]  = new JsonObject();
        else         frame["error"] = error ?? "prompt rejected";

        return frame.ToJsonString();
    }

    public static string AssistantText(string text, string? model = null) =>
        new JsonObject {
            ["type"]    = "message_end",
            ["message"] = new JsonObject {
                ["role"]    = "assistant",
                ["model"]   = model ?? StateModelId,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
        }.ToJsonString();

    public static string UserMessage(string text) =>
        new JsonObject {
            ["type"]    = "message_end",
            ["message"] = new JsonObject {
                ["role"]    = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
        }.ToJsonString();

    public const string AgentStart   = """{"type":"agent_start"}""";
    public const string AgentSettled = """{"type":"agent_settled"}""";

    /// <summary>Builds a runtime over a fresh <see cref="FakePiRpcProcess"/>. The caller owns
    /// disposing the runtime (which disposes the process).</summary>
    public static (PiRpcHostedAgentRuntime Runtime, FakePiRpcProcess Process) NewRuntime(
            string?    stateResponse  = null,
            bool       answerGetState = true,
            string?    requestedModel = RequestedModel,
            TimeSpan?  readyDeadline  = null,
            TimeSpan?  stopGrace      = null,
            Action?    onDisposed     = null) {
        var process = new FakePiRpcProcess {
            AutoStateResponse = answerGetState ? stateResponse ?? GetStateResponse() : null,
        };

        var runtime = new PiRpcHostedAgentRuntime(
            process:        process,
            logger:         NullLogger.Instance,
            agentId:        "agent-1",
            requestedModel: requestedModel,
            cwd:            "/w",
            readyDeadline:  readyDeadline,
            stopGrace:      stopGrace,
            onDisposed:     onDisposed);

        return (runtime, process);
    }

    /// <summary>The <c>id</c> the runtime stamped on the first command of the given
    /// <paramref name="type"/> it wrote — how a test answers a correlated command without the
    /// runtime having to expose its id generator.</summary>
    public static string? FirstCommandId(FakePiRpcProcess process, string type) {
        foreach (var line in process.Writes) {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("type").GetString() == type)
                return doc.RootElement.GetProperty("id").GetString();
        }

        return null;
    }
}
