# Spectre.Console CLI UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adopt Spectre.Console for kapacitor's two most human-facing commands — `setup` and `history` — replacing ad-hoc `Console.Write*` with prompts, status spinners, rules, grids, and pinned progress while preserving current behavior on piped/non-TTY output and keeping the AOT/trim publish clean.

**Architecture:** Add the `Spectre.Console` rendering package (NOT `Spectre.Console.Cli`). Extend `SessionImporter` with an optional `IProgress<ImportProgress>` hook so the importer remains console-agnostic while `HistoryCommand` drives a live footer. Introduce a tiny private `HistoryDisplay` struct inside `HistoryCommand.cs` that branches once on `Console.IsOutputRedirected` and exposes completion-line/footer-update methods to the main loop. `SetupCommand` calls `AnsiConsole` directly (prompts + status + grid). No shared console abstraction across commands.

**Tech Stack:** .NET 10, NativeAOT (`PublishAot=true`, `TrimMode=full`), TUnit test framework, WireMock.Net for HTTP mocking, Spectre.Console `0.*`.

**Spec:** `docs/superpowers/specs/2026-04-23-spectre-console-cli-ux-design.md`

---

## File map

- **Create:** `src/kapacitor/Commands/ImportProgress.cs` — event record hierarchy for importer progress callbacks.
- **Modify:** `src/kapacitor/kapacitor.csproj` — add `Spectre.Console` package reference.
- **Modify:** `src/kapacitor/Commands/SessionImporter.cs` — add optional `IProgress<ImportProgress>?` parameter to `ImportSessionAsync` and `SendTranscriptBatches`; fire `BatchFlushed` / `SubagentStarted` / `SubagentFinished` events.
- **Modify:** `src/kapacitor/Commands/SetupCommand.cs` — replace `Console.Write`/`ReadLine` with Spectre prompts, rules, status spinner, final grid.
- **Modify:** `src/kapacitor/Commands/HistoryCommand.cs` — introduce private `HistoryDisplay` struct; branch TTY vs piped; Spectre `Progress` footer + streamed completion lines; background-phase progress block with individual failure streaming.
- **Create:** `test/kapacitor.Tests.Unit/SessionImporterProgressTests.cs` — TDD tests for the new `IProgress` wiring.

---

## Task 1: Add Spectre.Console package and baseline AOT gate

**Files:**
- Modify: `src/kapacitor/kapacitor.csproj`

- [ ] **Step 1: Capture baseline publish output (no changes yet)**

Run:
```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' | tee /tmp/kapacitor-aot-baseline.txt
```
Expected: zero lines (baseline should already be clean per CLAUDE.md).

If non-empty, stop — investigate existing warnings before adding Spectre.

- [ ] **Step 2: Add the package reference**

Edit `src/kapacitor/kapacitor.csproj`. In the existing `<ItemGroup>` that contains `<PackageReference>` entries (the one with `IdentityModel.OidcClient`, `Microsoft.AspNetCore.SignalR.Client`, etc.), add:

```xml
<PackageReference Include="Spectre.Console" Version="0.*" />
```

- [ ] **Step 3: Restore and build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```
Expected: build succeeds.

- [ ] **Step 4: Verify AOT publish is still clean**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: zero lines. If warnings appear, they will be specific to Spectre's annotations — capture the output, stop, and post in the ticket before proceeding.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/kapacitor.csproj
git commit -m "[DEV-1573] Add Spectre.Console package reference

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Define ImportProgress event hierarchy

**Files:**
- Create: `src/kapacitor/Commands/ImportProgress.cs`

- [ ] **Step 1: Create the file**

Write `src/kapacitor/Commands/ImportProgress.cs`:

```csharp
namespace kapacitor.Commands;

/// <summary>
/// Progress events emitted by <see cref="SessionImporter.ImportSessionAsync"/>
/// and <see cref="SessionImporter.SendTranscriptBatches"/> for UI layers that
/// want to render a live view of an in-flight import.
/// </summary>
public abstract record ImportProgress;

/// <summary>Fired after a transcript batch is flushed to the server.</summary>
public sealed record BatchFlushed(int LinesAdded) : ImportProgress;

/// <summary>Fired when the importer begins streaming a subagent's transcript inline.</summary>
public sealed record SubagentStarted(string AgentId) : ImportProgress;

