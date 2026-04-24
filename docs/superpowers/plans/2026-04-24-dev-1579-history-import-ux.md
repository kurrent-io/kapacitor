# DEV-1579 History Import UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework `kapacitor history` into a plan-then-execute pipeline with parallel per-chain imports. Classify every transcript up front, print a plan grid, run chains in parallel, then render a done grid — with no per-session "skipped" noise. Non-TTY output keeps the parallelism and prints plain text.

**Architecture:** Split `HistoryCommand.HandleHistory` into four helpers in the same file: `DiscoverTranscriptsAsync`, `ClassifyAsync`, `ImportChainsAsync`, `RenderFinalSummary` (plus the existing background phase wrapped in a Rule). Introduce a `SessionClassification` record and `ClassificationStatus` enum. Probes run concurrently via `SemaphoreSlim(8)`; imports dispatch chains across a `SemaphoreSlim(4)` worker pool with sessions serial within a chain. Shared counters use `Interlocked.Increment`. No changes to `SessionImporter`, `ImportProgress`, `TitleGenerator`, or `WhatsDoneCommand`.

**Tech Stack:** .NET 10, NativeAOT (`PublishAot=true`, `TrimMode=full`), TUnit test framework, WireMock.Net for HTTP mocking, Spectre.Console (already added by DEV-1573).

**Spec:** `docs/superpowers/specs/2026-04-24-dev-1579-history-import-ux-design.md`

---

## File map

- **Modify:** `src/kapacitor/Program.cs` — raise `--min-lines` default from `10` to `15`.
- **Modify:** `src/kapacitor/Commands/HistoryCommand.cs` — most of the work: rewrite `HandleHistory` as orchestration over new helpers; add classification/chain/display code.
- **Create:** `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs` — TDD tests for `ClassifyAsync` against a WireMock probe server.
- **Create:** `test/kapacitor.Tests.Unit/HistoryChainTests.cs` — TDD tests for `BuildImportChains` (pure function over classification records).
- **Create:** `test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs` — TDD tests for `ImportChainsAsync` parallelism + within-chain ordering.

---

## Task 1: Raise default `--min-lines` from 10 to 15

**Files:**
- Modify: `src/kapacitor/Program.cs:359`
- Modify: `src/kapacitor/Commands/HistoryCommand.cs:49`

- [ ] **Step 1: Update `Program.cs` default**

Edit `src/kapacitor/Program.cs` around line 359. Change:

```csharp
        var     minLines      = 10;
```

to:

```csharp
        var     minLines      = 15;
```

- [ ] **Step 2: Update `HandleHistory` parameter default**

Edit `src/kapacitor/Commands/HistoryCommand.cs:49`. Change the signature from:

```csharp
    public static async Task<int> HandleHistory(string baseUrl, string? filterCwd, string? filterSession = null, int minLines = 10, bool generateSummaries = false) {
```

to:

```csharp
    public static async Task<int> HandleHistory(string baseUrl, string? filterCwd, string? filterSession = null, int minLines = 15, bool generateSummaries = false) {
```

- [ ] **Step 3: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Program.cs src/kapacitor/Commands/HistoryCommand.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] Raise default --min-lines from 10 to 15

Short transcripts (<15 lines) are almost always trivial sessions —
single-prompt exchanges, aborted invocations — and add noise to history
imports without meaningful data. Users can still override with any value.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Define `SessionClassification` record + `ClassificationStatus` enum

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs` — add types at top of the class (below the existing `HistoryDisplay` struct).

Scope: pure data types, no behavior. Used by Tasks 4–9.

- [ ] **Step 1: Add the enum and record inside `HistoryCommand`**

In `src/kapacitor/Commands/HistoryCommand.cs`, immediately after the `HistoryDisplay` struct definition (after its closing `}` around line 47), insert:

```csharp
    internal enum ClassificationStatus {
        /// <summary>Session does not exist on the server — needs a full import.</summary>
        New,
        /// <summary>Session exists on the server with a partial line count — resume from ResumeFromLine.</summary>
        Partial,
        /// <summary>Session is fully loaded on the server — no work to do.</summary>
        AlreadyLoaded,
        /// <summary>Transcript line count is below the minLines threshold.</summary>
        TooShort,
        /// <summary>Session's repository is in the user's excluded list and the user declined to include it.</summary>
        Excluded,
        /// <summary>The last-line probe failed (HTTP error, network error).</summary>
        ProbeError,
        /// <summary>Kapacitor-spawned sub-session (title generation, what's-done summary) — never imported.</summary>
        InternalSubSession,
    }

    internal sealed record SessionClassification {
        public required string SessionId { get; init; }
        public required string FilePath { get; init; }
        public required string EncodedCwd { get; init; }
        public required SessionMetadata Meta { get; init; }
        public required ClassificationStatus Status { get; init; }

        /// <summary>Only populated when Status == Partial.</summary>
        public int ResumeFromLine { get; init; }

        /// <summary>Only populated when Status == ProbeError. Short human-readable reason.</summary>
        public string? ProbeErrorReason { get; init; }

        /// <summary>Populated when the session is a continuation in the continuation map.</summary>
        public string? PreviousSessionId { get; init; }

        /// <summary>
        /// Populated when Status == Excluded OR when the session would otherwise be New/Partial
        /// but its cwd maps to an excluded repo and the user has not yet been consulted.
        /// Format: "{Owner}/{RepoName}".
        /// </summary>
        public string? ExcludedRepoKey { get; init; }

        /// <summary>Total transcript line count (cached so we don't re-read the file downstream).</summary>
        public int TotalLines { get; init; }
    }
```

- [ ] **Step 2: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: build succeeds with no warnings.

- [ ] **Step 3: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] Add SessionClassification record and ClassificationStatus enum

Types that describe a transcript's fate before the import phase runs.
No behavior change yet; consumers arrive in later commits.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Extract `DiscoverTranscriptsAsync` helper

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

Scope: move today's inline discovery block (~lines 53–89 in `HandleHistory`) into a private method returning the same triple list. Pure refactor with no behavior change — `HandleHistory` still calls it exactly where discovery happens today.

- [ ] **Step 1: Add the helper method**

In `HistoryCommand`, add the following method after the existing `BuildContinuationMap` helper (near the bottom of the class, grouped with the other discovery helpers):

```csharp
    /// <summary>
    /// Enumerate ~/.claude/projects/*/*.jsonl transcripts, deduplicating directories
    /// by their resolved path (so symlinked project dirs don't scan the same files
    /// twice). Returns one entry per transcript with the normalized session id.
    /// </summary>
    internal static List<(string SessionId, string FilePath, string EncodedCwd)> DiscoverTranscripts(string projectsDir) {
        var results = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(projectsDir)) return results;

        foreach (var cwdDir in Directory.GetDirectories(projectsDir)) {
            var realPath = new DirectoryInfo(cwdDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
             ?? Path.GetFullPath(cwdDir);

            if (!seen.Add(realPath)) continue;

            var encodedCwd = Path.GetFileName(cwdDir);

            results.AddRange(
                from jsonlFile in Directory.GetFiles(cwdDir, "*.jsonl")
                let sessionId = NormalizeGuid(Path.GetFileNameWithoutExtension(jsonlFile))
                select (sessionId, jsonlFile, encodedCwd)
            );
        }

        return results;
    }
```

- [ ] **Step 2: Replace the inline block in `HandleHistory`**

In `HandleHistory`, locate the block that starts with `var projectsDir = ClaudePaths.Projects;` (around line 55) and ends after the `transcriptFiles.AddRange` loop (around line 83). Replace it with:

```csharp
        var projectsDir = ClaudePaths.Projects;

        if (!Directory.Exists(projectsDir)) {
            display.Line("No Claude Code projects directory found.");

            return 0;
        }

        var transcriptFiles = DiscoverTranscripts(projectsDir);
