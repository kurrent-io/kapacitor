# DEV-1484: Retrospective-only tool access (PR A)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the ~1.1 MB compacted trace embedded in the retrospective prompt with an on-demand MCP tool guide. The judge pulls what it needs (recap, errors, transcript slices) instead of us pre-shipping everything.

**Architecture:** Add a new session-scoped MCP server subcommand (`kapacitor mcp judge`) that exposes three tools wrapping existing server endpoints. `RunRetrospectiveAsync` launches the judge with `--mcp-config` pointing at that subcommand, `--allowedTools` listing the three MCP tools, `--max-turns 10`, 15-min timeout. Per-question judges and title generation are untouched. The server-side follow-up (default `sonnet[1m]` from Kurrent.Capacitor) stays out of scope of this plan.

**Tech Stack:** .NET 10 AOT, TUnit + WireMock.Net, `claude` CLI MCP protocol (stdio JSON-RPC 2024-11-05), existing HTTP endpoints.

---

## File structure

- Create: `src/kapacitor/Commands/McpJudgeServer.cs` — session-scoped MCP server (three tools, all accept `session_id` as tool arg; validates against a single expected session ID bound at launch time).
- Modify: `src/kapacitor/Program.cs:233-257` — add `kapacitor mcp judge --session <id>` subcommand dispatch.
- Modify: `src/kapacitor/Resources/prompt-eval-retrospective.txt` — drop `{TRACE_JSON}`, add tool-use guide block, tighten budget language.
- Modify: `src/kapacitor/Eval/EvalService.cs` — `RunRetrospectiveAsync` gets new ClaudeCliRunner parameters (MCP config, allowed tools, turn/timeout overrides); drop `traceJson` from `BuildRetrospectivePrompt` signature.
- Modify: `src/kapacitor/ClaudeCliRunner.cs` — add `mcpConfigJson` and `allowedTools` optional parameters; conditionally drop `--strict-mcp-config` / `--disallowedTools LSP` when MCP is enabled; take `maxTurns` and timeout from the caller (no hardcoded 5-min).
- Create: `test/kapacitor.Tests.Unit/McpJudgeServerTests.cs` — exercise each tool handler against a WireMock-backed server URL, asserting the right HTTP path is hit and the tool result payload is forwarded.
- Modify: `test/kapacitor.Tests.Unit/ClaudeCliRunnerTests.cs` — no new tests (ClaudeCliRunner changes are exercised end-to-end by the judge-server tests; parser tests stay).

**No server (Kurrent.Capacitor) changes** in this PR. All three endpoints already exist:
- `GET /api/sessions/{sessionId}/recap?chain=true` → `SessionQueryHandlers.GetSessionRecap`
- `GET /api/sessions/{sessionId}/errors?chain=true` → `SessionQueryHandlers.GetMetaSessionErrors`
- `GET /api/review/sessions/{sessionId}/transcript?file_path=&skip=&take=` → `ReviewApiHandlers.GetSessionTranscript`

---

## Task 1: Stub `McpJudgeServer` with no-op tool list

**Files:**
- Create: `src/kapacitor/Commands/McpJudgeServer.cs`
- Modify: `src/kapacitor/Program.cs:233-257`

- [ ] **Step 1: Create `McpJudgeServer` skeleton.** Mirror `McpReviewServer`'s stdio pump shape (read stdin line-by-line, parse JSON-RPC, respond to `initialize` and `tools/list`). For `tools/call` return a generic error for now. Accept `baseUrl` and `expectedSessionId` at construction.

```csharp
namespace kapacitor.Commands;

static class McpJudgeServer {
    public static async Task<int> RunAsync(string baseUrl, string expectedSessionId) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        var tools = BuildToolsList();

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        while (await reader.ReadLineAsync() is { } line) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? request;
            try { request = JsonNode.Parse(line)?.AsObject(); } catch { continue; }
            if (request is null) continue;

            var id     = request["id"];
            var method = request["method"]?.GetValue<string>();
            if (id is null) continue; // notification, no response

            var response = method switch {
                "initialize" => BuildInitializeResponse(id),
                "tools/list" => BuildToolsListResponse(id, tools),
                "tools/call" => await HandleToolCallAsync(id, request, client, baseUrl, expectedSessionId),
                _            => BuildErrorResponse(id, -32601, $"Method not found: {method}")
            };

            await writer.WriteLineAsync(response);
        }

        return 0;
    }

    static McpTool[] BuildToolsList() => [];

    static Task<string> HandleToolCallAsync(
            JsonNode id, JsonObject request, HttpClient client,
            string baseUrl, string expectedSessionId
        ) => Task.FromResult(BuildErrorResponse(id, -32601, "not implemented yet"));

    // Reuse the same BuildInitializeResponse / BuildToolsListResponse / BuildErrorResponse /
    // BuildToolResult / ToResponse / McpJsonContext helpers from McpReviewServer.cs.
    // For Task 1 just re-declare them private-static — Task 5 will extract the shared bits
    // if the duplication becomes painful.
}
```