/// <summary>Fired after a subagent's transcript has been fully streamed.</summary>
public sealed record SubagentFinished(string AgentId, int LinesSent) : ImportProgress;
```

- [ ] **Step 2: Build to check it compiles**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/kapacitor/Commands/ImportProgress.cs
git commit -m "[DEV-1573] Define ImportProgress event hierarchy

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Thread IProgress through SendTranscriptBatches (TDD — BatchFlushed)

**Files:**
- Create: `test/kapacitor.Tests.Unit/SessionImporterProgressTests.cs`
- Modify: `src/kapacitor/Commands/SessionImporter.cs:425-474` (signature + firing site inside the two flush points)

- [ ] **Step 1: Write the failing test**

Create `test/kapacitor.Tests.Unit/SessionImporterProgressTests.cs`:

```csharp
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class SessionImporterProgressTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SendTranscriptBatches_fires_BatchFlushed_per_100_lines() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // Write 250 non-blank JSONL lines → expect 3 flushes: 100, 100, 50
        var path = Path.GetTempFileName();
        try {
            await File.WriteAllLinesAsync(path, Enumerable.Range(0, 250).Select(i =>
                $$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"line-{{i}}"}}"""
            ));

            var events = new List<ImportProgress>();
            var progress = new Progress<ImportProgress>(events.Add);

            using var client = new HttpClient();
            var totalSent = await SessionImporter.SendTranscriptBatches(
                client, _server.Url!, sessionId: "test", filePath: path,
                agentId: null, startLine: 0, progress: progress
            );

            await Assert.That(totalSent).IsEqualTo(250);

            // Progress<T> marshals via SynchronizationContext; give it a tick
            await Task.Delay(50);

            var flushes = events.OfType<BatchFlushed>().ToList();
            await Assert.That(flushes.Count).IsEqualTo(3);
            await Assert.That(flushes[0].LinesAdded).IsEqualTo(100);
            await Assert.That(flushes[1].LinesAdded).IsEqualTo(100);
            await Assert.That(flushes[2].LinesAdded).IsEqualTo(50);
        } finally {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails (method signature doesn't exist yet)**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionImporterProgressTests/*"
```
Expected: compilation error — `SendTranscriptBatches` has no `progress` parameter.

- [ ] **Step 3: Update the signature and fire BatchFlushed at both flush sites**

In `src/kapacitor/Commands/SessionImporter.cs`, change `SendTranscriptBatches` (currently lines 425-474). Add `IProgress<ImportProgress>? progress = null` as the last parameter, and call `progress?.Report(new BatchFlushed(batchLines.Count))` immediately after each `await PostTranscriptBatch(...)`.

Replace the method body with:

```csharp
internal static async Task<int> SendTranscriptBatches(
        HttpClient                 httpClient,
        string                     baseUrl,
        string                     sessionId,
        string                     filePath,
        string?                    agentId,
        int                        startLine,
        IProgress<ImportProgress>? progress = null
    ) {
    if (!File.Exists(filePath)) return 0;

    var       totalSent        = 0;
    var       batchLines       = new List<string>();
    var       batchLineNumbers = new List<int>();
    const int batchSize        = 100;

    await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var       reader = new StreamReader(stream);

    var lineIndex = 0;

    while (await reader.ReadLineAsync() is { } line) {
        if (lineIndex < startLine) {
            lineIndex++;

            continue;
        }

        if (!string.IsNullOrWhiteSpace(line)) {
            batchLines.Add(line);
            batchLineNumbers.Add(lineIndex);
        }

        lineIndex++;

        if (batchLines.Count >= batchSize) {
            await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId, batchLines, batchLineNumbers);
            var flushed = batchLines.Count;
            totalSent += flushed;
            progress?.Report(new BatchFlushed(flushed));
            batchLines.Clear();
            batchLineNumbers.Clear();
        }
    }

    if (batchLines.Count > 0) {
        await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId, batchLines, batchLineNumbers);
        var flushed = batchLines.Count;
        totalSent += flushed;
        progress?.Report(new BatchFlushed(flushed));
    }

    return totalSent;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionImporterProgressTests/*"
```
Expected: 1 test passed.

- [ ] **Step 5: Run the full unit suite to confirm nothing else broke**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/SessionImporter.cs test/kapacitor.Tests.Unit/SessionImporterProgressTests.cs
git commit -m "[DEV-1573] Emit BatchFlushed progress events from SendTranscriptBatches

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Wire progress into ImportSessionAsync and subagent lifecycle (TDD — SubagentStarted/Finished)

**Files:**
- Modify: `test/kapacitor.Tests.Unit/SessionImporterProgressTests.cs` (add test)
- Modify: `src/kapacitor/Commands/SessionImporter.cs:17-116` (add progress param to `ImportSessionAsync`, thread through batch flushes and `SendAgentLifecycle`)
- Modify: `src/kapacitor/Commands/SessionImporter.cs:369-420` (`SendAgentLifecycle` accepts progress, fires Subagent events around the transcript stream)

- [ ] **Step 1: Write the failing test**

Append to `test/kapacitor.Tests.Unit/SessionImporterProgressTests.cs` (inside the class, after the first test):

```csharp
[Test]
public async Task ImportSessionAsync_fires_subagent_events_around_inline_import() {
    _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
        .RespondWith(Response.Create().WithStatusCode(200));
    _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
        .RespondWith(Response.Create().WithStatusCode(200));
    _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
        .RespondWith(Response.Create().WithStatusCode(200));

    // Layout:
    //   <tmp>/<sessionName>.jsonl
    //   <tmp>/<sessionName>/subagents/agent-<id>.jsonl
    var tmp = Directory.CreateTempSubdirectory("kapacitor-import-test");
    try {
        var sessionName = Guid.NewGuid().ToString("N");
        var sessionPath = Path.Combine(tmp.FullName, $"{sessionName}.jsonl");
        var agentsDir   = Path.Combine(tmp.FullName, sessionName, "subagents");
        Directory.CreateDirectory(agentsDir);

        var agentId = Guid.NewGuid().ToString("N");
        var agentPath = Path.Combine(agentsDir, $"agent-{agentId}.jsonl");

        // Parent transcript: one assistant tool_use launching a Task, plus an
        // agent_progress event that pins the interleave line.
        await File.WriteAllLinesAsync(sessionPath, [
            $$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu1","name":"Task","input":{"subagent_type":"code-reviewer"}}]}}""",
            $$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{agentId}}"}}""",
            $$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"after"}}"""
        ]);

        // Agent transcript: 3 lines → 1 BatchFlushed of 3.
        await File.WriteAllLinesAsync(agentPath, [
            $$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"a1"}}""",
            $$"""{"type":"assistant","timestamp":"2026-03-15T10:00:01Z","message":{"content":"a2"}}""",
            $$"""{"type":"user","timestamp":"2026-03-15T10:00:02Z","message":{"content":"a3"}}"""
        ]);

        var events   = new List<ImportProgress>();
        var progress = new Progress<ImportProgress>(events.Add);

        using var client = new HttpClient();
        var result = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, sessionPath, sessionId: "s1",
            new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
            progress: progress
        );

        await Task.Delay(50);

        await Assert.That(result.AgentIds).Contains(agentId);

        var ordered = events.ToList();
        // Expect SubagentStarted(agentId) before any BatchFlushed from the agent,
        // then SubagentFinished(agentId, 3), then parent BatchFlushed(s).
        var startIdx  = ordered.FindIndex(e => e is SubagentStarted s && s.AgentId == agentId);
        var finishIdx = ordered.FindIndex(e => e is SubagentFinished f && f.AgentId == agentId);

        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(finishIdx).IsGreaterThan(startIdx);
        await Assert.That(((SubagentFinished)ordered[finishIdx]).LinesSent).IsEqualTo(3);
    } finally {
        tmp.Delete(recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionImporterProgressTests/ImportSessionAsync_fires_subagent_events_around_inline_import"
```
Expected: compilation error — `ImportSessionAsync` has no `progress` parameter.

- [ ] **Step 3: Thread progress through `SendAgentLifecycle`**

In `src/kapacitor/Commands/SessionImporter.cs`, change `SendAgentLifecycle` (currently starts at line 369). Add `IProgress<ImportProgress>? progress` as the last parameter. Fire `SubagentStarted` before the transcript call and `SubagentFinished` after it, capturing the returned line count. Replace the method with:

```csharp
static async Task<int> SendAgentLifecycle(
        HttpClient                 httpClient,
        string                     baseUrl,
        string                     sessionId,
        string                     agentId,
        string?                    agentType,
        string                     agentPath,
        string                     cwd,
        string                     sessionTranscriptPath,
        IProgress<ImportProgress>? progress
    ) {
    var resolvedAgentType = agentType ?? "task";

    var agentStartHook = new JsonObject {
        ["session_id"]      = sessionId,
        ["transcript_path"] = sessionTranscriptPath,
        ["cwd"]             = cwd,
        ["hook_event_name"] = "subagent_start",
        ["agent_id"]        = agentId,
        ["agent_type"]      = resolvedAgentType
    };

    try {
        using var agentStartContent = new StringContent(agentStartHook.ToJsonString(), Encoding.UTF8, "application/json");
        await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/subagent-start", agentStartContent);
    } catch {
        // Best effort
    }

    progress?.Report(new SubagentStarted(agentId));
    var agentLines = await SendTranscriptBatches(httpClient, baseUrl, sessionId, agentPath, agentId, startLine: 0, progress: progress);
    progress?.Report(new SubagentFinished(agentId, agentLines));

    var agentStopHook = new JsonObject {
        ["session_id"]             = sessionId,
        ["transcript_path"]        = sessionTranscriptPath,
        ["cwd"]                    = cwd,
        ["hook_event_name"]        = "subagent_stop",
        ["agent_id"]               = agentId,
        ["agent_type"]             = resolvedAgentType,
        ["stop_hook_active"]       = false,
        ["agent_transcript_path"]  = agentPath,
        ["last_assistant_message"] = ""
    };

    try {
        using var agentStopContent = new StringContent(agentStopHook.ToJsonString(), Encoding.UTF8, "application/json");
        await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/subagent-stop", agentStopContent);
    } catch {
        // Best effort
    }

    return agentLines;
}
```

- [ ] **Step 4: Thread progress through `ImportSessionAsync` and fire BatchFlushed on parent flushes**

In `src/kapacitor/Commands/SessionImporter.cs`, change `ImportSessionAsync` (starting line 17). Add `IProgress<ImportProgress>? progress = null` as the last parameter. Pass it to every `PostTranscriptBatch` flush site (call `progress?.Report(new BatchFlushed(batchLines.Count))` right after each flush) and to `SendAgentLifecycle` calls.

Replace the method body with:

```csharp
internal static async Task<ImportResult> ImportSessionAsync(
        HttpClient                 httpClient,
        string                     baseUrl,
        string                     transcriptPath,
        string                     sessionId,
        SessionMetadata            metadata,
        string?                    encodedCwd,
        IProgress<ImportProgress>? progress = null
    ) {
    if (!File.Exists(transcriptPath))
        return new(sessionId, [], 0);

    var cwd = metadata.Cwd ?? (encodedCwd is not null ? DecodeCwdFromDirName(encodedCwd) : null) ?? "";

    var agentTranscripts = DiscoverAgentTranscripts(transcriptPath);
    var agentMap         = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var (agentId, agentPath) in agentTranscripts) {
        agentMap[agentId] = agentPath;
    }

    var scan           = ScanAgentLifecycle(transcriptPath);
    var agentFirstLine = scan.FirstLineByAgent;
    var agentTypes     = scan.AgentTypeByAgent;

    var sentAgents = new HashSet<string>(StringComparer.Ordinal);
    var agentIds   = new List<string>();
    var totalSent  = 0;

    var       batchLines       = new List<string>();
    var       batchLineNumbers = new List<int>();
    const int batchSize        = 100;

    await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var       reader = new StreamReader(stream);

    var lineIndex = 0;

    while (await reader.ReadLineAsync() is { } line) {
        foreach (var (agentId, firstLine) in agentFirstLine) {
            if (firstLine == lineIndex && !sentAgents.Contains(agentId) && agentMap.TryGetValue(agentId, out var agentPath)) {
                if (batchLines.Count > 0) {
                    await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers);
                    var flushed = batchLines.Count;
                    totalSent += flushed;
                    progress?.Report(new BatchFlushed(flushed));
                    batchLines.Clear();
                    batchLineNumbers.Clear();
                }

                agentTypes.TryGetValue(agentId, out var agentType);
                await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentType, agentPath, cwd, transcriptPath, progress);
                sentAgents.Add(agentId);
                agentIds.Add(agentId);
            }
        }

        if (!string.IsNullOrWhiteSpace(line)) {
            batchLines.Add(line);
            batchLineNumbers.Add(lineIndex);
        }

        lineIndex++;

        if (batchLines.Count >= batchSize) {
            await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers);
            var flushed = batchLines.Count;
            totalSent += flushed;
            progress?.Report(new BatchFlushed(flushed));
            batchLines.Clear();
            batchLineNumbers.Clear();
        }
    }

    if (batchLines.Count > 0) {
        await PostTranscriptBatch(httpClient, baseUrl, sessionId, agentId: null, batchLines, batchLineNumbers);
        var flushed = batchLines.Count;
        totalSent += flushed;
        progress?.Report(new BatchFlushed(flushed));
    }

    foreach (var (agentId, agentPath) in agentTranscripts) {
        if (!sentAgents.Contains(agentId)) {
            agentTypes.TryGetValue(agentId, out var agentType);
            await SendAgentLifecycle(httpClient, baseUrl, sessionId, agentId, agentType, agentPath, cwd, transcriptPath, progress);
            sentAgents.Add(agentId);
            agentIds.Add(agentId);
        }
    }

    return new ImportResult(sessionId, agentIds, totalSent);
}
```

- [ ] **Step 5: Run the new test**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionImporterProgressTests/*"
```
Expected: 2 tests passed.