```

Keep the existing `if (transcriptFiles.Count == 0)` block and everything below it unchanged for now.

- [ ] **Step 3: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: build succeeds.

- [ ] **Step 4: Run existing tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass — including `HistoryCommandTests` (they exercise helpers that didn't change).

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] Extract DiscoverTranscripts helper

Pure refactor: move the ~/.claude/projects enumeration + dedup block
out of HandleHistory into a named helper. No behavior change.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: TDD `ClassifyAsync` — `New` and `AlreadyLoaded` cases

**Files:**
- Create: `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs`
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

Scope: introduce `ClassifyAsync` with WireMock-backed tests for the two simplest probe outcomes. The remaining outcomes (`Partial`, `TooShort`, `Excluded`, `ProbeError`, `InternalSubSession`) arrive in Tasks 5–6.

- [ ] **Step 1: Write the failing tests**

Create `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs`:

```csharp
using System.Net;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class HistoryClassifyTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kapacitor-classify-test").FullName;

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    static async Task<string> WriteTranscript(string dir, string sessionId, int lines) {
        var path = Path.Combine(dir, $"{sessionId}.jsonl");
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return path;
    }

    [Test]
    public async Task ClassifyAsync_maps_404_to_New() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var path = await WriteTranscript(_tempDir, "sessionNew", lines: 50);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionNew", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.New);
        await Assert.That(result[0].SessionId).IsEqualTo("sessionNew");
        await Assert.That(result[0].TotalLines).IsEqualTo(50);
    }

    [Test]
    public async Task ClassifyAsync_maps_204_to_AlreadyLoaded() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(204));

        var path = await WriteTranscript(_tempDir, "sessionDone", lines: 50);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionDone", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.AlreadyLoaded);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: FAIL. Compilation error — `HistoryCommand.ClassifyAsync` does not exist.

- [ ] **Step 3: Implement the minimal `ClassifyAsync`**

In `HistoryCommand`, add the following method (place it below `DiscoverTranscripts`):

```csharp
    /// <summary>
    /// Probe each transcript against the server's last-line API and classify
    /// what the import phase should do with it. Probes run concurrently via
    /// SemaphoreSlim(8) — idempotent GETs, safe to parallelize.
    /// </summary>
    internal static async Task<List<SessionClassification>> ClassifyAsync(
            HttpClient httpClient,
            string baseUrl,
            List<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
            int minLines,
            string[]? excludedRepos,
            CancellationToken ct
        ) {
        using var probeGate = new SemaphoreSlim(8);
        var tasks = new List<Task<SessionClassification>>(transcripts.Count);

        foreach (var (sessionId, filePath, encodedCwd) in transcripts) {
            tasks.Add(ClassifyOneAsync(httpClient, baseUrl, sessionId, filePath, encodedCwd, minLines, excludedRepos, probeGate, ct));
        }

        var results = await Task.WhenAll(tasks);
        return [.. results];
    }

    static async Task<SessionClassification> ClassifyOneAsync(
            HttpClient httpClient,
            string baseUrl,
            string sessionId,
            string filePath,
            string encodedCwd,
            int minLines,
            string[]? excludedRepos,
            SemaphoreSlim probeGate,
            CancellationToken ct
        ) {
        var meta = ExtractSessionMetadata(filePath);

        // Probe the server.
        await probeGate.WaitAsync(ct);
        ClassificationStatus status;
        int resumeFromLine = 0;
        string? probeErrorReason = null;
        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line");
            switch (resp.StatusCode) {
                case HttpStatusCode.NotFound:
                    status = ClassificationStatus.New;
                    break;
                case HttpStatusCode.NoContent:
                    status = ClassificationStatus.AlreadyLoaded;
                    break;
                default:
                    if (resp.IsSuccessStatusCode) {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.Num("last_line_number") is { } lastLine) {
                            resumeFromLine = (int)lastLine + 1;
                            status = ClassificationStatus.Partial;
                        } else {
                            status = ClassificationStatus.AlreadyLoaded;
                        }
                    } else {
                        status = ClassificationStatus.ProbeError;
                        probeErrorReason = $"HTTP {(int)resp.StatusCode}";
                    }
                    break;
            }
        } catch (HttpRequestException ex) {
            status = ClassificationStatus.ProbeError;
            probeErrorReason = ex.Message;
        } finally {
            probeGate.Release();
        }

        var totalLines = WatchCommand.CountFileLines(filePath);

        return new SessionClassification {
            SessionId = sessionId,
            FilePath = filePath,
            EncodedCwd = encodedCwd,
            Meta = meta,
            Status = status,
            ResumeFromLine = resumeFromLine,
            ProbeErrorReason = probeErrorReason,
            TotalLines = totalLines,
        };
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: PASS.

- [ ] **Step 5: Run full unit suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryClassifyTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] Introduce ClassifyAsync with New/AlreadyLoaded cases

Parallel probe of the server's last-line API. Handles 404 → New and
204 → AlreadyLoaded. Remaining status branches arrive in follow-ups.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: TDD `ClassifyAsync` — `Partial`, `TooShort`, `ProbeError`, `InternalSubSession`

**Files:**
- Modify: `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs`
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

- [ ] **Step 1: Add failing tests**

Append to `HistoryClassifyTests.cs`:

```csharp
    [Test]
    public async Task ClassifyAsync_maps_200_with_last_line_to_Partial() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"last_line_number": 42}"""));

        var path = await WriteTranscript(_tempDir, "sessionPartial", lines: 100);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionPartial", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(43);
    }

    [Test]
    public async Task ClassifyAsync_maps_short_transcript_to_TooShort_without_probing() {
        // No WireMock stub — if ClassifyAsync probes, this will fail on a bare 404 default.
        // But since we classify TooShort before the probe, there's no request at all.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500)); // sabotage: would cause ProbeError if used

        var path = await WriteTranscript(_tempDir, "tiny", lines: 5);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("tiny", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.TooShort);
        await Assert.That(result[0].TotalLines).IsEqualTo(5);
    }

    [Test]
    public async Task ClassifyAsync_maps_server_error_to_ProbeError() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var path = await WriteTranscript(_tempDir, "sessionErr", lines: 50);
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionErr", path, "-tmp-proj")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.ProbeError);
        await Assert.That(result[0].ProbeErrorReason).IsEqualTo("HTTP 500");
    }

    [Test]
    public async Task ClassifyAsync_identifies_kapacitor_subsession() {
        // Kapacitor sub-sessions live under ~/.claude/projects/<cwd>/subagents/
        // TitleGenerator.IsKapacitorSubSession returns true when the path contains "/subagents/"
        // and the session id matches the title/whats-done agent id pattern.
        var subagentDir = Directory.CreateTempSubdirectory("kapacitor-sub").FullName;
        var subagentSubDir = Path.Combine(subagentDir, "subagents");
        Directory.CreateDirectory(subagentSubDir);
        var path = Path.Combine(subagentSubDir, "agent-title-abc123.jsonl");
        await File.WriteAllLinesAsync(path, ["""{"type":"user","timestamp":"2026-03-15T10:00:00Z"}"""]);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("title-abc123", path, "-tmp-sub")
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15, excludedRepos: null, CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.InternalSubSession);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: the two new tests (`Partial`, `TooShort`, `ProbeError`, `InternalSubSession`) fail.

- [ ] **Step 3: Extend `ClassifyOneAsync` to handle the new branches**

Replace `ClassifyOneAsync` in `HistoryCommand.cs` with:

```csharp
    static async Task<SessionClassification> ClassifyOneAsync(
            HttpClient httpClient,
            string baseUrl,
            string sessionId,
            string filePath,
            string encodedCwd,
            int minLines,
            string[]? excludedRepos,
            SemaphoreSlim probeGate,
            CancellationToken ct
        ) {
        var meta = ExtractSessionMetadata(filePath);

        // Short-circuit: kapacitor's own sub-sessions (title / what's-done) never get imported.
        if (TitleGenerator.IsKapacitorSubSession(filePath)) {
            return new SessionClassification {
                SessionId = sessionId,
                FilePath = filePath,
                EncodedCwd = encodedCwd,
                Meta = meta,
                Status = ClassificationStatus.InternalSubSession,
            };
        }

        // Short-circuit: count lines first; skip the probe entirely if too short.
        var totalLines = WatchCommand.CountFileLines(filePath);
        if (minLines > 0 && totalLines < minLines) {
            return new SessionClassification {
                SessionId = sessionId,
                FilePath = filePath,
                EncodedCwd = encodedCwd,
                Meta = meta,
                Status = ClassificationStatus.TooShort,
                TotalLines = totalLines,
            };
        }

        // Probe the server.
        ClassificationStatus status;
        int resumeFromLine = 0;
        string? probeErrorReason = null;

        await probeGate.WaitAsync(ct);
        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line");
            switch (resp.StatusCode) {
                case HttpStatusCode.NotFound:
                    status = ClassificationStatus.New;
                    break;
                case HttpStatusCode.NoContent:
                    status = ClassificationStatus.AlreadyLoaded;
                    break;
                default:
                    if (resp.IsSuccessStatusCode) {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.Num("last_line_number") is { } lastLine) {
                            resumeFromLine = (int)lastLine + 1;
                            status = ClassificationStatus.Partial;
                        } else {
                            status = ClassificationStatus.AlreadyLoaded;
                        }
                    } else {
                        status = ClassificationStatus.ProbeError;
                        probeErrorReason = $"HTTP {(int)resp.StatusCode}";
                    }
                    break;
            }
        } catch (HttpRequestException ex) {
            status = ClassificationStatus.ProbeError;
            probeErrorReason = ex.Message;
        } finally {
            probeGate.Release();
        }

        return new SessionClassification {
            SessionId = sessionId,
            FilePath = filePath,
            EncodedCwd = encodedCwd,
            Meta = meta,
            Status = status,
            ResumeFromLine = resumeFromLine,
            ProbeErrorReason = probeErrorReason,
            TotalLines = totalLines,
        };
    }
```

Add the missing `using System.Net;` at the top of the file if it's not already there.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: PASS, all 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryClassifyTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] ClassifyAsync: Partial / TooShort / ProbeError / InternalSubSession

Completes the probe-to-status mapping per the spec. TooShort and
InternalSubSession short-circuit before the HTTP probe.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: TDD excluded-repo classification

**Files:**
- Modify: `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs`
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

Scope: if a session's cwd maps to an excluded repo AND the session would otherwise be `New` or `Partial`, populate `ExcludedRepoKey` on the classification. Actually resolving the user's choice ("include or skip?") happens in Task 11; here we just flag the key.

- [ ] **Step 1: Write the failing test**

Append to `HistoryClassifyTests.cs`:

```csharp
    [Test]
    public async Task ClassifyAsync_tags_ExcludedRepoKey_for_new_sessions_in_excluded_repos() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        // Make a transcript whose cwd is a real git repo cloned from an "excluded" remote.
        // We use a real directory with a fake git config to make DetectRepositoryAsync return a payload.
        var repoDir = Directory.CreateTempSubdirectory("kapacitor-excl").FullName;
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repoDir, ".git", "config"), """
            [remote "origin"]
                url = https://github.com/acme/secret.git
            [user]
                name = Test
                email = test@example.com
            """);
        await File.WriteAllTextAsync(Path.Combine(repoDir, ".git", "HEAD"), "ref: refs/heads/main\n");

        var transcriptPath = Path.Combine(_tempDir, "sessionX.jsonl");
        await File.WriteAllLinesAsync(transcriptPath, Enumerable.Range(0, 50).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"{{{repoDir.Replace("\\", "\\\\")}}}","message":{"content":"x"}}"""
        ));

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionX", transcriptPath, repoDir.Replace('/', '-'))
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ClassifyAsync(
            client, _server.Url!, transcripts, minLines: 15,
            excludedRepos: ["acme/secret"], CancellationToken.None);

        await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.New);
        await Assert.That(result[0].ExcludedRepoKey).IsEqualTo("acme/secret");
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: the new test fails (`ExcludedRepoKey` is null).