- [ ] **Step 2: Wire subcommand in `Program.cs`.** Add a second branch in the `mcp` case:

```csharp
if (args[1] == "judge") {
    var session = GetArg(args, "--session");
    if (string.IsNullOrWhiteSpace(session)) {
        Console.Error.WriteLine("Usage: kapacitor mcp judge --session <sessionId>");
        return 1;
    }
    return await McpJudgeServer.RunAsync(baseUrl!, session);
}
```

Update the usage line to mention both: `kapacitor mcp review|judge …`.

- [ ] **Step 3: Build.**

Run: `dotnet build src/kapacitor/kapacitor.csproj`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Commit.**

```bash
git add src/kapacitor/Commands/McpJudgeServer.cs src/kapacitor/Program.cs
git commit -m "[DEV-1484] Add 'kapacitor mcp judge' subcommand scaffold"
```

---

## Task 2: Implement `get_session_recap` tool

**Files:**
- Modify: `src/kapacitor/Commands/McpJudgeServer.cs`
- Create: `test/kapacitor.Tests.Unit/McpJudgeServerTests.cs`

- [ ] **Step 1: Write the failing test.**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class McpJudgeServerTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    [Test]
    public async Task get_session_recap_forwards_session_id_and_chain_flag() {
        _server.Given(Request.Create()
                .WithPath("/api/sessions/abc-123/recap")
                .WithParam("chain", "true")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""[{"summary":"did a thing"}]"""));

        using var http = new HttpClient();
        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_recap",
            arguments: new JsonObject { ["session_id"] = "abc-123" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        await Assert.That(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString())
            .IsEqualTo("""[{"summary":"did a thing"}]""");
    }
}
```

Add an `internal static` helper `HandleToolCallForTests` on `McpJudgeServer` that invokes the tool-call handler with synthesised `id` / `request` and returns the same JSON-RPC envelope.

- [ ] **Step 2: Run the test. Expect FAIL** (tool not implemented).

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter '/*/*/McpJudgeServerTests/*'`

- [ ] **Step 3: Implement the tool.** In `HandleToolCallAsync`:

```csharp
var prBase = baseUrl.TrimEnd('/');
var arguments = request["params"]?.AsObject()?["arguments"]?.AsObject();
var toolName  = request["params"]?.AsObject()?["name"]?.GetValue<string>();

// Every tool validates session_id matches the one bound at launch — prevents
// a judge from fishing another session's data through this process.
var sessionId = arguments?["session_id"]?.GetValue<string>();
if (sessionId is null) return BuildToolResult(id, "Error: missing required argument: session_id", isError: true);
if (sessionId != expectedSessionId)
    return BuildToolResult(id, $"Error: session_id '{sessionId}' does not match this judge's bound session", isError: true);

var encoded = Uri.EscapeDataString(sessionId);
var httpResponse = toolName switch {
    "get_session_recap"  => await client.GetAsync($"{prBase}/api/sessions/{encoded}/recap?chain=true"),
    _                    => throw new ArgumentException($"Unknown tool: {toolName}")
};

var body = await httpResponse.Content.ReadAsStringAsync();
return !httpResponse.IsSuccessStatusCode
    ? BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true)
    : BuildToolResult(id, body);
```

Add the tool to `BuildToolsList`:

```csharp
new(
    "get_session_recap",
    "Get a short narrative recap of the session's user inputs, assistant replies, and tool invocations. "
  + "Start here — it's the cheapest way to orient yourself before pulling specific transcript slices.",
    new("object",
        new() { ["session_id"] = new("string", "Session ID to recap (must match the judge's bound session)") },
        ["session_id"])
)
```

- [ ] **Step 4: Run the test. Expect PASS.**

- [ ] **Step 5: Commit.**

```bash
git add src/kapacitor/Commands/McpJudgeServer.cs test/kapacitor.Tests.Unit/McpJudgeServerTests.cs
git commit -m "[DEV-1484] Implement get_session_recap MCP tool"
```

---

## Task 3: Implement `get_session_errors` tool

**Files:**
- Modify: `src/kapacitor/Commands/McpJudgeServer.cs`
- Modify: `test/kapacitor.Tests.Unit/McpJudgeServerTests.cs`

- [ ] **Step 1: Write failing test.** Same shape as Task 2, path `/api/sessions/{id}/errors?chain=true`, tool name `get_session_errors`.

- [ ] **Step 2: Run — FAIL.**

- [ ] **Step 3: Add another arm to the `toolName switch`:**

```csharp
"get_session_errors" => await client.GetAsync($"{prBase}/api/sessions/{encoded}/errors?chain=true"),
```

Add to `BuildToolsList`:

```csharp
new(
    "get_session_errors",
    "List errors and failures recorded in the session (tool errors, non-zero exits, exceptions). "
  + "Use when you need to ground a finding in specific failures instead of inferring from the transcript.",
    new("object",
        new() { ["session_id"] = new("string", "Session ID (must match the judge's bound session)") },
        ["session_id"])
)
```

- [ ] **Step 4: Run — PASS.**

- [ ] **Step 5: Commit.**

```bash
git commit -am "[DEV-1484] Implement get_session_errors MCP tool"
```

---

## Task 4: Implement `get_transcript` tool

**Files:**
- Modify: `src/kapacitor/Commands/McpJudgeServer.cs`
- Modify: `test/kapacitor.Tests.Unit/McpJudgeServerTests.cs`

- [ ] **Step 1: Write failing tests** covering: required `session_id`, optional `file_path` filter, optional `skip`/`take` pagination.

- [ ] **Step 2: Run — FAIL.**

- [ ] **Step 3: Implement.** Reuse the `BuildTranscriptUrl` helper copied verbatim from `McpReviewServer.cs:162-186` (keep it private-static in `McpJudgeServer` for now — if Task 5 proves a common base class is worth it, factor then). Switch arm:

```csharp
"get_transcript" => await client.GetAsync(BuildTranscriptUrl(baseUrl, arguments!)),
```

Tool definition matches `McpReviewServer`'s existing `get_transcript` entry verbatim.

- [ ] **Step 4: Run — PASS.**

- [ ] **Step 5: Commit.**

```bash
git commit -am "[DEV-1484] Implement get_transcript MCP tool for judge"
```

---

## Task 5: Rewrite the retrospective prompt

**Files:**
- Modify: `src/kapacitor/Resources/prompt-eval-retrospective.txt`

- [ ] **Step 1: Replace the `{TRACE_JSON}` block** with a tool-use guide.

Full new file content:

```
You are reviewing an already-scored Claude Code session. Thirteen judge calls have already produced per-question verdicts (score 1–5, finding, evidence, recommendation). Your job is to synthesise an actionable retrospective the user can apply to improve future sessions — NOT to re-score.

You MUST ground every point in the verdicts below OR in evidence pulled via the MCP tools. Do not invent. If the run was clean, say so plainly; do not pad.

# Session metadata
{SESSION_META}

# Per-question verdicts
{VERDICTS_JSON}

# Known patterns (retained from prior evals in this repo, by category)
{KNOWN_PATTERNS}

# Investigating the session

Use these MCP tools to pull session details on demand — the trace is NOT embedded in this prompt:

- `get_session_recap(session_id)` — START HERE. Short narrative of user inputs, assistant replies, and tool invocations. Cheapest way to orient yourself.
- `get_session_errors(session_id)` — explicit errors, tool failures, non-zero exits. Use when grounding issues.
- `get_transcript(session_id, skip, take, file_path?)` — paginated raw events. Use for targeted drill-downs; prefer recap and errors first.

Budget: at most 6 tool calls. Prefer recap + errors + one targeted transcript slice over paging the full trace. Only call `get_transcript` when recap/errors leave a specific question unanswered.

# Task
Produce a structured retrospective that calls out what went well, what went poorly, and concrete CLAUDE.md-style suggestions the user can apply to their next session in this project. Reference the known patterns where relevant.

Respond with exactly this JSON shape — no prose outside the JSON, no markdown code fences, no comments, no extra fields:
{
  "overall":     "<1-2 sentence headline capturing the run's overall character>",
  "strengths":   ["<short bullet>", ...],
  "issues":      ["<short bullet>", ...],
  "suggestions": ["<CLAUDE.md-style bullet, phrased as a rule or instruction>", ...]
}

Guidance:
- `overall` names the pattern, not the score. Example: "Clean run with careful test coverage but an unguarded destructive command on turn 412." For a truly uneventful session, a short line is fine: "Clean run, nothing worth retrospecting."
- `strengths` and `issues` each capture at most three items. Empty arrays are correct for a clean run or a trivially-failed run.
- `suggestions` hold at most five items and are the reason this feature exists. Phrase them so the user could paste one into CLAUDE.md without editing. Prefer specific over general; prefer one concrete rule over two vague ones. Reference file paths, commands, or patterns from the session where helpful.
- Good suggestion: "Before any `rm -rf`, require the agent to echo the expanded path and ask for confirmation."
- Bad suggestion:  "Be more careful with destructive commands."
- If there is genuinely nothing worth suggesting, return an empty `suggestions` array. Do not pad.
- Only reference a known pattern when the session actually shows (or fails to show) the same behaviour. Do not punish this run for a pattern unrelated to what you see.
- Emit only the four fields above. Do not include a `notes`, `metadata`, or any other top-level field.
```

- [ ] **Step 2: Commit.**

```bash
git commit -am "[DEV-1484] Retrospective prompt: drop embedded trace, add MCP tool guide"
```

---

## Task 6: Extend `ClaudeCliRunner` with MCP + allowed-tools + configurable turns/timeout

**Files:**
- Modify: `src/kapacitor/ClaudeCliRunner.cs`

- [ ] **Step 1: Add optional parameters.** Signature change on `RunAsync` and `RunCoreAsync`:

```csharp
public static async Task<ClaudeCliResult?> RunAsync(
        string            prompt,
        TimeSpan          timeout,
        Action<string>    log,
        string            model          = "haiku",
        int               maxTurns       = 1,
        bool              promptViaStdin = false,
        string?           jsonSchema     = null,
        string?           mcpConfigJson  = null,
        string[]?         allowedTools   = null,
        CancellationToken ct             = default
    )
```

- [ ] **Step 2: Conditionalise the MCP / tool flags.** In `RunCoreAsync`, replace the current hardcoded block:

```csharp
if (mcpConfigJson is null) {
    psi.ArgumentList.Add("--strict-mcp-config");
    psi.ArgumentList.Add("--tools");
    psi.ArgumentList.Add("");
    psi.ArgumentList.Add("--disallowedTools");
    psi.ArgumentList.Add("LSP");
} else {
    psi.ArgumentList.Add("--mcp-config");
    psi.ArgumentList.Add(mcpConfigJson);
    if (allowedTools is { Length: > 0 }) {
        psi.ArgumentList.Add("--allowedTools");
        psi.ArgumentList.Add(string.Join(",", allowedTools));
    }
}
psi.ArgumentList.Add("--disable-slash-commands");
```

The `--tools ""` / LSP-disallow path stays for title generation and per-question judges; the MCP path only engages when the caller opts in.

- [ ] **Step 3: Build + run all unit tests.**

```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expect `Build succeeded` and `245/245 passed`.

- [ ] **Step 4: Commit.**

```bash
git commit -am "[DEV-1484] ClaudeCliRunner: optional MCP config + allowed tools"
```

---

## Task 7: Switch `RunRetrospectiveAsync` to tool-based dispatch

**Files:**
- Modify: `src/kapacitor/Eval/EvalService.cs`

- [ ] **Step 1: Drop `traceJson` from the retrospective prompt.** In `BuildRetrospectivePrompt`:

```csharp
public static string BuildRetrospectivePrompt(
        string sessionMeta,
        string verdictsJson,
        string knownPatterns
    ) =>
    RetrospectivePromptTemplate
        .Replace("{SESSION_META}",   sessionMeta)
        .Replace("{VERDICTS_JSON}",  verdictsJson)
        .Replace("{KNOWN_PATTERNS}", knownPatterns);
```

Remove the `{TRACE_JSON}` substitution. The template (Task 5) no longer contains that placeholder.

- [ ] **Step 2: Update the call site in `RunRetrospectiveAsync`:**

```csharp
var prompt = BuildRetrospectivePrompt(sessionMeta, verdictsJson, knownPatterns);

var mcpConfig = JsonSerializer.Serialize(new {
    mcpServers = new {
        @kapacitor_review = new {
            command = "kapacitor",
            args    = new[] { "mcp", "judge", "--session", sessionId }
        }
    }
});

var allowedTools = new[] {
    "mcp__kapacitor_review__get_session_recap",
    "mcp__kapacitor_review__get_session_errors",
    "mcp__kapacitor_review__get_transcript"
};

var result = await ClaudeCliRunner.RunAsync(
    prompt,
    TimeSpan.FromMinutes(15),                      // bumped from 5 per DEV-1484 decision
    msg => observer.OnInfo($"  {msg}"),
    model:          JudgeModelFor(model),
    maxTurns:       RetrospectiveMaxTurns,         // new const = 10
    promptViaStdin: true,
    jsonSchema:     RetrospectiveJsonSchema,
    mcpConfigJson:  mcpConfig,
    allowedTools:   allowedTools,
    ct:             ct
);
```

Add `const int RetrospectiveMaxTurns = 10;` next to `JudgeMaxTurns`.

- [ ] **Step 3: Drop the unused `traceJson` parameter** threading. `RunRetrospectiveAsync` still takes it today because callers compute it; keep accepting it for the caller's shape but no longer pass it into the prompt builder. Remove later once call sites stabilise — out of scope for this PR.

- [ ] **Step 4: Build + run all unit tests.**

- [ ] **Step 5: Commit.**

```bash
git commit -am "[DEV-1484] Retrospective: use MCP judge server instead of embedded trace"
```

---

## Task 8: Republish local binary + verify end-to-end

**Files:** none (build + install only).

- [ ] **Step 1: AOT publish.**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release -r osx-arm64 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo "no AOT warnings"
```

Expect `no AOT warnings`.

- [ ] **Step 2: Copy + re-sign.**

```bash
cp src/kapacitor/bin/Release/net10.0/osx-arm64/publish/kapacitor \
   /opt/homebrew/lib/node_modules/@kurrent/kapacitor/node_modules/@kurrent/kapacitor-darwin-arm64/bin/kapacitor
codesign --force --sign - /opt/homebrew/lib/node_modules/@kurrent/kapacitor/node_modules/@kurrent/kapacitor-darwin-arm64/bin/kapacitor
kapacitor --version
```

- [ ] **Step 3: Manual smoke test.** Trigger a real retrospective via the dashboard or CLI against a session known to have ~1 MB compacted trace. Verify:
  - New transcript under `/tmp/kapacitor-claude-*/` shows `tool_use` entries for `mcp__kapacitor_review__*` tools.
  - No `compact_boundary` event.
  - Retrospective JSON parses correctly.
  - `RetrospectiveCompleted` fires on the SignalR channel.

- [ ] **Step 4: Open PR** once the smoke test passes. Title: `[DEV-1484] Retrospective tool access via kapacitor mcp judge`.

---

## Self-review checklist

- Spec coverage: DEV-1484 scope maps 1:1 — new subcommand (Tasks 1–4), drop `--strict-mcp-config` / `--disallowedTools LSP` when MCP is on (Task 6), `--max-turns 10` + 15-min timeout + `--disable-slash-commands` (Tasks 6–7), tool-use guide prompt (Task 5). Per-question judges stay text-only (no changes there). Decisions from the audit follow-up (skills off, budget via turns, 15-min, session_id via prompt) all applied.
- Placeholders: none — every step has code or an exact command.
- Type consistency: `McpTool`, `McpInputSchema`, `McpSchemaProperty` names match the existing `McpReviewServer` declarations (reused from `McpJsonContext`). Tool name `mcp__kapacitor_review__*` prefix matches the MCP server registration key (`@kapacitor_review` in the anon object serialises to `kapacitor-review` via the explicit mcpServers key — double-check the serialisation during Task 7; fall back to a handwritten JSON string if System.Text.Json's anon-object serialisation refuses AOT).
- Out of scope parked clearly: `--max-budget-usd`, server-side default model, session-scoped summary/search/untruncated-tool-result (that's DEV-1485).