- [ ] **Step 6: Run the full unit suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Commands/SessionImporter.cs test/kapacitor.Tests.Unit/SessionImporterProgressTests.cs
git commit -m "[DEV-1573] Emit subagent progress events from ImportSessionAsync

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Setup wizard — swap output primitives for Spectre (rules, status, grid)

This task changes output only. Prompts still use `Console.Write`/`ReadLine` so the wizard's keyboard UX is unchanged and we can bisect if something regresses.

**Files:**
- Modify: `src/kapacitor/Commands/SetupCommand.cs`

- [ ] **Step 1: Add the using and wrap section headers in `Rule`**

At the top of `src/kapacitor/Commands/SetupCommand.cs`, add:

```csharp
using Spectre.Console;
```

Then in `HandleAsync`, replace each of the 5 lines like `await Console.Out.WriteLineAsync("Step 1/5: Server");` with a rule. The current lines are:

- `"Step 1/5: Server"` at around line 34
- `"Step 2/5: Login"` at around line 74
- `"Step 3/5: Default session visibility"` at around line 94
- `"Step 4/5: Claude Code Plugin"` at around line 136
- `"Step 5/5: Agent Daemon"` at around line 202

Replace each line with:

```csharp
AnsiConsole.Write(new Rule("[yellow]Step 1/5 — Server[/]").LeftJustified());
```

