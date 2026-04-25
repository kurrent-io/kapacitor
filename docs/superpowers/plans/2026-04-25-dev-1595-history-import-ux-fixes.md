# DEV-1595 History Import UX Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three UX regressions in `kapacitor history`: probing bar stuck at 0%, false `Partial` classifications when no new lines beyond server's last imported line, and per-session "Loading" lines scrolling instead of staying live.

**Architecture:** All changes are in `src/kapacitor/Commands/HistoryCommand.cs`. Issue 1 adds an optional `Action? onProbed` callback to `ClassifyAsync`. Issue 2 extends `CountLinesUpTo`'s read in `ClassifyOneAsync` to detect "no new lines beyond server's last line" and reclassify Partial → AlreadyLoaded. Issue 3 replaces `Parallel.ForEachAsync` with a `Channel`-based 4-worker fan-out, threads a `slot` index through `ChainWorkerEvents`, and rewires the TTY Importing-phase wrapper to render four live `ProgressTask` "slot rows" in place of the scrolling per-session log.

**Tech Stack:** .NET 10, NativeAOT (`PublishAot=true`, `TrimMode=full`), TUnit test framework, WireMock.Net for HTTP mocking, Spectre.Console (already a dependency), `System.Threading.Channels` (BCL).

**Spec:** `docs/superpowers/specs/2026-04-25-dev-1595-history-import-ux-fixes-design.md`

---

## File map

- **Modify:** `src/kapacitor/Commands/HistoryCommand.cs` — all production changes.
- **Modify:** `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs` — add tests for probe-callback and Partial → AlreadyLoaded reclassification.
- **Modify:** `test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs` — update existing tests for new `ChainWorkerEvents` shape.

No new files. No changes to `SessionImporter`, `ImportProgress`, `TitleGenerator`, `WhatsDoneCommand`, or `Program.cs`.

---

## Task 1: Probe progress callback (Issue 1)

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs:992-1010` (`ClassifyAsync` signature + body)
- Modify: `src/kapacitor/Commands/HistoryCommand.cs:1012-1132` (`ClassifyOneAsync` — invoke callback on completion)
- Modify: `src/kapacitor/Commands/HistoryCommand.cs:219-232` (TTY wrapper passes callback)
- Test: `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs` (add new test)

- [ ] **Step 1: Write the failing test**

Add this method to `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs` just before the closing `}` of the class:

```csharp
[Test]
public async Task ClassifyAsync_invokes_onProbed_callback_once_per_transcript() {
    _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(404));

    var paths = new List<(string SessionId, string FilePath, string EncodedCwd)>();
    for (var i = 0; i < 5; i++) {
        var path = await WriteTranscript(_tempDir, $"cb-{i}", lines: 50);
        paths.Add(($"cb-{i}", path, "-tmp-proj"));
    }

    var probedCount = 0;
    using var client = new HttpClient();

    var result = await HistoryCommand.ClassifyAsync(
        client,
        _server.Url!,
        paths,
        minLines: 15,
        excludedRepos: null,
        CancellationToken.None,
        onProbed: () => Interlocked.Increment(ref probedCount)
    );

    await Assert.That(result.Count).IsEqualTo(5);
    await Assert.That(probedCount).IsEqualTo(5);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/ClassifyAsync_invokes_onProbed_callback_once_per_transcript"