- [ ] **Step 3: Extend `ClassifyOneAsync` with the excluded-repo check**

At the bottom of `ClassifyOneAsync` (just before the final `return`), add:

```csharp
        // Flag excluded repos for New/Partial sessions. Resolution (include or skip?)
        // happens later in HandleHistory, where we can batch prompts by repo key.
        string? excludedRepoKey = null;
        if ((status == ClassificationStatus.New || status == ClassificationStatus.Partial)
            && excludedRepos is { Length: > 0 }) {
            var cwd = meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(encodedCwd);
            if (cwd is not null) {
                var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);
                if (repo?.Owner is not null && repo.RepoName is not null) {
                    var key = $"{repo.Owner}/{repo.RepoName}";
                    if (excludedRepos.Contains(key, StringComparer.OrdinalIgnoreCase)) {
                        excludedRepoKey = key;
                    }
                }
            }
        }
```

And update the final return to include the key:

```csharp
        return new SessionClassification {
            SessionId = sessionId,
            FilePath = filePath,
            EncodedCwd = encodedCwd,
            Meta = meta,
            Status = status,
            ResumeFromLine = resumeFromLine,
            ProbeErrorReason = probeErrorReason,
            TotalLines = totalLines,
            ExcludedRepoKey = excludedRepoKey,
        };
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: PASS, all 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryClassifyTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] ClassifyAsync: tag ExcludedRepoKey on New/Partial in excluded repos

Consent prompts are resolved later in HandleHistory so we can batch one
prompt per unique repo instead of per session.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: TDD `BuildImportChains` (pure grouping function)

**Files:**
- Create: `test/kapacitor.Tests.Unit/HistoryChainTests.cs`
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

Scope: take the subset of classifications that actually need import (`New` + `Partial`), group into ordered chains by slug + timestamp, and return a list of chains. Singletons (sessions without a slug or with a unique slug) are chains of length 1.

- [ ] **Step 1: Write the failing tests**

Create `test/kapacitor.Tests.Unit/HistoryChainTests.cs`:

```csharp
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class HistoryChainTests {
    static HistoryCommand.SessionClassification Classify(
        string id,
        HistoryCommand.ClassificationStatus status,
        string? slug = null,
        DateTimeOffset? ts = null
    ) => new() {
        SessionId = id,
        FilePath = $"/tmp/{id}.jsonl",
        EncodedCwd = "-tmp",
        Meta = new SessionMetadata { Slug = slug, FirstTimestamp = ts ?? DateTimeOffset.UnixEpoch },
        Status = status,
    };

    [Test]
    public async Task BuildImportChains_includes_only_New_and_Partial() {
        var classifications = new List<HistoryCommand.SessionClassification> {
            Classify("a", HistoryCommand.ClassificationStatus.New),
            Classify("b", HistoryCommand.ClassificationStatus.AlreadyLoaded),
            Classify("c", HistoryCommand.ClassificationStatus.Partial),
            Classify("d", HistoryCommand.ClassificationStatus.TooShort),
            Classify("e", HistoryCommand.ClassificationStatus.ProbeError),
            Classify("f", HistoryCommand.ClassificationStatus.Excluded),
            Classify("g", HistoryCommand.ClassificationStatus.InternalSubSession),
        };

        var chains = HistoryCommand.BuildImportChains(classifications);

        var ids = chains.SelectMany(c => c).Select(c => c.SessionId).OrderBy(s => s).ToList();
        await Assert.That(ids).IsEquivalentTo(new[] { "a", "c" });
    }

    [Test]
    public async Task BuildImportChains_groups_by_slug_and_orders_by_timestamp() {
        var classifications = new List<HistoryCommand.SessionClassification> {
            Classify("a2", HistoryCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T10:00:00Z")),
            Classify("a1", HistoryCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T09:00:00Z")),
            Classify("a3", HistoryCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T11:00:00Z")),
        };

        var chains = HistoryCommand.BuildImportChains(classifications);

        await Assert.That(chains.Count).IsEqualTo(1);
        var ids = chains[0].Select(c => c.SessionId).ToList();
        await Assert.That(ids).IsEquivalentTo(new[] { "a1", "a2", "a3" });
    }

    [Test]
    public async Task BuildImportChains_sessions_without_slug_are_singleton_chains() {
        var classifications = new List<HistoryCommand.SessionClassification> {
            Classify("solo1", HistoryCommand.ClassificationStatus.New),
            Classify("solo2", HistoryCommand.ClassificationStatus.New),
        };

        var chains = HistoryCommand.BuildImportChains(classifications);

        await Assert.That(chains.Count).IsEqualTo(2);
        await Assert.That(chains.All(c => c.Count == 1)).IsTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryChainTests/*"
```

Expected: compilation error — `BuildImportChains` does not exist.

- [ ] **Step 3: Implement `BuildImportChains`**

In `HistoryCommand.cs`, add:

```csharp
    /// <summary>
    /// Group the import-bound subset (New + Partial) into ordered chains by slug.
    /// A chain is a list of classifications sharing the same slug, ordered by
    /// FirstTimestamp ascending. Sessions without a slug (or with a unique slug)
    /// become chains of length 1. Chain order (across chains) is stable by slug
    /// string so re-runs import in the same order.
    /// </summary>
    internal static List<List<SessionClassification>> BuildImportChains(List<SessionClassification> classifications) {
        var importable = classifications
            .Where(c => c.Status == ClassificationStatus.New || c.Status == ClassificationStatus.Partial)
            .ToList();

        var chains = new List<List<SessionClassification>>();

        var withSlug = importable
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in withSlug) {
            var ordered = group
                .OrderBy(c => c.Meta.FirstTimestamp ?? DateTimeOffset.MinValue)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList();
            chains.Add(ordered);
        }

        foreach (var solo in importable.Where(c => c.Meta.Slug is null).OrderBy(c => c.SessionId, StringComparer.Ordinal)) {
            chains.Add([solo]);
        }

        return chains;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryChainTests/*"
```

Expected: PASS, all 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryChainTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] Introduce BuildImportChains for parallel dispatch

Pure function that groups New/Partial classifications into ordered
chains by slug. Singletons become length-1 chains. Input to the
parallel import phase in a follow-up.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: TDD `ImportChainsAsync` — parallelism + within-chain ordering

**Files:**
- Create: `test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs`
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

Scope: dispatch chains across 4 workers via `SemaphoreSlim`; within a chain, process sessions serially; post session-start → import → session-end per session. Capture completion callbacks for the display layer.

Signature of the new helper (display-agnostic — the caller bridges to Spectre):

```csharp
internal sealed record ImportChainsResult(int Loaded, int Resumed, int Errored);

internal sealed record ChainWorkerEvents {
    public required Action<string> OnLineCompleted { get; init; }     // "Loading {sid}... {N} lines [new]" or similar
    public required Action<string, string, int> OnSubagentFinished { get; init; } // (sid, aid, lines)
    public required Action<string, string> OnSessionErrored { get; init; }        // (sid, reason)
    public required Action<(string SessionId, string FilePath, string PreviousSessionId)> OnTitleTaskReady { get; init; }
    public required Action<(string SessionId, bool GenerateWhatsDone)> OnSessionEnded { get; init; }
}
```

For testability, the chain workers call these callbacks; production code wires them to display + background-task enqueue. The callbacks are invoked from arbitrary worker threads, so production code must be thread-safe (Spectre is; counters use `Interlocked`).

- [ ] **Step 1: Write the failing tests**

Create `test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs`:

```csharp
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class HistoryImportChainsTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kapacitor-import-chains-test").FullName;

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    void StubAllHookEndpoints() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    HistoryCommand.SessionClassification MakeNew(string id, int lines) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new HistoryCommand.SessionClassification {
            SessionId = id,
            FilePath = path,
            EncodedCwd = "-tmp-proj",
            Meta = new SessionMetadata { Cwd = "/tmp/proj" },
            Status = HistoryCommand.ClassificationStatus.New,
            TotalLines = lines,
        };
    }

    [Test]
    public async Task ImportChainsAsync_counts_loaded_sessions() {
        StubAllHookEndpoints();

        var chains = new List<List<HistoryCommand.SessionClassification>> {
            [MakeNew("s1", 50)], [MakeNew("s2", 50)], [MakeNew("s3", 50)],
        };

        var completedLines = new System.Collections.Concurrent.ConcurrentBag<string>();
        var events = new HistoryCommand.ChainWorkerEvents {
            OnLineCompleted = s => completedLines.Add(s),
            OnSubagentFinished = (_, _, _) => { },
            OnSessionErrored = (_, _) => { },
            OnTitleTaskReady = _ => { },
            OnSessionEnded = _ => { },
        };

        using var client = new HttpClient();
        var result = await HistoryCommand.ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        await Assert.That(result.Loaded).IsEqualTo(3);
        await Assert.That(result.Errored).IsEqualTo(0);
        await Assert.That(completedLines.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ImportChainsAsync_processes_within_chain_in_order() {
        StubAllHookEndpoints();

        // Single chain of 3 sessions. They must complete in order s1 → s2 → s3.
        var chain = new List<HistoryCommand.SessionClassification> {
            MakeNew("s1", 50), MakeNew("s2", 50), MakeNew("s3", 50),
        };
        var chains = new List<List<HistoryCommand.SessionClassification>> { chain };

        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var events = new HistoryCommand.ChainWorkerEvents {
            OnLineCompleted = s => order.Enqueue(s),
            OnSubagentFinished = (_, _, _) => { },
            OnSessionErrored = (_, _) => { },
            OnTitleTaskReady = _ => { },
            OnSessionEnded = _ => { },
        };

        using var client = new HttpClient();
        await HistoryCommand.ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        var lines = order.ToArray();
        await Assert.That(lines[0]).Contains("s1");
        await Assert.That(lines[1]).Contains("s2");
        await Assert.That(lines[2]).Contains("s3");
    }

    [Test]
    public async Task ImportChainsAsync_dispatches_independent_chains_in_parallel() {
        // Slow the transcript endpoint so parallel chains take less wall time than serial would.
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromMilliseconds(150)));
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // 4 chains, each 1 session of 50 lines (one 150ms transcript POST).
        var chains = Enumerable.Range(0, 4)
            .Select(i => (List<HistoryCommand.SessionClassification>)[MakeNew($"p{i}", 50)])
            .ToList();

        var events = new HistoryCommand.ChainWorkerEvents {
            OnLineCompleted = _ => { },
            OnSubagentFinished = (_, _, _) => { },
            OnSessionErrored = (_, _) => { },
            OnTitleTaskReady = _ => { },
            OnSessionEnded = _ => { },
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client = new HttpClient();
        await HistoryCommand.ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);
        sw.Stop();

        // Serial would be ~600ms (4 × 150). Parallel across 4 workers should be well under 400ms.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(400);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryImportChainsTests/*"
```

Expected: compilation error — `ImportChainsAsync` and `ChainWorkerEvents` don't exist.

- [ ] **Step 3: Implement `ImportChainsAsync`**

In `HistoryCommand.cs`, add:

```csharp
    internal sealed record ImportChainsResult(int Loaded, int Resumed, int Errored);

    internal sealed record ChainWorkerEvents {
        public required Action<string> OnLineCompleted { get; init; }
        public required Action<string, string, int> OnSubagentFinished { get; init; }
        public required Action<string, string> OnSessionErrored { get; init; }
        public required Action<(string SessionId, string FilePath, string? PreviousSessionId)> OnTitleTaskReady { get; init; }
        public required Action<(string SessionId, bool GenerateWhatsDone)> OnSessionEnded { get; init; }
    }

    /// <summary>
    /// Dispatch chains across 4 parallel workers; sessions within a chain run
    /// serially. Thread-safe: counters use Interlocked, callbacks must be
    /// thread-safe (production wiring uses AnsiConsole + ConcurrentBag).
    /// </summary>
    internal static async Task<ImportChainsResult> ImportChainsAsync(
            HttpClient httpClient,
            string baseUrl,
            List<List<SessionClassification>> chains,
            ChainWorkerEvents events,
            CancellationToken ct
        ) {
        using var chainGate = new SemaphoreSlim(4);
        var loaded = 0;
        var resumed = 0;
        var errored = 0;

        var tasks = chains.Select(chain => Task.Run(async () => {
            await chainGate.WaitAsync(ct);
            try {
                foreach (var session in chain) {
                    var r = await ImportSingleSessionAsync(httpClient, baseUrl, session, events, ct);
                    switch (r) {
                        case SessionImportOutcome.Loaded:  Interlocked.Increment(ref loaded); break;
                        case SessionImportOutcome.Resumed: Interlocked.Increment(ref resumed); break;
                        case SessionImportOutcome.Errored: Interlocked.Increment(ref errored); break;
                    }
                }
            } finally {
                chainGate.Release();
            }
        }, ct));

        await Task.WhenAll(tasks);
        return new ImportChainsResult(loaded, resumed, errored);
    }

    enum SessionImportOutcome { Loaded, Resumed, Errored }

    static async Task<SessionImportOutcome> ImportSingleSessionAsync(
            HttpClient httpClient,
            string baseUrl,
            SessionClassification session,
            ChainWorkerEvents events,
            CancellationToken ct
        ) {
        IProgress<ImportProgress> perSessionProgress = new CallbackProgress(ev => {
            if (ev is SubagentFinished sf) {
                events.OnSubagentFinished(session.SessionId, sf.AgentId, sf.LinesSent);
            }
        });

        if (session.Status == ClassificationStatus.Partial) {
            try {
                var linesSent = await SessionImporter.SendTranscriptBatches(
                    httpClient, baseUrl, session.SessionId, session.FilePath,
                    agentId: null, startLine: session.ResumeFromLine, progress: perSessionProgress);
                events.OnLineCompleted($"Loading {session.SessionId}... {linesSent} lines [resuming from line {session.ResumeFromLine}]");
                return SessionImportOutcome.Resumed;
            } catch (HttpRequestException ex) {
                events.OnSessionErrored(session.SessionId, $"server unreachable: {ex.Message}");
                return SessionImportOutcome.Errored;
            }
        }

        // status == New: session-start → import → session-end → enqueue background tasks
        var meta = session.Meta;
        var cwd = meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(session.EncodedCwd);

        var startHook = new System.Text.Json.Nodes.JsonObject {
            ["session_id"] = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"] = cwd ?? "",
            ["source"] = "Startup",
            ["hook_event_name"] = "session_start",
            ["model"] = meta.Model,
        };
        if (meta.FirstTimestamp is not null) startHook["started_at"] = meta.FirstTimestamp.Value.ToString("O");
        if (session.PreviousSessionId is not null) startHook["previous_session_id"] = session.PreviousSessionId;
        if (meta.Slug is not null) startHook["slug"] = meta.Slug;

        if (cwd is not null) {
            var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);
            if (repo is not null) {
                var repoNode = new System.Text.Json.Nodes.JsonObject();
                if (repo.UserName is not null)  repoNode["user_name"]  = repo.UserName;
                if (repo.UserEmail is not null) repoNode["user_email"] = repo.UserEmail;
                if (repo.RemoteUrl is not null) repoNode["remote_url"] = repo.RemoteUrl;
                if (repo.Owner is not null)     repoNode["owner"]      = repo.Owner;
                if (repo.RepoName is not null)  repoNode["repo_name"]  = repo.RepoName;
                if (repo.Branch is not null)    repoNode["branch"]     = repo.Branch;
                startHook["repository"] = repoNode;
            }
        }

        try {
            using var startContent = new StringContent(startHook.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var startResp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-start", startContent);
            if (!startResp.IsSuccessStatusCode) {
                events.OnSessionErrored(session.SessionId, $"session-start failed: HTTP {(int)startResp.StatusCode}");
                return SessionImportOutcome.Errored;
            }
        } catch (HttpRequestException ex) {
            events.OnSessionErrored(session.SessionId, $"server unreachable: {ex.Message}");
            return SessionImportOutcome.Errored;
        }

        var importResult = await SessionImporter.ImportSessionAsync(
            httpClient, baseUrl, session.FilePath, session.SessionId, meta, session.EncodedCwd, perSessionProgress);

        events.OnLineCompleted($"Loading {session.SessionId}... {importResult.LinesSent} lines [new]");

        var lastTs = ExtractLastTimestamp(session.FilePath);
        var endHook = new System.Text.Json.Nodes.JsonObject {
            ["session_id"] = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"] = cwd ?? "",
            ["reason"] = "Other",
            ["hook_event_name"] = "session_end",
        };
        if (lastTs is not null) endHook["ended_at"] = lastTs.Value.ToString("O");

        var generateWhatsDone = false;
        try {
            using var endContent = new StringContent(endHook.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var endResp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-end", endContent);
            if (endResp.IsSuccessStatusCode) {
                try {
                    var body = await endResp.Content.ReadAsStringAsync(ct);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                    generateWhatsDone = node?["generate_whats_done"]?.GetValue<bool>() == true;
                } catch { /* best effort */ }
            }
        } catch { /* best effort */ }

        events.OnTitleTaskReady((session.SessionId, session.FilePath, session.PreviousSessionId));
        events.OnSessionEnded((session.SessionId, generateWhatsDone));

        return SessionImportOutcome.Loaded;
    }

    sealed class CallbackProgress(Action<ImportProgress> onReport) : IProgress<ImportProgress> {
        public void Report(ImportProgress value) => onReport(value);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryImportChainsTests/*"
```

Expected: PASS, all 3 tests.

- [ ] **Step 5: Run full unit suite**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] Introduce ImportChainsAsync for parallel chain dispatch

Dispatches chains across 4 workers; sessions within a chain run
serially so continuation links hold. Display-agnostic — callers bridge
callbacks to Spectre or plain Console. Counters use Interlocked.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Extend `HistoryDisplay` with phase rules and grids

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

Scope: add three methods to `HistoryDisplay`: `BeginPhase(title)`, `WritePlanGrid(counts)`, `WriteDoneGrid(counts)`. TTY path uses Spectre `Rule` + `Grid`; non-TTY path uses plain text with aligned columns.

- [ ] **Step 1: Add a simple counts record**

In `HistoryCommand.cs`, near the other internal types, add:

```csharp
    internal sealed record ClassificationCounts(
        int New, int Partial, int AlreadyLoaded, int TooShort, int Excluded, int ProbeError);

    internal sealed record FinalCounts(
        int Loaded, int Resumed, int AlreadyLoaded, int TooShort, int Excluded,
        int ProbeError, int Errored,
        int TitlesGenerated, int TitlesSkipped, int TitlesFailed,
        int SummariesGenerated, int SummariesFailed,
        bool RanBackground, bool RequestedSummaries);
```

- [ ] **Step 2: Extend `HistoryDisplay`**

In the existing `HistoryDisplay` struct, add (below the existing `Line` method):

```csharp
        public void BeginPhase(string title) {
            if (Tty) {
                AnsiConsole.Write(new Rule($"[yellow]{Markup.Escape(title)}[/]").LeftJustified());
            } else {
                Console.WriteLine();
                Console.WriteLine($"== {title} ==");
            }
        }

        public void WritePlanGrid(ClassificationCounts c) {
            if (Tty) {
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]New[/]",             c.New.ToString());
                grid.AddRow("[bold]Resumable[/]",       c.Partial.ToString());
                grid.AddRow("[bold]Already loaded[/]",  c.AlreadyLoaded.ToString());
                grid.AddRow("[bold]Too short[/]",       c.TooShort.ToString());
                grid.AddRow("[bold]Excluded[/]",        c.Excluded.ToString());
                if (c.ProbeError > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{c.ProbeError}[/]");
                AnsiConsole.Write(grid);
            } else {
                Console.WriteLine($"  New               {c.New}");
                Console.WriteLine($"  Resumable         {c.Partial}");
                Console.WriteLine($"  Already loaded    {c.AlreadyLoaded}");
                Console.WriteLine($"  Too short         {c.TooShort}");
                Console.WriteLine($"  Excluded          {c.Excluded}");
                if (c.ProbeError > 0) Console.WriteLine($"  Probe errors      {c.ProbeError}");
            }
        }

        public void WriteDoneGrid(FinalCounts f) {
            if (Tty) {
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]Loaded[/]",         f.Loaded.ToString());
                grid.AddRow("[bold]Resumed[/]",        f.Resumed.ToString());
                grid.AddRow("[bold]Already loaded[/]", f.AlreadyLoaded.ToString());
                if (f.TooShort > 0)    grid.AddRow("[bold]Too short[/]",    f.TooShort.ToString());
                if (f.Excluded > 0)    grid.AddRow("[bold]Excluded[/]",     f.Excluded.ToString());
                if (f.ProbeError > 0)  grid.AddRow("[bold]Probe errors[/]", $"[red]{f.ProbeError}[/]");
                if (f.Errored > 0)     grid.AddRow("[bold]Errored[/]",      $"[red]{f.Errored}[/]");
                if (f.RanBackground) {
                    grid.AddRow("[bold]Titles[/]", $"{f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");
                    if (f.RequestedSummaries)
                        grid.AddRow("[bold]Summaries[/]", $"{f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }
                AnsiConsole.Write(new Rule("[green]Done[/]").LeftJustified());
                AnsiConsole.Write(grid);
            } else {
                Console.WriteLine();
                Console.WriteLine("== Done ==");
                Console.WriteLine($"  Loaded              {f.Loaded}");
                Console.WriteLine($"  Resumed             {f.Resumed}");
                Console.WriteLine($"  Already loaded      {f.AlreadyLoaded}");
                if (f.TooShort > 0)   Console.WriteLine($"  Too short           {f.TooShort}");
                if (f.Excluded > 0)   Console.WriteLine($"  Excluded            {f.Excluded}");
                if (f.ProbeError > 0) Console.WriteLine($"  Probe errors        {f.ProbeError}");
                if (f.Errored > 0)    Console.WriteLine($"  Errored             {f.Errored}");
                if (f.RanBackground) {
                    Console.WriteLine($"  Titles              {f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");
                    if (f.RequestedSummaries)
                        Console.WriteLine($"  Summaries           {f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }
            }
        }
```

- [ ] **Step 3: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] HistoryDisplay: BeginPhase + Plan/Done grids

TTY path renders Spectre Rule + Grid; non-TTY path prints aligned
plain text. Not wired into HandleHistory yet — that arrives next.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Rewrite `HandleHistory` to drive the pipeline

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs`

Scope: replace the body of `HandleHistory` with the orchestration: discover → classify → resolve excluded-repo prompts → plan grid → build chains → import → background phase → done grid. This is the cutover commit; after it the old serial loop and `HistorySessionStatus` handling inside `HandleHistory` are gone.

- [ ] **Step 1: Replace `HandleHistory` body**

Replace the entire body of `HandleHistory` (lines 49–585 in the current file) with:

```csharp
    public static async Task<int> HandleHistory(string baseUrl, string? filterCwd, string? filterSession = null, int minLines = 15, bool generateSummaries = false) {
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var display = HistoryDisplay.Create();

        // --- Discover ---
        display.BeginPhase("Discovering");
        var projectsDir = ClaudePaths.Projects;
        if (!Directory.Exists(projectsDir)) {
            display.Line("No Claude Code projects directory found.");
            return 0;
        }

        var transcriptFiles = DiscoverTranscripts(projectsDir);
        if (transcriptFiles.Count == 0) {
            display.Line("No transcript files found.");
            return 0;
        }

        if (filterSession is not null) {
            var normalized = NormalizeGuid(filterSession);
            transcriptFiles = [.. transcriptFiles.Where(t => t.SessionId == normalized)];
            if (transcriptFiles.Count == 0) {
                await Console.Error.WriteLineAsync($"Session not found: {normalized}");
                return 1;
            }
        }

        if (filterCwd is not null) {
            var normalizedFilter = filterCwd.TrimEnd('/');
            transcriptFiles = [.. transcriptFiles.Where(t => {
                var cwd = ExtractCwdFromTranscript(t.FilePath);
                return cwd?.TrimEnd('/').Equals(normalizedFilter, StringComparison.Ordinal) == true;
            })];
        }

        var projectCount = transcriptFiles.Select(t => t.EncodedCwd).Distinct().Count();
        display.Line($"Found {transcriptFiles.Count} session{(transcriptFiles.Count == 1 ? "" : "s")} in {projectCount} project{(projectCount == 1 ? "" : "s")}");

        // --- Classify (parallel probes) ---
        var excludedRepos = (await AppConfig.Load())?.ExcludedRepos;
        List<SessionClassification> classifications;
        if (display.Tty) {
            var tmp = new List<SessionClassification>();
            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                    var bar = ctx.AddTask("[yellow]Probing[/]", maxValue: transcriptFiles.Count);
                    // We call ClassifyAsync and tick the bar as tasks complete. For simplicity
                    // we tick once per input right after the probe gate releases — approximate
                    // but gives visible motion.
                    var results = await ClassifyWithProgressAsync(httpClient, baseUrl, transcriptFiles, minLines, excludedRepos, bar, CancellationToken.None);
                    tmp.AddRange(results);
                });
            classifications = tmp;
        } else {
            display.Line($"Probing {transcriptFiles.Count} sessions...");
            classifications = await ClassifyAsync(httpClient, baseUrl, transcriptFiles, minLines, excludedRepos, CancellationToken.None);
        }

        // --- Resolve excluded-repo prompts (TTY only; non-TTY auto-skips) ---
        var excludedByKey = classifications
            .Where(c => c.ExcludedRepoKey is not null)
            .GroupBy(c => c.ExcludedRepoKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (excludedByKey.Count > 0) {
            var includedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Console.IsInputRedirected) {
                foreach (var (key, sessions) in excludedByKey) {
                    Console.Write($"Repository {key} is excluded. Include {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} from it? (y/N) ");
                    var answer = Console.ReadLine()?.Trim();
                    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) includedKeys.Add(key);
                }
            }

            for (var i = 0; i < classifications.Count; i++) {
                var c = classifications[i];
                if (c.ExcludedRepoKey is null) continue;
                if (!includedKeys.Contains(c.ExcludedRepoKey)) {
                    classifications[i] = c with { Status = ClassificationStatus.Excluded };
                }
            }
        }

        // --- Plan grid ---
        var planCounts = new ClassificationCounts(
            New:           classifications.Count(c => c.Status == ClassificationStatus.New),
            Partial:       classifications.Count(c => c.Status == ClassificationStatus.Partial),
            AlreadyLoaded: classifications.Count(c => c.Status == ClassificationStatus.AlreadyLoaded),
            TooShort:      classifications.Count(c => c.Status == ClassificationStatus.TooShort),
            Excluded:      classifications.Count(c => c.Status == ClassificationStatus.Excluded),
            ProbeError:    classifications.Count(c => c.Status == ClassificationStatus.ProbeError));

        display.BeginPhase("Plan");
        display.WritePlanGrid(planCounts);

        // --- Build chains + set continuation predecessors ---
        var continuationMap = BuildContinuationMapFromClassifications(classifications);
        classifications = [.. classifications.Select(c =>
            continuationMap.TryGetValue(c.SessionId, out var prev) ? c with { PreviousSessionId = prev } : c)];
        var chains = BuildImportChains(classifications);

        // --- Import ---
        var backgroundTasks = new List<Task>();
        var titleTaskCount = 0;
        var summaryTaskCount = 0;
        using var concurrencyLimit = new SemaphoreSlim(3);
        var titlesGenerated = 0;
        var titlesSkipped = 0;
        var titlesFailed = 0;
        var summariesGenerated = 0;
        var summariesFailed = 0;
        var titleFailures = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();
        var summaryFailures = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();

        var events = new ChainWorkerEvents {
            OnLineCompleted = s => display.Line(s, $"[green]✓[/] {Markup.Escape(s)}"),
            OnSubagentFinished = (_, aid, lines) => display.Line(
                $"  ↳ imported subagent {aid} ({lines} lines)",
                $"  [dim]↳[/] imported subagent [cyan]{Markup.Escape(aid)}[/] ({lines} lines)"),
            OnSessionErrored = (sid, reason) => display.Line(
                $"Skipping {sid} [{reason}]",
                $"[red]✗[/] Skipping [cyan]{Markup.Escape(sid)}[/] [{Markup.Escape(reason)}]"),
            OnTitleTaskReady = t => {
                var (sid, fp, _) = t;
                Interlocked.Increment(ref titleTaskCount);
                backgroundTasks.Add(Task.Run(async () => {
                    await concurrencyLimit.WaitAsync();
                    try {
                        var result = await GenerateTitleForImportAsync(httpClient, baseUrl, sid, fp);
                        switch (result) {
                            case TitleResult.Generated: Interlocked.Increment(ref titlesGenerated); break;
                            case TitleResult.Skipped:   Interlocked.Increment(ref titlesSkipped); break;
                            case TitleResult.Failed:
                                Interlocked.Increment(ref titlesFailed);
                                titleFailures.Add((sid, "generation error"));
                                break;
                        }
                    } finally { concurrencyLimit.Release(); }
                }));
            },
            OnSessionEnded = t => {
                if (!t.GenerateWhatsDone || !generateSummaries) return;
                Interlocked.Increment(ref summaryTaskCount);
                var sid = t.SessionId;
                backgroundTasks.Add(Task.Run(async () => {
                    await concurrencyLimit.WaitAsync();
                    try {
                        var rc = await WhatsDoneCommand.GenerateForSessionAsync(baseUrl, sid, _ => { });
                        if (rc == 0) Interlocked.Increment(ref summariesGenerated);
                        else {
                            Interlocked.Increment(ref summariesFailed);
                            summaryFailures.Add((sid, $"exit {rc}"));
                        }
                    } catch (Exception ex) {
                        Interlocked.Increment(ref summariesFailed);
                        summaryFailures.Add((sid, ex.Message));
                    } finally { concurrencyLimit.Release(); }
                }));
            },
        };

        ImportChainsResult importResult;
        if (chains.Count > 0) {
            display.BeginPhase($"Importing {chains.Sum(c => c.Count)} sessions");
            if (display.Tty) {
                var r = default(ImportChainsResult);
                await AnsiConsole.Progress()
                    .AutoClear(false).HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                        var bar = ctx.AddTask("[green]Importing[/]", maxValue: chains.Sum(c => c.Count));
                        var wrappedEvents = events with {
                            OnLineCompleted = s => { events.OnLineCompleted(s); bar.Increment(1); },
                        };
                        r = await ImportChainsAsync(httpClient, baseUrl, chains, wrappedEvents, CancellationToken.None);
                    });
                importResult = r;
            } else {
                importResult = await ImportChainsAsync(httpClient, baseUrl, chains, events, CancellationToken.None);
            }
        } else {
            importResult = new ImportChainsResult(0, 0, 0);
        }

        // --- Background phase (titles / summaries) ---
        var ranBackground = backgroundTasks.Count > 0;
        if (ranBackground) {
            display.BeginPhase("Titles & summaries");
            if (display.Tty) {
                await AnsiConsole.Progress()
                    .AutoClear(false).HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                        var titleTask = titleTaskCount > 0 ? ctx.AddTask("[cyan]Titles[/]", maxValue: titleTaskCount) : null;
                        var summaryTask = summaryTaskCount > 0 ? ctx.AddTask("[cyan]Summaries[/]", maxValue: summaryTaskCount) : null;
                        var seenT = 0; var seenS = 0;
                        while (backgroundTasks.Any(t => !t.IsCompleted)) {
                            titleTask?.Value = titlesGenerated + titlesFailed + titlesSkipped;
                            summaryTask?.Value = summariesGenerated + summariesFailed;
                            var tList = titleFailures.ToList();
                            var sList = summaryFailures.ToList();
                            for (var i = seenT; i < tList.Count; i++) {
                                var (sid, reason) = tList[i];
                                AnsiConsole.MarkupLine($"  [red]✗[/] title failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                            }
                            seenT = tList.Count;
                            for (var i = seenS; i < sList.Count; i++) {
                                var (sid, reason) = sList[i];
                                AnsiConsole.MarkupLine($"  [red]✗[/] summary failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                            }
                            seenS = sList.Count;
                            await Task.Delay(250);
                        }
                        try { await Task.WhenAll(backgroundTasks); } catch { /* per-task try/catch */ }
                        titleTask?.Value = titlesGenerated + titlesFailed + titlesSkipped;
                        summaryTask?.Value = summariesGenerated + summariesFailed;
                    });
            } else {
                display.Line($"Waiting for {backgroundTasks.Count} background task(s) (titles/summaries)...");
                try { await Task.WhenAll(backgroundTasks); } catch { /* per-task */ }
                foreach (var (sid, reason) in titleFailures)
                    display.Line($"  ✗ title failed for {sid}: {reason}");
                foreach (var (sid, reason) in summaryFailures)
                    display.Line($"  ✗ summary failed for {sid}: {reason}");
            }
        }

        // --- Done ---
        var final = new FinalCounts(
            Loaded: importResult.Loaded,
            Resumed: importResult.Resumed,
            AlreadyLoaded: planCounts.AlreadyLoaded,
            TooShort: planCounts.TooShort,
            Excluded: planCounts.Excluded,
            ProbeError: planCounts.ProbeError,
            Errored: importResult.Errored,
            TitlesGenerated: titlesGenerated,
            TitlesSkipped: titlesSkipped,
            TitlesFailed: titlesFailed,
            SummariesGenerated: summariesGenerated,
            SummariesFailed: summariesFailed,
            RanBackground: ranBackground,
            RequestedSummaries: summaryTaskCount > 0);
        display.WriteDoneGrid(final);
        return 0;
    }

    /// <summary>
    /// Wrapper around ClassifyAsync that ticks the supplied Spectre task once per
    /// completed probe. Matches ClassifyAsync signature otherwise.
    /// </summary>
    static async Task<List<SessionClassification>> ClassifyWithProgressAsync(
            HttpClient httpClient, string baseUrl,
            List<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
            int minLines, string[]? excludedRepos, ProgressTask bar, CancellationToken ct) {
        // Simple variant: run serial probes, ticking as we go. Parallelism for the
        // probe phase is preserved by calling ClassifyAsync; TTY tick accuracy is
        // best-effort (ticks come in bursts as batched tasks complete).
        var result = await ClassifyAsync(httpClient, baseUrl, transcripts, minLines, excludedRepos, ct);
        bar.Value = bar.MaxValue;
        return result;
    }

    /// <summary>
    /// Build a sessionId → previousSessionId map from classifications, grouping by slug.
    /// Replaces BuildContinuationMap which read transcripts again.
    /// </summary>
    static Dictionary<string, string> BuildContinuationMapFromClassifications(List<SessionClassification> classifications) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var bySlug = classifications
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal);

        foreach (var group in bySlug) {
            var chain = group
                .OrderBy(c => c.Meta.FirstTimestamp ?? DateTimeOffset.MinValue)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList();
            for (var i = 1; i < chain.Count; i++)
                map[chain[i].SessionId] = chain[i - 1].SessionId;
        }
        return map;
    }