(Substitute the per-step title.)

Also replace the welcome lines at the top:

```csharp
await Console.Out.WriteLineAsync();
await Console.Out.WriteLineAsync("Welcome to Kapacitor!");
await Console.Out.WriteLineAsync();
```

with:

```csharp
AnsiConsole.Write(new Rule("[bold green]Welcome to Kapacitor[/]").Centered());
```

- [ ] **Step 2: Wrap the reachability probe in a `Status()` spinner**

Find the block starting with `Console.Write("  Checking server... ");` (around line 59). Replace the whole try/catch block:

```csharp
Console.Write("  Checking server... ");
string provider;

try {
    provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl);
    await Console.Out.WriteLineAsync($"✓ Reachable. Auth provider: {provider}");
} catch (Exception ex) {
    await Console.Error.WriteLineAsync($"✗ Cannot reach server: {ex.Message}");

    return 1;
}
```

with:

```csharp
string provider;

try {
    provider = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Checking server…", async _ =>
            await HttpClientExtensions.DiscoverProviderAsync(serverUrl));

    AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{Markup.Escape(provider)}[/]");
} catch (Exception ex) {
    AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");

    return 1;
}
```

- [ ] **Step 3: Replace the final summary with a Grid**

Find the trailing block starting `await Console.Out.WriteLineAsync("Setup complete!");` (around line 235). Replace it and the five subsequent `Console.Out.WriteLineAsync` calls through `"  Optional: start the agent daemon…"` with:

```csharp
AnsiConsole.Write(new Rule("[green]Setup complete[/]").LeftJustified());

var grid = new Grid().AddColumn().AddColumn();
grid.AddRow("[bold]Server[/]",     Markup.Escape(serverUrl));
grid.AddRow("[bold]Visibility[/]", Markup.Escape(defaultVisibility));
grid.AddRow("[bold]Daemon[/]",     Markup.Escape(daemonName));

if (finalTokens is not null) {
    grid.AddRow("[bold]Auth[/]", Markup.Escape($"{finalTokens.GitHubUsername} ({finalTokens.Provider})"));
}

grid.AddRow("[bold]Config[/]", Markup.Escape(AppConfig.GetConfigPath()));

AnsiConsole.Write(grid);
AnsiConsole.MarkupLine("\n[dim]Optional:[/] start the agent daemon with [cyan]kapacitor agent start -d[/]");
```

- [ ] **Step 4: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```
Expected: success.

- [ ] **Step 5: Run existing tests (they exercise file I/O and pure helpers; must still pass)**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/SetupCommandTests/*"
```
Expected: all pass.

- [ ] **Step 6: AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: zero lines.

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Commands/SetupCommand.cs
git commit -m "[DEV-1573] Setup wizard: Spectre rules, status spinner, final grid

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Setup wizard — replace prompts with Spectre prompts

**Files:**
- Modify: `src/kapacitor/Commands/SetupCommand.cs`