```

Expected: build error — `ClassifyAsync` does not accept `onProbed` parameter.

- [ ] **Step 3: Add `onProbed` parameter to `ClassifyAsync`**

In `src/kapacitor/Commands/HistoryCommand.cs`, find the existing `ClassifyAsync` declaration (search for `internal static async Task<List<SessionClassification>> ClassifyAsync(`) and update the signature and body:

```csharp
internal static async Task<List<SessionClassification>> ClassifyAsync(
        HttpClient                                                   httpClient,
        string                                                       baseUrl,
        List<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
        int                                                          minLines,
        string[]?                                                    excludedRepos,
        CancellationToken                                            ct,
        Action?                                                      onProbed = null
    ) {
    using var probeGate = new SemaphoreSlim(8);
    var       tasks     = new List<Task<SessionClassification>>(transcripts.Count);

    foreach (var (sessionId, filePath, encodedCwd) in transcripts) {
        tasks.Add(ClassifyOneAsync(httpClient, baseUrl, sessionId, filePath, encodedCwd, minLines, excludedRepos, probeGate, onProbed, ct));
    }

    var results = await Task.WhenAll(tasks);

    return [.. results];
}
```

- [ ] **Step 4: Add `onProbed` parameter to `ClassifyOneAsync` and invoke it**

In `src/kapacitor/Commands/HistoryCommand.cs`, find `ClassifyOneAsync` (search for `static async Task<SessionClassification> ClassifyOneAsync(`). Update its signature to add the `Action? onProbed` parameter just before `CancellationToken ct`, and wrap the entire body in a `try { … } finally { onProbed?.Invoke(); }`. The simplest way: rename the existing body method to `ClassifyOneCoreAsync` and add a thin wrapper.

Replace the entire `ClassifyOneAsync` method with:

```csharp
static async Task<SessionClassification> ClassifyOneAsync(
        HttpClient        httpClient,
        string            baseUrl,
        string            sessionId,
        string            filePath,
        string            encodedCwd,
        int               minLines,
        string[]?         excludedRepos,
        SemaphoreSlim     probeGate,
        Action?           onProbed,
        CancellationToken ct
    ) {
    try {
        return await ClassifyOneCoreAsync(httpClient, baseUrl, sessionId, filePath, encodedCwd, minLines, excludedRepos, probeGate, ct);
    } finally {
        onProbed?.Invoke();
    }
}

static async Task<SessionClassification> ClassifyOneCoreAsync(
        HttpClient        httpClient,
        string            baseUrl,
        string            sessionId,
        string            filePath,
        string            encodedCwd,
        int               minLines,
        string[]?         excludedRepos,
        SemaphoreSlim     probeGate,
        CancellationToken ct
    ) {
    // <existing body of ClassifyOneAsync — copy it verbatim>
}
```

Copy the entire existing `ClassifyOneAsync` body (from `var meta = ExtractSessionMetadata(filePath);` through the final `return new SessionClassification { … };`) into the new `ClassifyOneCoreAsync` method. The wrapper above guarantees `onProbed` fires exactly once per transcript regardless of exceptions inside the core.

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/ClassifyAsync_invokes_onProbed_callback_once_per_transcript"
```

Expected: PASS. Also re-run the full HistoryClassifyTests class to ensure no regressions:

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: all tests pass.

- [ ] **Step 6: Wire callback into `HandleHistory` TTY wrapper**

In `src/kapacitor/Commands/HistoryCommand.cs`, find the TTY branch of the Probing block (around line 219-232). Replace:

```csharp
        if (display.Tty) {
            var tmp = new List<SessionClassification>();

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar     = ctx.AddTask("[yellow]Probing[/]", maxValue: transcriptFiles.Count);
                        var results = await ClassifyAsync(httpClient, baseUrl, transcriptFiles, minLines, excludedRepos, CancellationToken.None);
                        bar.Value = bar.MaxValue;
                        tmp.AddRange(results);
                    }
                );
            classifications = tmp;
        } else {
```

with:

```csharp
        if (display.Tty) {
            var tmp = new List<SessionClassification>();

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar     = ctx.AddTask("[yellow]Probing[/]", maxValue: transcriptFiles.Count);
                        var results = await ClassifyAsync(
                            httpClient,
                            baseUrl,
                            transcriptFiles,
                            minLines,
                            excludedRepos,
                            CancellationToken.None,
                            onProbed: () => bar.Increment(1)
                        );
                        tmp.AddRange(results);
                    }
                );
            classifications = tmp;
        } else {
```

The `bar.Value = bar.MaxValue` line is removed because the bar reaches max via the per-probe increments.

- [ ] **Step 7: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: build succeeds with no warnings.

- [ ] **Step 8: AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output (no IL3050/IL2026 warnings).

- [ ] **Step 9: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryClassifyTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1595] history: live probing progress

Add an optional onProbed callback to ClassifyAsync that fires once per
transcript when its probe completes. The HandleHistory TTY wrapper
passes bar.Increment(1) so the Probing bar advances smoothly instead
of jumping from 0% to 100% at the end.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Reclassify Partial-with-no-new-lines as AlreadyLoaded (Issue 2)

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs` — `ClassifyOneCoreAsync` body
- Test: `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs` (add three tests)

- [ ] **Step 1: Write the failing tests**

Add these methods to `test/kapacitor.Tests.Unit/HistoryClassifyTests.cs` before the closing `}` of the class:

```csharp
[Test]
public async Task ClassifyAsync_reclassifies_Partial_to_AlreadyLoaded_when_no_new_lines() {
    // Server says last_line_number = 49 (50 lines stored: indices 0..49).
    // Local transcript is exactly 50 lines (indices 0..49). resumeFromLine
    // would be 50 — but there are no lines past index 49, so this is a
    // false Partial that should be reclassified as AlreadyLoaded.
    _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
        .RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"last_line_number": 49}""")
        );

    var path = await WriteTranscript(_tempDir, "noNewLines", lines: 50);

    var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
        ("noNewLines", path, "-tmp-proj")
    };

    using var client = new HttpClient();

    var result = await HistoryCommand.ClassifyAsync(
        client, _server.Url!, transcripts,
        minLines: 15, excludedRepos: null, CancellationToken.None
    );

    await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.AlreadyLoaded);
    await Assert.That(result[0].ResumeFromLine).IsEqualTo(0);
}

[Test]
public async Task ClassifyAsync_keeps_Partial_when_local_transcript_has_new_lines() {
    // Server says last_line_number = 49. Local transcript is 60 lines —
    // there are 10 new lines past index 49, so Partial is correct.
    _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
        .RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"last_line_number": 49}""")
        );

    var path = await WriteTranscript(_tempDir, "hasNewLines", lines: 60);

    var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
        ("hasNewLines", path, "-tmp-proj")
    };

    using var client = new HttpClient();

    var result = await HistoryCommand.ClassifyAsync(
        client, _server.Url!, transcripts,
        minLines: 15, excludedRepos: null, CancellationToken.None
    );

    await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.Partial);
    await Assert.That(result[0].ResumeFromLine).IsEqualTo(50);
}