```

Remove the now-dead `InlineProgress<T>` class definition at the top of `HistoryCommand` (it's replaced by `CallbackProgress`). Remove the now-dead `BuildContinuationMap` and `SortByContinuationOrder` helpers — their behavior is subsumed by `BuildContinuationMapFromClassifications` + chain grouping.

- [ ] **Step 2: Build and run all unit tests**

```bash
dotnet build src/kapacitor/kapacitor.csproj
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: build succeeds, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] HandleHistory: cutover to plan-then-execute pipeline

Discover → classify → plan grid → batch excluded-repo prompts →
parallel chain import → background phase → done grid. The old serial
loop and per-session skip spam are gone. Classification phase runs 8
probes concurrently; import phase runs 4 chains in parallel with
serial within-chain ordering.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: AOT publish gate

**Files:**
- None (verification only).

- [ ] **Step 1: Publish Release**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' | tee /tmp/kapacitor-aot-dev1579.txt
```

Expected: zero lines.

If any warning appears:
- Note the specific line (likely a Spectre API or System.Text.Json surface).
- Replace the offending call with a non-reflection alternative (see DEV-1573 commits for precedent — e.g. swap collection-expression `JsonArray` for its constructor).
- Re-publish until clean.

- [ ] **Step 2: Commit only if you changed anything**

If Step 1 required code changes, commit them:

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "$(cat <<'EOF'
[DEV-1579] Fix AOT trim warning in history import

<describe the specific warning and the swap that resolved it>

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

If no warnings appeared, skip the commit.

---

## Task 12: Manual verification

**Files:**
- None (manual testing only).

Each scenario runs the built binary (`dotnet run --project src/kapacitor/kapacitor.csproj -- <args>` or the published binary). Pick a working server URL that matches your local `kapacitor setup`.

- [ ] **Scenario 1 — Mixed re-run** (the big one)

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 15
```