- [ ] **Step 1: Server URL prompt**

Find the block (around line 44):

```csharp
Console.Write("  Enter your Capacitor server URL: ");
serverUrl = Console.ReadLine()?.Trim() ?? "";

if (string.IsNullOrEmpty(serverUrl)) {
    await Console.Error.WriteLineAsync("  Server URL is required.");

    return 1;
}
```

Replace with:

```csharp
serverUrl = AnsiConsole.Prompt(
    new TextPrompt<string>("Capacitor server URL:")
        .Validate(u => !string.IsNullOrWhiteSpace(u)
            ? ValidationResult.Success()
            : ValidationResult.Error("[red]URL cannot be empty[/]")));
```

- [ ] **Step 2: Re-run confirmation prompt**

Find the block (around line 22):

```csharp
if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
    Console.Write($"Already configured for {existing.ServerUrl} as {existingTokens.GitHubUsername}. Re-run setup? [y/N] ");
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

    if (answer is not "y" and not "yes") {
        await Console.Out.WriteLineAsync("Setup cancelled.");

        return 0;
    }
}
```

Replace with:

```csharp
if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
    var rerun = AnsiConsole.Prompt(
        new ConfirmationPrompt($"Already configured for [cyan]{Markup.Escape(existing.ServerUrl)}[/] as [cyan]{Markup.Escape(existingTokens.GitHubUsername ?? "?")}[/]. Re-run setup?")
            { DefaultValue = false });

    if (!rerun) {
        AnsiConsole.MarkupLine("[dim]Setup cancelled.[/]");

        return 0;
    }
}
```

- [ ] **Step 3: Default visibility selection**

Find the block (around lines 97-131) that prints the three numbered options and reads a choice. Replace the whole interactive branch (the else after `if (noPrompt)`) with:

```csharp
} else {
    defaultVisibility = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("How should your sessions be visible to others by default?")
            .AddChoices("org_public", "private", "public")
            .UseConverter(v => v switch {
                "private"    => "All private — only you can see your sessions",
                "org_public" => "Org repos public, others private (default)",
                "public"     => "All public — everyone can see all your sessions",
                _            => v
            }));
}
```

(The preceding `AnsiConsole.Out.WriteLineAsync` calls that list the options can be deleted — the SelectionPrompt renders them.)

- [ ] **Step 4: Plugin scope selection**

Find the matching block (around lines 149-174). Replace the interactive branch with:

```csharp
} else {
    pluginScope = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Where should the plugin be installed?")
            .AddChoices("user", "project", "skip")
            .UseConverter(v => v switch {
                "user"    => "User-wide — all Claude Code sessions (recommended)",
                "project" => "This project only",
                "skip"    => "Skip — I'll install it manually",
                _         => v
            }));
}
```

(Delete the preceding option-list `WriteLineAsync` calls.)

- [ ] **Step 5: Daemon name prompt**

Find (around line 210):

```csharp
Console.Write($"  Daemon name [{defaultName}]: ");
var input = Console.ReadLine()?.Trim();
daemonName = string.IsNullOrEmpty(input) ? defaultName : input;
```

Replace with:

```csharp
daemonName = AnsiConsole.Prompt(
    new TextPrompt<string>("Daemon name:")
        .DefaultValue(defaultName)
        .ShowDefaultValue());
```

- [ ] **Step 6: Build and AOT check**

```bash
dotnet build src/kapacitor/kapacitor.csproj && dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: build succeeds, publish grep returns zero lines.

- [ ] **Step 7: Smoke test interactively**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- setup
```
Expected: wizard renders with colored rules, status spinner during server probe, arrow-key selection for visibility and plugin scope, defaulted daemon name, and a final two-column grid.

Cancel with Ctrl+C partway through if you don't want to save config.

- [ ] **Step 8: Smoke test `--no-prompt`**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- setup --no-prompt \
  --server-url https://example.invalid --default-visibility private --plugin-scope skip
```
Expected: rules and grid render; reachability fails with `✗ Cannot reach server:` and exit code 1. (Using `.invalid` guarantees DNS failure.)

- [ ] **Step 9: Run tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/SetupCommandTests/*"
```
Expected: all pass.

- [ ] **Step 10: Commit**

