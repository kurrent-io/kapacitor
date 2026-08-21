using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Per-tool-call telemetry for the kcap MCP servers. The interesting unit is the CALL, not the
/// process: recap and memory are used through MCP rather than as terminal verbs, so a
/// process-start event would say nothing about usage.
///
/// Tool arguments are never recorded — they carry repo paths, prompts, and session ids.
/// </summary>
public static class McpTelemetry {
    // MCP servers are long-lived (a session can run for hours), so without a periodic flush
    // every captured event would sit queued until process exit — and a server killed alongside
    // its harness (the common way one of these ends) would never report anything at all.
    const int FlushEvery = 20;

    static int _sinceFlush;

    public static void ToolCalled(string server, string tool, bool ok, long durationMs) {
        CliTelemetry.Capture("mcp_tool_called", new JsonObject {
            ["server"]      = server,
            ["tool"]        = tool,
            ["ok"]          = ok,
            ["duration_ms"] = durationMs,
        });

        if (Interlocked.Increment(ref _sinceFlush) % FlushEvery == 0)
            _ = CliTelemetry.FlushAndClose();
    }

    /// <summary>
    /// Reads params.name defensively, for the telemetry wrapper only: a malformed tools/call
    /// request — non-object params, a missing name, or a name of the wrong JSON type — must
    /// degrade to "unknown" and never throw. Each server's own dispatch guards its OWN
    /// failures independently, but this read happens BEFORE that dispatch runs, so telemetry
    /// cannot rely on it and must be safe on its own.
    /// </summary>
    internal static string SafeToolName(JsonObject request) {
        try {
            return request["params"]?["name"]?.GetValue<string>() ?? "unknown";
        } catch {
            return "unknown";
        }
    }

    /// <summary>
    /// Every kcap MCP server's tools/call dispatch catches its own exceptions (invalid URLs,
    /// unknown tools, auth failures, ...) and returns a normal JSON-RPC result shaped
    /// <c>{"result":{"content":[...],"isError":true}}</c> rather than throwing or an
    /// <c>{"error":...}</c> envelope — so "the call returned a string" says nothing about
    /// success. This reads that flag out of the response the dispatcher already produced.
    /// Read defensively, for the telemetry wrapper only: anything that isn't an explicit
    /// `isError: true` (missing, false, or unparseable — the latter would itself be a
    /// serialization bug elsewhere) counts as success rather than throwing here.
    /// </summary>
    internal static bool ResponseOk(string responseJson) {
        try {
            return JsonNode.Parse(responseJson)?["result"]?["isError"]?.GetValue<bool>() != true;
        } catch {
            return true;
        }
    }
}