Expected:
- `Discovering` rule → count line → `Probing` bar reaches 100%.
- `Plan` rule → plan grid with six rows, counts match reality (the re-run should show `AlreadyLoaded` = most sessions).
- If nothing to import: jumps straight to `Done` grid.
- If sessions to import: `Importing N sessions` rule, top-level bar, completion lines stream (out of order across chains is expected), no `Skipping [already loaded]` lines anywhere.
- `Done` grid with only non-zero optional rows.

- [ ] **Scenario 2 — Non-TTY (piped)**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 15 > /tmp/history.log 2>&1
cat /tmp/history.log | head -50
```

Expected: plain text with `== Discovering ==` / `== Plan ==` / `== Done ==` headers, no Spectre escape codes, plan/done grids as aligned text rows. Parallel imports still happen — look for interleaved `Loading {sid}...` lines if there was anything to import.

- [ ] **Scenario 3 — Single session filter**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 15 --session <sid>
```

Expected: plan grid shows a 1-row plan (one of the six statuses is 1, others 0); pipeline completes without crashing on a 1-element input.

- [ ] **Scenario 4 — With summaries**

```bash
dotnet run --project src/kapacitor/kapacitor.csproj -- history --min-lines 15 --generate-summaries
```

Expected: background phase renders `Titles & summaries` rule + two bars; individual failures stream as red `✗` lines during the phase.