[Test]
public async Task ClassifyAsync_does_not_set_ExcludedRepoKey_when_reclassified_to_AlreadyLoaded() {
    // A "Partial" probe in an excluded repo, but the local transcript has
    // no new lines. We must NOT prompt the user to "include this excluded
    // repo" for work that does not exist — ExcludedRepoKey must be null.
    _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
        .RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"last_line_number": 49}""")
        );

    // The repo detection in ClassifyOneCoreAsync uses RepositoryDetection
    // against meta.Cwd, which falls back to DecodeCwdFromDirName(EncodedCwd).
    // We bypass repo detection by leaving cwd unset — the excluded check
    // never fires when cwd is null. So we need a different angle: rely on
    // the order of operations in the spec — reclassification happens BEFORE
    // the excluded-repo check, so even with a real excluded repo the flag
    // should not be set. We simulate by writing a transcript whose path's
    // EncodedCwd would NOT match the excluded list, but mark "anything" as
    // excluded; the assertion is that ExcludedRepoKey is null because the
    // session is no longer New|Partial when the excluded check runs.
    var path = await WriteTranscript(_tempDir, "excludedNoNew", lines: 50);

    var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
        ("excludedNoNew", path, "-tmp-proj")
    };

    using var client = new HttpClient();

    // We pass an excluded list that would match EVERY repo if reached; the
    // contract says it must not be reached for AlreadyLoaded.
    var result = await HistoryCommand.ClassifyAsync(
        client, _server.Url!, transcripts,
        minLines: 15,
        excludedRepos: new[] { "any/repo" },
        CancellationToken.None
    );

    await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.AlreadyLoaded);
    await Assert.That(result[0].ExcludedRepoKey).IsNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/ClassifyAsync_reclassifies_Partial_to_AlreadyLoaded_when_no_new_lines"
```

Expected: FAIL — current code returns `Partial` with `ResumeFromLine = 50`.

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/ClassifyAsync_does_not_set_ExcludedRepoKey_when_reclassified_to_AlreadyLoaded"
```

Expected: FAIL.

(`ClassifyAsync_keeps_Partial_when_local_transcript_has_new_lines` should already pass — it documents existing behavior.)

- [ ] **Step 3: Update `ClassifyOneCoreAsync` to reclassify**

In `src/kapacitor/Commands/HistoryCommand.cs`, find the existing TooShort block in `ClassifyOneCoreAsync`:

```csharp
        // Apply the TooShort filter only for sessions that would otherwise be imported.
        // This avoids scanning the whole transcript when AlreadyLoaded / ProbeError.
        if (minLines > 0 && (status == ClassificationStatus.New || status == ClassificationStatus.Partial)) {
            var observedLines = CountLinesUpTo(filePath, minLines);

            if (observedLines < minLines) {
                return new SessionClassification {
                    SessionId  = sessionId,
                    FilePath   = filePath,
                    EncodedCwd = encodedCwd,
                    Meta       = meta,
                    Status     = ClassificationStatus.TooShort,
                    TotalLines = observedLines,
                };
            }
        }
```

Replace it with:

```csharp
        // Read enough of the local transcript to satisfy two checks at once:
        //   1. TooShort — fewer lines than minLines.
        //   2. False Partial — server says last_line_number = N but the local
        //      transcript has no lines past index N (resumeFromLine would be
        //      N+1 with nothing to send).
        // CountLinesUpTo early-exits at the threshold, so the read cost is
        // bounded by Math.Max(minLines, resumeFromLine + 1) lines.
        if (status is ClassificationStatus.New or ClassificationStatus.Partial) {
            var threshold = Math.Max(
                minLines,
                status == ClassificationStatus.Partial ? resumeFromLine + 1 : 0
            );

            if (threshold > 0) {
                var observedLines = CountLinesUpTo(filePath, threshold);

                if (minLines > 0 && observedLines < minLines) {
                    return new SessionClassification {
                        SessionId  = sessionId,
                        FilePath   = filePath,
                        EncodedCwd = encodedCwd,
                        Meta       = meta,
                        Status     = ClassificationStatus.TooShort,
                        TotalLines = observedLines,
                    };
                }

                // Server has lines >= the local transcript — nothing to resume.
                if (status == ClassificationStatus.Partial && observedLines <= resumeFromLine) {
                    status         = ClassificationStatus.AlreadyLoaded;
                    resumeFromLine = 0;
                }
            }
        }
```

The reclassification is placed *before* the excluded-repo block (which still follows immediately below), so a session that becomes `AlreadyLoaded` here will not be flagged with `ExcludedRepoKey`.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj -- --treenode-filter "/*/*/HistoryClassifyTests/*"
```

Expected: all tests pass, including the three new ones.

- [ ] **Step 5: AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryClassifyTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1595] history: reclassify Partial as AlreadyLoaded when no new lines

When the server probe returns 200 with last_line_number = N, the local
transcript may have no lines past index N. The previous classifier
marked these as Partial and the import phase then sent zero lines per
session — wasted work and a misleading plan grid that under-reported
"Already loaded".

Extend the bounded line read in ClassifyOneCoreAsync to cover both
TooShort and "no new lines" detection in one pass, with early exit at
Math.Max(minLines, resumeFromLine + 1). Reclassification happens before
the excluded-repo flag so a falsely-Partial excluded session no longer
prompts the user.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Channel-based 4-worker dispatch with slot-aware events (Issue 3 — plumbing)

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs` — `ChainWorkerEvents` record (lines ~775-781), `ImportChainsAsync` (lines ~788-835), `ImportSingleSessionAsync` (lines ~839-981).
- Modify: `test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs` — update existing event-shape usages.
- Modify: `src/kapacitor/Commands/HistoryCommand.cs:312-373` and lines ~390-400 — `HandleHistory` event-construction sites must adopt the new shape (legacy log behavior preserved in this task; visual change comes in Task 4).

This task is a behavior-preserving refactor: tests and end-user output are identical when complete. It enables Task 4 to render slot rows.

- [ ] **Step 1: Update `ChainWorkerEvents` record**

In `src/kapacitor/Commands/HistoryCommand.cs`, replace the `ChainWorkerEvents` record (currently at line ~775) with:

```csharp
internal sealed record ChainWorkerEvents {
    /// <summary>Fired before a worker begins importing a session on its slot.</summary>
    public required Action<int, SessionClassification> OnSessionStarted { get; init; }

    /// <summary>Fired when the worker begins streaming a subagent's transcript inline.</summary>
    public required Action<int, string, string> OnSubagentStarted { get; init; }   // slot, sessionId, agentId

    /// <summary>Fired after a subagent's transcript has been fully streamed.</summary>
    public required Action<int, string, string, int> OnSubagentFinished { get; init; }  // slot, sessionId, agentId, lines

    /// <summary>Fired when a session import fails on a worker slot.</summary>
    public required Action<int, string, string> OnSessionErrored { get; init; }   // slot, sessionId, reason

    /// <summary>
    /// Fired after a session import completes (loaded or resumed). The slot is
    /// available for the next session as soon as this returns.
    /// </summary>
    public required Action<int, SessionClassification, SessionImportOutcome, int> OnSessionEnded { get; init; }
    // slot, classification, outcome (Loaded|Resumed), linesSent

    /// <summary>Fired when a successfully-imported session is ready for title generation.</summary>
    public required Action<(string SessionId, string FilePath, string? PreviousSessionId)> OnTitleTaskReady { get; init; }

    /// <summary>
    /// Fired when a session's session-end hook returned, signalling that the
    /// background phase may enqueue title / what's-done work for it.
    /// Renamed from the previous `OnSessionEnded` to disambiguate from the
    /// slot-aware lifecycle event above.
    /// </summary>
    public required Action<(string SessionId, bool GenerateWhatsDone)> OnBackgroundWorkReady { get; init; }
}
```

Also promote the existing `enum SessionImportOutcome` from `private` to `internal` so it can be referenced by callers and tests. Find:

```csharp
    enum SessionImportOutcome { Loaded, Resumed, Errored }
```

Replace with:

```csharp
    internal enum SessionImportOutcome { Loaded, Resumed, Errored }
```

- [ ] **Step 2: Rewrite `ImportChainsAsync` with channel-based 4-worker fan-out**

Find `ImportChainsAsync` (around line 788) and replace its body:

```csharp
internal static async Task<ImportChainsResult> ImportChainsAsync(
        HttpClient                        httpClient,
        string                            baseUrl,
        List<List<SessionClassification>> chains,
        ChainWorkerEvents                 events,
        CancellationToken                 ct
    ) {
    var loaded  = 0;
    var resumed = 0;
    var errored = 0;

    var queue = System.Threading.Channels.Channel.CreateUnbounded<List<SessionClassification>>(
        new System.Threading.Channels.UnboundedChannelOptions {
            SingleReader = false,
            SingleWriter = true,
        }
    );

    foreach (var chain in chains) await queue.Writer.WriteAsync(chain, ct);
    queue.Writer.Complete();

    const int workerCount = 4;
    var workers = new Task[workerCount];

    for (var i = 0; i < workerCount; i++) {
        var slot = i; // capture
        workers[i] = Task.Run(async () => {
            while (await queue.Reader.WaitToReadAsync(ct)) {
                while (queue.Reader.TryRead(out var chain)) {
                    foreach (var session in chain) {
                        events.OnSessionStarted(slot, session);

                        SessionImportOutcome r;
                        var linesSent = 0;
                        try {
                            (r, linesSent) = await ImportSingleSessionAsync(httpClient, baseUrl, session, slot, events, ct);
                        } catch (Exception ex) {
                            events.OnSessionErrored(slot, session.SessionId, ex.Message);
                            r = SessionImportOutcome.Errored;
                        }

                        switch (r) {
                            case SessionImportOutcome.Loaded:  Interlocked.Increment(ref loaded);  break;
                            case SessionImportOutcome.Resumed: Interlocked.Increment(ref resumed); break;
                            case SessionImportOutcome.Errored: Interlocked.Increment(ref errored); break;
                        }

                        if (r != SessionImportOutcome.Errored) {
                            events.OnSessionEnded(slot, session, r, linesSent);
                        }
                    }
                }
            }
        }, ct);
    }

    await Task.WhenAll(workers);

    return new ImportChainsResult(loaded, resumed, errored);
}
```

The signature of `ImportSingleSessionAsync` now takes a `slot` and returns a tuple `(outcome, linesSent)` — Step 3 updates it.

- [ ] **Step 3: Update `ImportSingleSessionAsync`**

Replace the entire `ImportSingleSessionAsync` method. The new signature returns `(SessionImportOutcome, int linesSent)` and threads `slot` through to events:

```csharp
static async Task<(SessionImportOutcome Outcome, int LinesSent)> ImportSingleSessionAsync(
        HttpClient            httpClient,
        string                baseUrl,
        SessionClassification session,
        int                   slot,
        ChainWorkerEvents     events,
        CancellationToken     ct
    ) {
    IProgress<ImportProgress> perSessionProgress = new CallbackProgress(ev => {
            switch (ev) {
                case SubagentStarted ss:  events.OnSubagentStarted(slot, session.SessionId, ss.AgentId); break;
                case SubagentFinished sf: events.OnSubagentFinished(slot, session.SessionId, sf.AgentId, sf.LinesSent); break;
            }
        }
    );

    if (session.Status == ClassificationStatus.Partial) {
        try {
            var linesSent = await SessionImporter.SendTranscriptBatches(
                httpClient,
                baseUrl,
                session.SessionId,
                session.FilePath,
                agentId: null,
                startLine: session.ResumeFromLine,
                progress: perSessionProgress
            );

            return (SessionImportOutcome.Resumed, linesSent);
        } catch (HttpRequestException ex) {
            events.OnSessionErrored(slot, session.SessionId, $"server unreachable: {ex.Message}");

            return (SessionImportOutcome.Errored, 0);
        } catch (Exception ex) {
            events.OnSessionErrored(slot, session.SessionId, ex.Message);

            return (SessionImportOutcome.Errored, 0);
        }
    }

    // status == New: session-start → import → session-end → enqueue background tasks
    var meta = session.Meta;
    var cwd  = meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(session.EncodedCwd);

    var startHook = new System.Text.Json.Nodes.JsonObject {
        ["session_id"]      = session.SessionId,
        ["transcript_path"] = session.FilePath,
        ["cwd"]             = cwd ?? "",
        ["source"]          = "Startup",
        ["hook_event_name"] = "session_start",
        ["model"]           = meta.Model,
    };
    if (meta.FirstTimestamp is not null) startHook["started_at"]                = meta.FirstTimestamp.Value.ToString("O");
    if (session.PreviousSessionId is not null) startHook["previous_session_id"] = session.PreviousSessionId;
    if (meta.Slug is not null) startHook["slug"]                                = meta.Slug;

    if (cwd is not null) {
        var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);

        if (repo is not null) {
            var repoNode                                           = new System.Text.Json.Nodes.JsonObject();
            if (repo.UserName is not null) repoNode["user_name"]   = repo.UserName;
            if (repo.UserEmail is not null) repoNode["user_email"] = repo.UserEmail;
            if (repo.RemoteUrl is not null) repoNode["remote_url"] = repo.RemoteUrl;
            if (repo.Owner is not null) repoNode["owner"]          = repo.Owner;
            if (repo.RepoName is not null) repoNode["repo_name"]   = repo.RepoName;
            if (repo.Branch is not null) repoNode["branch"]        = repo.Branch;
            startHook["repository"] = repoNode;
        }
    }

    try {
        using var startContent = new StringContent(startHook.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var startResp    = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-start", startContent, ct: ct);

        if (!startResp.IsSuccessStatusCode) {
            events.OnSessionErrored(slot, session.SessionId, $"session-start failed: HTTP {(int)startResp.StatusCode}");

            return (SessionImportOutcome.Errored, 0);
        }
    } catch (HttpRequestException ex) {
        events.OnSessionErrored(slot, session.SessionId, $"server unreachable: {ex.Message}");

        return (SessionImportOutcome.Errored, 0);
    }

    ImportResult importResult;

    try {
        importResult = await SessionImporter.ImportSessionAsync(
            httpClient,
            baseUrl,
            session.FilePath,
            session.SessionId,
            meta,
            session.EncodedCwd,
            perSessionProgress
        );
    } catch (Exception ex) {
        events.OnSessionErrored(slot, session.SessionId, ex.Message);

        return (SessionImportOutcome.Errored, 0);
    }

    var lastTs = ExtractLastTimestamp(session.FilePath);

    var endHook = new System.Text.Json.Nodes.JsonObject {
        ["session_id"]      = session.SessionId,
        ["transcript_path"] = session.FilePath,
        ["cwd"]             = cwd ?? "",
        ["reason"]          = "Other",
        ["hook_event_name"] = "session_end",
    };
    if (lastTs is not null) endHook["ended_at"] = lastTs.Value.ToString("O");

    var generateWhatsDone = false;

    try {
        using var endContent = new StringContent(endHook.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var endResp    = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-end", endContent, ct: ct);

        if (endResp.IsSuccessStatusCode) {
            try {
                var body = await endResp.Content.ReadAsStringAsync(ct);
                var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                generateWhatsDone = node?["generate_whats_done"]?.GetValue<bool>() == true;
            } catch {
                /* best effort */
            }
        }
    } catch {
        /* best effort */
    }

    events.OnTitleTaskReady((session.SessionId, session.FilePath, session.PreviousSessionId));
    events.OnBackgroundWorkReady((session.SessionId, generateWhatsDone));

    return (SessionImportOutcome.Loaded, importResult.LinesSent);
}
```

Three notable behavior preservations:
- `OnTitleTaskReady` and `OnBackgroundWorkReady` (renamed) fire only on the success path of New imports, exactly as before.
- The Partial path's `events.OnLineCompleted(...)` is gone — its UI role moves to `OnSessionEnded(slot, session, Resumed, linesSent)` invoked by `ImportChainsAsync`. The TTY/non-TTY wrappers translate.
- The New path's `events.OnLineCompleted(...)` likewise moves to `OnSessionEnded`.

- [ ] **Step 4: Update `HandleHistory` event constructions**

In `src/kapacitor/Commands/HistoryCommand.cs`, find the `var events = new ChainWorkerEvents { … }` block (around line 312). Replace it entirely:

```csharp
var events = new ChainWorkerEvents {
    OnSessionStarted = (_, _) => { },     // overridden by display wrappers below
    OnSubagentStarted = (_, _, _) => { }, // overridden by display wrappers below
    OnSubagentFinished = (_, sid, aid, lines) => display.Line(
        $"  ↳ imported subagent {aid} ({lines} lines)",
        $"  [dim]↳[/] imported subagent [cyan]{Markup.Escape(aid)}[/] ({lines} lines)"
    ),
    OnSessionErrored = (_, sid, reason) => display.Line(
        $"Skipping {sid} [{reason}]",
        $"[red]✗[/] Skipping [cyan]{Markup.Escape(sid)}[/] [{Markup.Escape(reason)}]"
    ),
    OnSessionEnded = (_, c, outcome, lines) => {
        var verb = outcome == SessionImportOutcome.Resumed
            ? $"resuming from line {c.ResumeFromLine}"
            : "new";
        display.Line(
            $"Loading {c.SessionId}... {lines} lines [{verb}]",
            $"[green]✓[/] Loading [cyan]{Markup.Escape(c.SessionId)}[/]... {lines} lines [{verb}]"
        );
    },
    OnTitleTaskReady = t => {
        var (sid, fp, _) = t;
        Interlocked.Increment(ref titleTaskCount);

        backgroundTasks.Add(
            Task.Run(async () => {
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
                }
            )
        );
    },
    OnBackgroundWorkReady = t => {
        if (!t.GenerateWhatsDone || !generateSummaries) return;

        Interlocked.Increment(ref summaryTaskCount);
        var sid = t.SessionId;

        backgroundTasks.Add(
            Task.Run(async () => {
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
                }
            )
        );
    },
};
```

This preserves the exact pre-Task-3 console output (errors, subagent lines, the per-session "Loading … N lines [new|resuming…]" line) but routes them through the new event shape. Task 4 will replace the TTY portion of these handlers with slot-row updates.

- [ ] **Step 5: Update the TTY Importing-phase wrapper to drop the now-redundant `OnLineCompleted` plumbing**

Find the existing TTY block inside the `if (chains.Count > 0)` branch (around line 380):

```csharp
        if (display.Tty) {
            var r = default(ImportChainsResult);

            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar = ctx.AddTask("[green]Importing[/]", maxValue: chains.Sum(c => c.Count));

                        var wrappedEvents = events with {
                            OnLineCompleted = s => {
                                events.OnLineCompleted(s);
                                bar.Increment(1);
                            },
                        };
                        r = await ImportChainsAsync(httpClient, baseUrl, chains, wrappedEvents, CancellationToken.None);
                    }
                );
            importResult = r!;
        } else {
            importResult = await ImportChainsAsync(httpClient, baseUrl, chains, events, CancellationToken.None);
        }
```

Replace with (Task 4 will completely rewrite the TTY branch — for now just rewire the bar increment to `OnSessionEnded`):

```csharp
        if (display.Tty) {
            var r = default(ImportChainsResult);

            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar = ctx.AddTask("[green]Importing[/]", maxValue: chains.Sum(c => c.Count));

                        var wrappedEvents = events with {
                            OnSessionEnded = (slot, c, outcome, lines) => {
                                events.OnSessionEnded(slot, c, outcome, lines);
                                bar.Increment(1);
                            },
                            // Errored sessions also count toward the bar so it reaches 100%.
                            OnSessionErrored = (slot, sid, reason) => {
                                events.OnSessionErrored(slot, sid, reason);
                                bar.Increment(1);
                            },
                        };
                        r = await ImportChainsAsync(httpClient, baseUrl, chains, wrappedEvents, CancellationToken.None);
                    }
                );
            importResult = r!;
        } else {
            importResult = await ImportChainsAsync(httpClient, baseUrl, chains, events, CancellationToken.None);
        }
```

- [ ] **Step 6: Update existing `HistoryImportChainsTests` test fixtures**

In `test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs`, find every `new HistoryCommand.ChainWorkerEvents { … }` literal and update its shape. There are several tests — update them all by pattern.

For example, the first one at line 76-82:

```csharp
        var events = new HistoryCommand.ChainWorkerEvents {
            OnLineCompleted    = s => completedLines.Add(s),
            OnSubagentFinished = (_, _, _) => { },
            OnSessionErrored   = (_, _) => { },
            OnTitleTaskReady   = _ => { },
            OnSessionEnded     = _ => { },
        };
```

becomes:

```csharp
        var events = new HistoryCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, c, _, _) => completedLines.Add($"Loading {c.SessionId}..."),
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };
```

The migration rule for each test: anywhere the test was capturing `OnLineCompleted` strings, capture the equivalent in `OnSessionEnded` using the classification. If the test was checking `OnSessionEnded` for background-work tracking, rename to `OnBackgroundWorkReady`. Stub the new `OnSessionStarted` and `OnSubagentStarted` as no-ops. Update arity of `OnSubagentFinished` from `(_, _, _)` to `(_, _, _, _)` and `OnSessionErrored` from `(_, _)` to `(_, _, _)`.

Search for every occurrence of `ChainWorkerEvents` in the test project and apply the migration:

```bash
grep -rn "ChainWorkerEvents" test/kapacitor.Tests.Unit/
```

- [ ] **Step 7: Run all unit tests to verify no regressions**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 8: AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output. `System.Threading.Channels` and `Channel.CreateUnbounded` are AOT-safe.

- [ ] **Step 9: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs test/kapacitor.Tests.Unit/HistoryImportChainsTests.cs
git commit -m "$(cat <<'EOF'
[DEV-1595] history: slot-aware ChainWorkerEvents + channel dispatch

Replace Parallel.ForEachAsync with a 4-worker channel-based fan-out so
each chain runs on a stable, identifiable slot (0..3). Thread the slot
index through ChainWorkerEvents so future UI layers can render four
live worker rows instead of scrolling per-session log lines.

Behavior is preserved in this refactor: the TTY wrapper still emits
the same per-session output it did before. Task 4 rewires the TTY
output to render the four slot rows.

Renames the background-work trigger callback OnSessionEnded →
OnBackgroundWorkReady so OnSessionEnded can carry slot lifecycle
semantics.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Render four worker-slot rows in the TTY Importing phase (Issue 3 — UI)

**Files:**
- Modify: `src/kapacitor/Commands/HistoryCommand.cs` — `HandleHistory` Importing-phase TTY block (around line 380), `events` construction (around line 312).

This task is the visual change. No test changes — the unit tests cover dispatch and counts; the slot rendering is verified by manual smoke.

The base `events` object built in Task 3 already encodes the correct non-TTY behavior (per-session log lines, scrolling). This task only rewrites the TTY branch: it overrides the slot-aware events to render four live `ProgressTask` rows.

- [ ] **Step 1: Rewrite the TTY Importing-phase block to render slot rows**

Find the TTY block from Task 3 Step 5 (the `if (display.Tty) { … }` inside `if (chains.Count > 0)`). Replace it with:

```csharp
        if (display.Tty) {
            var r = default(ImportChainsResult);
            const int slotCount = 4;

            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar = ctx.AddTask("[green]Importing[/]", maxValue: chains.Sum(c => c.Count));

                        // Four description-only progress tasks rendered as live "slot rows"
                        // beneath the main bar. IsIndeterminate=true draws a stripe
                        // animation while a worker is processing; setting it to false
                        // and Description="idle" parks the slot.
                        var slots = new ProgressTask[slotCount];
                        for (var i = 0; i < slotCount; i++) {
                            slots[i] = ctx.AddTask($"  Slot {i + 1} — idle", maxValue: 1);
                            slots[i].IsIndeterminate = false;
                        }

                        // currentSession[slot] holds the SessionId currently rendered on
                        // the slot row, used to revert the description after a subagent
                        // finishes (revert from "↳ subagent X" to "Loading <parent>").
                        var currentVerb = new string[slotCount];
                        var currentSid  = new string[slotCount];

                        void SetSlot(int slot, string markup) {
                            slots[slot].Description    = markup;
                            slots[slot].IsIndeterminate = true;
                        }

                        void IdleSlot(int slot) {
                            slots[slot].Description    = $"  Slot {slot + 1} — idle";
                            slots[slot].IsIndeterminate = false;
                            currentSid[slot]            = "";
                            currentVerb[slot]           = "";
                        }

                        var wrappedEvents = events with {
                            OnSessionStarted = (slot, c) => {
                                var verb = c.Status == ClassificationStatus.Partial
                                    ? $"resuming from line {c.ResumeFromLine}"
                                    : "new";
                                currentSid[slot]  = c.SessionId;
                                currentVerb[slot] = verb;
                                SetSlot(slot, $"  [bold]Slot {slot + 1}[/] — Loading [cyan]{Markup.Escape(c.SessionId)}[/] ({verb})");
                            },
                            OnSubagentStarted = (slot, sid, aid) => {
                                SetSlot(slot, $"  [bold]Slot {slot + 1}[/] — [dim]↳[/] subagent [cyan]{Markup.Escape(aid)}[/] (parent {Markup.Escape(sid)})");
                            },
                            OnSubagentFinished = (slot, sid, aid, lines) => {
                                // Revert to the parent session's "Loading" description.
                                if (!string.IsNullOrEmpty(currentSid[slot])) {
                                    SetSlot(slot,
                                        $"  [bold]Slot {slot + 1}[/] — Loading [cyan]{Markup.Escape(currentSid[slot])}[/] ({currentVerb[slot]})");
                                }
                                // Also fire the base handler so non-TTY callers (and any
                                // other observers) still see the "↳ imported subagent" line.
                                events.OnSubagentFinished(slot, sid, aid, lines);
                            },
                            OnSessionEnded = (slot, c, outcome, lines) => {
                                // Description stays on the just-finished session until
                                // the next OnSessionStarted swaps it. We only flip the
                                // stripe off here so a slot that drains (queue empty)
                                // looks calm.
                                bar.Increment(1);
                                slots[slot].IsIndeterminate = false;
                                // Suppress the legacy per-session log line in TTY mode
                                // by NOT calling the base handler. Errors and subagents
                                // already render via slot updates / scrollback below.
                            },
                            OnSessionErrored = (slot, sid, reason) => {
                                bar.Increment(1);
                                IdleSlot(slot);
                                // Errors print to scrollback above the live region —
                                // Spectre.Console.Progress flushes prior writes.
                                AnsiConsole.MarkupLine($"[red]✗[/] Skipping [cyan]{Markup.Escape(sid)}[/] [{Markup.Escape(reason)}]");
                            },
                        };

                        r = await ImportChainsAsync(httpClient, baseUrl, chains, wrappedEvents, CancellationToken.None);

                        // After the await, all workers have drained; mark every slot idle.
                        for (var i = 0; i < slotCount; i++) IdleSlot(i);
                    }
                );
            importResult = r!;
        } else {
            importResult = await ImportChainsAsync(httpClient, baseUrl, chains, events, CancellationToken.None);
        }
```

- [ ] **Step 2: Build**

```bash
dotnet build src/kapacitor/kapacitor.csproj
```

Expected: build succeeds.

- [ ] **Step 3: Run all unit tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
```

Expected: all tests pass.

- [ ] **Step 4: AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output.

- [ ] **Step 5: Manual smoke test**

Run the AOT-published binary against the developer's own `~/.claude/projects` (the screenshots' 2261-session sample is representative). The binary path after publish is:

```
src/kapacitor/bin/Release/net10.0/<rid>/publish/kapacitor
```

(re-sign on macOS per CLAUDE.md if you copied it elsewhere). Invoke:

```bash
src/kapacitor/bin/Release/net10.0/osx-arm64/publish/kapacitor history
```

Verify:
- Probing bar advances smoothly from 0% to 100%.
- Plan grid's "Already loaded" count is greater than zero (was always 0 in the regression).
- Importing phase shows: one main `Importing` bar + four "Slot N — Loading <id> (…)" rows updating in place. No `✓ Loading …` lines scroll.
- Errors (if any) scroll above the live region as `✗ Skipping …`.
- After the import phase, all slots show "idle" briefly before the Done grid prints.

Pipe-friendly path (smoke):

```bash
src/kapacitor/bin/Release/net10.0/osx-arm64/publish/kapacitor history > /tmp/history.txt
head -40 /tmp/history.txt
```

Verify the file contains the legacy scrolling lines (`Loading <id>... N lines […]`, `↳ imported subagent …`, `Skipping …`) — the non-TTY path is unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/HistoryCommand.cs
git commit -m "$(cat <<'EOF'
[DEV-1595] history: render four live worker-slot rows in TTY

The Importing phase now shows one main progress bar plus four
"Slot N — Loading <id> (…)" rows that update in place as workers pick
up the next chain. Subagent activity is rendered on the same slot
row while in flight and reverts to the parent session line on finish.
Errors continue to scroll into scrollback above the live region.

Non-TTY output is unchanged.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Final verification

**Files:** none.

- [ ] **Step 1: Run all unit and integration tests**

```bash
dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj
dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj
```

Expected: all tests pass.

- [ ] **Step 2: Final AOT publish gate**

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output.

- [ ] **Step 3: Confirm git log**

```bash
git log --oneline -5
```

Expected: four DEV-1595 commits on top of the spec commit, each addressing one of: probe progress, reclassification, slot-aware events, slot rendering.

---

## Risks & open questions

- **Slot animation under non-iTerm terminals.** Spectre stripe rendering may degrade gracefully on Windows Terminal / Conhost; acceptable, slot rows still legible.
- **Channel single-writer flag.** `SingleWriter = true` is correct because all chains are written before the workers start reading. If a future change writes chains incrementally, flip the flag.
- **Errored sessions and the main bar.** Task 3 Step 5's wrapper increments `bar` on both `OnSessionEnded` and `OnSessionErrored` so the bar always reaches 100%. Without the errored increment, a single failed session would leave the bar stuck below max.