```bash
git add src/kapacitor/Commands/SetupCommand.cs
git commit -m "[DEV-1573] Setup wizard: Spectre prompts (text, selection, confirmation)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Introduce HistoryDisplay and gate TTY vs piped

This task adds a private `HistoryDisplay` struct at the top of `HistoryCommand.cs` that owns all user-facing output during the import loop. Both modes (`Tty` vs `Plain`) route through the same methods — the TTY path is added in the next task, and for now both branches emit the current plain lines. This keeps the diff small and gives us a bisect point.

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

- [ ] **Step 1: Add usings**

At the top of `src/kapacitor/Commands/HistoryCommand.cs`, add:

```csharp
using Spectre.Console;
```

- [ ] **Step 2: Add the HistoryDisplay struct and factory**

Immediately inside `static class HistoryCommand {` (at the top of the class body, above `HandleHistory`), add:

```csharp
readonly struct HistoryDisplay {
    public bool Tty { get; init; }
    // Non-null in Tty mode, null in Plain mode.
    public ProgressTask? Footer { get; init; }

    public void SetFooterSession(string sessionIdShort, int totalLines) {
        if (Footer is null) return;
        Footer.Description = $"[green]Importing[/] {(int)Footer.Value}/{(int)Footer.MaxValue} · {Markup.Escape(sessionIdShort)}: 0/{totalLines} lines";
    }

    public void AdvanceFooterLines(int linesDone, int linesTotal, string sessionIdShort, string? agentSuffixId) {
        if (Footer is null) return;
        var suffix = agentSuffixId is null ? "" : $" ↳ subagent {Markup.Escape(agentSuffixId)}";
        Footer.Description = $"[green]Importing[/] {(int)Footer.Value}/{(int)Footer.MaxValue} · {Markup.Escape(sessionIdShort)}: {linesDone}/{linesTotal} lines{suffix}";
    }

    public void Line(string plain, string? markup = null) {
        if (Tty) AnsiConsole.MarkupLine(markup ?? Markup.Escape(plain));
        else     Console.WriteLine(plain);
    }

    public static HistoryDisplay Create() {
        var tty = !Console.IsOutputRedirected;

        return new HistoryDisplay { Tty = tty, Footer = null };
    }
}
```

(`Footer` stays null in this task. The Progress block and its task are added in Task 8.)

- [ ] **Step 3: Wire HistoryDisplay through HandleHistory output lines**

Inside `HandleHistory`, near the top (right after `using var httpClient = …`), create the display:

```csharp
var display = HistoryDisplay.Create();
```

Then, throughout the method, replace user-facing `await Console.Out.WriteLineAsync(...)` and `Console.Write(...)` calls related to session processing with `display.Line(...)`. Specifically:

- `await Console.Out.WriteLineAsync("Discovering sessions...");` → `display.Line("Discovering sessions...");`
- `await Console.Out.WriteLineAsync("No Claude Code projects directory found.");` → `display.Line("No Claude Code projects directory found.");`
- `await Console.Out.WriteLineAsync("No transcript files found.");` → `display.Line("No transcript files found.");`
- `await Console.Out.WriteLineAsync($"Found {transcriptFiles.Count} session…")` → `display.Line($"Found …");`
- Every `await Console.Out.WriteLineAsync($"Skipping {sessionId} [...]");` → `display.Line($"Skipping {sessionId} […]");`
- `Console.Write($"Loading {sessionId}... ");` and its paired `await Console.Out.WriteLineAsync($"{linesSent} lines [new]");` → one combined `display.Line($"Loading {sessionId}... {linesSent} lines [new]");` called **after** the import completes.
- `await Console.Out.WriteLineAsync($"  {importResult.AgentIds.Count} agent{...} imported inline");` — **delete this line** entirely. The per-subagent streaming line is emitted from the progress callback in the next task; for this task, agents stream nothing (no regression — the current summary disappears but the non-TTY pipe still reports `Loading … N lines [new]`). We deliberately accept this one-task gap to keep the diff small; Task 8 restores subagent visibility.
- The progress/count bookkeeping (`loaded`, `resumed`, `skipped`, `errored`, etc.) is unchanged.
- Final summary lines at the bottom → `display.Line($"Done: {loaded} loaded, …");`
- `await Console.Out.WriteLineAsync($"Waiting for {backgroundTasks.Count} background task(s) (titles/summaries)...");` → `display.Line($"Waiting …");`
- The summary-parts line → `display.Line($"  {string.Join(", ", parts)}");`

Leave `Console.Error.WriteLineAsync` calls alone — stderr is not user-UX output.

- [ ] **Step 4: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```
Expected: success.

- [ ] **Step 5: Run tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```
Expected: all pass.

- [ ] **Step 6: AOT publish check**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: zero lines.

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "[DEV-1573] Introduce HistoryDisplay abstraction (plain-only for now)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: History — TTY mode with pinned Progress footer and streaming completion lines

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

- [ ] **Step 1: Extract the per-session loop into a local method**

In `HandleHistory`, locate the `foreach (var (sessionId, filePath, encodedCwd) in transcriptFiles) { … }` loop. Extract the body into a local method `ProcessSession` that closes over counters, or keep it inline — the exact refactor is the author's choice, but the loop must be callable from inside a Spectre `Progress.Start` callback.

The cleanest approach: convert `HandleHistory` so that after `display` is created, the session loop runs inside:

```csharp
if (display.Tty) {
    await AnsiConsole.Progress()
        .AutoClear(false)
        .HideCompleted(false)
        .Columns(new SpinnerColumn(), new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
        .StartAsync(async ctx => {
            var footer = ctx.AddTask("[green]Importing[/]", autoStart: true, maxValue: transcriptFiles.Count);
            display = display with { Footer = footer };

            await RunSessionLoop(display, transcriptFiles, httpClient, baseUrl, /*…all other state…*/);
        });
} else {
    await RunSessionLoop(display, transcriptFiles, httpClient, baseUrl, /*…*/);
}
```

Here `RunSessionLoop` is a local function that contains the existing per-session loop body with `display.Line` calls and, in TTY mode, updates to `display.Footer`.

Because `HandleHistory` has many local variables (counters, continuation map, exclusion flags), the cleanest refactor is to **keep everything inline** and wrap the whole `foreach` in the `Progress.StartAsync` branch. Concretely:

```csharp
var display = HistoryDisplay.Create();

async Task RunLoop() {
    foreach (var (sessionId, filePath, encodedCwd) in transcriptFiles) {
        // …existing body, using display.Line, display.SetFooterSession, display.AdvanceFooterLines…
    }
}

if (display.Tty) {
    await AnsiConsole.Progress()
        .AutoClear(false)
        .HideCompleted(false)
        .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
        .StartAsync(async ctx => {
            var footer = ctx.AddTask("[green]Importing[/]", maxValue: transcriptFiles.Count);
            display = display with { Footer = footer };

            await RunLoop();
            footer.Value = footer.MaxValue;
        });
} else {
    await RunLoop();
}
```

- [ ] **Step 2: Drive the footer from a per-session IProgress callback**

Inside the per-session processing body, before calling `SessionImporter.ImportSessionAsync`, compute the pre-known line count and set the footer session description:

```csharp
var sessionIdShort = sessionId.Length >= 8 ? sessionId[..8] : sessionId;
var totalLines     = WatchCommand.CountFileLines(filePath);
var linesDone      = 0;
string? currentSubagent = null;

display.SetFooterSession(sessionIdShort, totalLines);

var perSessionProgress = new Progress<ImportProgress>(ev => {
    switch (ev) {
        case BatchFlushed bf:
            linesDone += bf.LinesAdded;
            display.AdvanceFooterLines(linesDone, totalLines, sessionIdShort, currentSubagent);
            break;
        case SubagentStarted ss:
            currentSubagent = ss.AgentId.Length >= 8 ? ss.AgentId[..8] : ss.AgentId;
            display.AdvanceFooterLines(linesDone, totalLines, sessionIdShort, currentSubagent);
            break;
        case SubagentFinished sf:
            display.Line(
                $"  ↳ imported subagent {sf.AgentId} ({sf.LinesSent} lines)",
                $"  [dim]↳[/] imported subagent [cyan]{Markup.Escape(sf.AgentId)}[/] ({sf.LinesSent} lines)");
            currentSubagent = null;
            display.AdvanceFooterLines(linesDone, totalLines, sessionIdShort, null);
            break;
    }
});
```

Pass `perSessionProgress` as the last argument of both importer calls. Current call sites are around line 268 (for new sessions) and line 368 (partial/resume). Change them to:

```csharp
var importResult = await SessionImporter.ImportSessionAsync(
    httpClient, baseUrl, filePath, sessionId, meta, encodedCwd, perSessionProgress
);
```

and

```csharp
var linesSent = await SessionImporter.SendTranscriptBatches(
    httpClient, baseUrl, sessionId, filePath, agentId: null,
    startLine: resumeFromLine, progress: perSessionProgress
);
```

After a session completes, advance the footer's session counter:

```csharp
display.Footer?.Increment(1);
```

- [ ] **Step 3: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```
Expected: success.

- [ ] **Step 4: Run tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```
Expected: all pass.

- [ ] **Step 5: AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: zero lines.

- [ ] **Step 6: Smoke test — TTY mode**

Run against your own local server (or any reachable Capacitor server):

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 10
```
Expected: a pinned footer line at the bottom showing session count and per-session line progress; completion lines (`Loading …`, `Skipping …`, `  ↳ imported subagent …`) stream above the footer; when done, a plain `Done: …` line.

- [ ] **Step 7: Smoke test — piped mode**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 10 > /tmp/kapacitor-history.log
cat /tmp/kapacitor-history.log
```
Expected: no ANSI, no progress bar, one line per session in today's format plus per-subagent `  ↳ imported subagent …` lines.

- [ ] **Step 8: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "[DEV-1573] History: TTY-mode Spectre progress footer + streaming lines

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: History — background phase progress block with individual failure streaming

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

- [ ] **Step 1: Capture per-task failure reasons**

Currently the background tasks only `Interlocked.Increment` counters. Extend them to record failure reasons so the UI can stream each failure line. Introduce two thread-safe lists above the `foreach`:

```csharp
var titleFailures   = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();
var summaryFailures = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();
```

Inside the title-generation `Task.Run` at around line 322, where `TitleResult.Failed` is handled, also `titleFailures.Add((titleSessionId, "generation error"))` (the current code doesn't surface a reason; `"generation error"` is the best we have without further instrumentation). In the summary-generation `Task.Run` at around line 344, on failure path `summaryFailures.Add((titleSessionId, "generator exited non-zero"))`.

Concretely, in the title task:

```csharp
case TitleResult.Failed:
    Interlocked.Increment(ref titlesFailed);
    titleFailures.Add((titleSessionId, "generation error"));
    break;
```

In the summary task:

```csharp
if (wdResult == 0) {
    Interlocked.Increment(ref summariesGenerated);
} else {
    Interlocked.Increment(ref summariesFailed);
    summaryFailures.Add((titleSessionId, $"exit {wdResult}"));
}
```

And the outer catch in that Task.Run:

```csharp
catch (Exception ex) {
    Interlocked.Increment(ref summariesFailed);
    summaryFailures.Add((titleSessionId, ex.Message));
}
```

- [ ] **Step 2: Replace the flat "Waiting for N background tasks…" block with a TTY Progress**

Find the block starting `if (backgroundTasks.Count > 0)` (around line 378). Replace it with:

```csharp
if (backgroundTasks.Count > 0) {
    if (display.Tty) {
        AnsiConsole.Write(new Rule($"[dim]── Waiting for {backgroundTasks.Count} background task(s) ──[/]").LeftJustified());

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .StartAsync(async ctx => {
                var titleCount    = backgroundTasks.Count; // overcount-safe: Progress caps at MaxValue
                var titleTask     = ctx.AddTask("[cyan]Titles[/]", maxValue: titleCount);
                var summaryTask   = ctx.AddTask("[cyan]Summaries[/]", maxValue: titleCount);

                // Poll counters while tasks run; drain failure bags as they populate.
                var seenTitleFailures   = 0;
                var seenSummaryFailures = 0;

                while (backgroundTasks.Any(t => !t.IsCompleted)) {
                    titleTask.Value   = titlesGenerated   + titlesFailed + titlesSkipped;
                    summaryTask.Value = summariesGenerated + summariesFailed;

                    foreach (var (sid, reason) in titleFailures.Skip(seenTitleFailures).ToList()) {
                        AnsiConsole.MarkupLine($"  [red]✗[/] title failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                        seenTitleFailures++;
                    }
                    foreach (var (sid, reason) in summaryFailures.Skip(seenSummaryFailures).ToList()) {
                        AnsiConsole.MarkupLine($"  [red]✗[/] summary failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                        seenSummaryFailures++;
                    }

                    await Task.Delay(250);
                }

                try {
                    await Task.WhenAll(backgroundTasks);
                } catch {
                    // per-task try/catch handles individual failures
                }

                // Final drain after tasks completed
                foreach (var (sid, reason) in titleFailures.Skip(seenTitleFailures).ToList())
                    AnsiConsole.MarkupLine($"  [red]✗[/] title failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                foreach (var (sid, reason) in summaryFailures.Skip(seenSummaryFailures).ToList())
                    AnsiConsole.MarkupLine($"  [red]✗[/] summary failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");

                titleTask.Value   = titlesGenerated   + titlesFailed + titlesSkipped;
                summaryTask.Value = summariesGenerated + summariesFailed;
            });
    } else {
        display.Line($"Waiting for {backgroundTasks.Count} background task(s) (titles/summaries)...");
        try {
            await Task.WhenAll(backgroundTasks);
        } catch {
            // per-task try/catch handles individual failures
        }

        foreach (var (sid, reason) in titleFailures)
            display.Line($"  ✗ title failed for {sid}: {reason}");
        foreach (var (sid, reason) in summaryFailures)
            display.Line($"  ✗ summary failed for {sid}: {reason}");
    }

    var parts = new List<string>();

    if (titlesGenerated                > 0) parts.Add($"{titlesGenerated} title{(titlesGenerated        == 1 ? "" : "s")}");
    if (summariesGenerated             > 0) parts.Add($"{summariesGenerated} summar{(summariesGenerated == 1 ? "y" : "ies")}");
    if (titlesSkipped                  > 0) parts.Add($"{titlesSkipped} skipped");
    if (titlesFailed + summariesFailed > 0) parts.Add($"{titlesFailed + summariesFailed} failed");

    if (parts.Count > 0) display.Line($"  {string.Join(", ", parts)}");
}
```

- [ ] **Step 3: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```
Expected: success.

- [ ] **Step 4: Run tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```
Expected: all pass.

- [ ] **Step 5: AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: zero lines.

- [ ] **Step 6: Smoke test with `--generate-summaries`**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 10 --generate-summaries
```
Expected: after the main import finishes, a rule separator, then two progress bars (`Titles`, `Summaries`) that advance as background tasks complete. Any failures stream as red `✗` lines between the bars. Final summary counts unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "[DEV-1573] History: background-phase progress + streaming failure lines

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Final verification pass

**Files:** none (verification only).

- [ ] **Step 1: Full unit test suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```
Expected: all tests pass.

- [ ] **Step 2: Integration test suite**

```bash
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```
Expected: all tests pass.

- [ ] **Step 3: Release publish AOT gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: zero lines.

- [ ] **Step 4: End-to-end smoke (`setup`, interactive and `--no-prompt`)**

Interactive:
```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- setup
```
Confirm: rules, arrow-key selection, status spinner, final grid.

Non-interactive:
```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- setup --no-prompt \
  --server-url https://example.invalid --default-visibility private --plugin-scope skip
```
Confirm: rules and final grid render, reachability fails, exit code non-zero.

- [ ] **Step 5: End-to-end smoke (`history`, TTY and piped)**

TTY:
```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 10
```
Confirm: pinned footer, streaming `Loading … N lines [new]`, `↳ imported subagent …` lines, final summary.

Piped:
```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 10 | head -50
```
Confirm: no ANSI, same content as today plus new per-subagent lines.

With summaries:
```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 10 --generate-summaries
```
Confirm: second progress block appears after main import.

- [ ] **Step 6: Hook commands untouched — spot-check**

Confirm one hook still works (it does not import Spectre). Pick any cached transcript line and feed it via stdin:

```bash
echo '{"session_id":"00000000000000000000000000000001","hook_event_name":"session_start","cwd":"/tmp"}' \
  | dotnet run --project src/kapacitor/kapacitor.csproj -- session-start
```
Expected: exits cleanly (success or clean HTTP error — not a crash; no Spectre output).

- [ ] **Step 7: No plan-level commit — work is already committed across tasks**

---

## Self-review notes (author)

- **Spec coverage.** Every spec section maps to at least one task:
  - Dependencies (`Spectre.Console` only) → Task 1.
  - Setup wizard (rules/grid/status + prompts) → Tasks 5–6.
  - History display model (single footer, streaming lines, subagent indicator) → Tasks 7–8.
  - Importer `IProgress` hook + event records → Tasks 2–4.
  - Background phase progress + streaming failures → Task 9.
  - Non-TTY fallback → Task 7 establishes the gate; Tasks 8–9 route TTY vs piped through it.
  - AOT gate → Tasks 1, 5, 6, 7, 8, 9, 10.
- **Type consistency.** `ImportProgress`, `BatchFlushed(int LinesAdded)`, `SubagentStarted(string AgentId)`, `SubagentFinished(string AgentId, int LinesSent)` are used consistently across Tasks 2, 3, 4, 8. `HistoryDisplay` methods `Line`, `SetFooterSession`, `AdvanceFooterLines` are defined in Task 7 and used in Tasks 7, 8, 9.
- **Known gap.** Task 7 temporarily removes the current `"  N agents imported inline"` summary line; Task 8 restores per-subagent visibility via streaming `↳ imported subagent …` lines. If the plan stops mid-way (e.g. Task 7 merged but Task 8 not), the non-TTY pipe loses the inline agent count. This is flagged explicitly in Task 7 Step 3. Do not ship a release between Task 7 and Task 8.