- [ ] **Scenario 5 — Excluded repo (TTY)**

If your `~/.config/kapacitor/config.json` lists an excluded repo that has matching sessions locally, a plain re-run should show a single consent prompt per unique repo — not per session. Answer `n` → those sessions classify as `Excluded` in both grids. Answer `y` → they move to `New`/`Partial` and get imported.

- [ ] **Step 1: If any scenario reveals a bug**

Reproduce minimally, write a failing test if the bug is in a tested helper, fix it, and commit under the same ticket:

```bash
git add <files>
git commit -m "[DEV-1579] Fix <short description>

<details>

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 2: Sign off**

All five scenarios pass → the implementation is complete. Move on to PR creation.

---

## Self-review notes

Before opening a PR:

1. **Spec coverage** — run through each spec section and check off:
   - Defaults (min-lines 15) → Task 1.
   - Phase 1 classify with all 7 statuses → Tasks 4–6.
   - Excluded-repo batch prompt → Task 10 (in `HandleHistory`).
   - Phase 2 parallel chains → Task 8.
   - Plan / Done grids → Task 9.
   - Non-TTY fallback → Tasks 9–10 (both grids have non-TTY branches; ImportChainsAsync is display-agnostic).
   - AOT gate → Task 11.

2. **Dead code** — after Task 10, confirm these are gone: `InlineProgress<T>`, `BuildContinuationMap`, `SortByContinuationOrder`, and the `HistorySessionStatus` enum usage inside `HandleHistory`. The `HistorySessionStatus` enum itself can stay — it's referenced in `Models.cs` and may be used elsewhere.

3. **Sanity grep:** `grep -n "Skipping.*already loaded" src/kapacitor/Commands/HistoryCommand.cs` must return no hits.

4. **CLAUDE.md compliance** — AOT warnings must still be zero (Task 11).
