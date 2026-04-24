# DEV-1579 — History import UX: classify, parallelize, summarize

## Summary

Rework `kapacitor history` into an explicit plan-then-execute pipeline with parallel per-chain imports. The classification phase probes every transcript up front and prints a plan grid, so users see what the command will actually do before it starts. The import phase runs chains in parallel and no longer prints per-session "skipped" lines; the final summary grid holds the roll-ups. Non-TTY output loses the Spectre decorations but keeps the same parallelism, so CI runs benefit from the speed.

This is a follow-up to DEV-1573 (which introduced Spectre for the existing serial loop). Scope is limited to the `history` command.

Non-goals:

- Touching `setup`, `watch`, the daemon, hook handlers, or MCP servers.
- Exposing `--parallelism` / `--probe-concurrency` flags. Hardcoded values in v1; flags can be added later if real usage demands it.
- Changing the wire protocol or server-side behaviour.
- Reworking `SessionImporter` or `ImportProgress` signatures.

## Defaults

Raise the `history` command's default minimum-lines filter from **10 to 15**. Short transcripts with fewer than 15 lines are almost always trivial (single-prompt sessions, aborted invocations) and add noise to imports without meaningful data. Users who want to override can still pass `--min-lines 0` or any other value.

Touches two sites:

- `src/kapacitor/Program.cs:359` — `var minLines = 10;` → `var minLines = 15;`.
- `src/kapacitor/Commands/HistoryCommand.cs:49` — `HandleHistory(..., int minLines = 10, ...)` → `int minLines = 15`.

The `TooShort` classification in Phase 1 uses whichever value is passed in; the change is only to the CLI-level default.

## Context

Today `HistoryCommand.HandleHistory` is a single serial loop that mixes three concerns per iteration: probe the server's `last-line` API, decide what to do with the session (new / partial / skip), and then do it. The resulting UX has two problems:

- **Noise on re-runs.** A 1.5K-session re-run prints ~1.4K `Skipping {sid} [already loaded]` lines. Anything interesting — errors, newly-imported sessions — is drowned in it.
- **No upfront shape.** The pinned `Importing {done}/{total}` footer's denominator is the raw transcript count, not the count of sessions that actually need work. On re-runs it flies from 0 to 100% in seconds while doing almost nothing.

Serial imports also leave bandwidth on the table for fresh imports: each session does ~hundreds of batch POSTs and the next session waits on the previous one's final `session-end` hook.

## Design

### Phase layout

```
Rule: Discovering
  spinner: "Scanning ~/.claude/projects..."
  progress bar: "Probing {done}/{total} sessions"

Rule: Plan
  grid:
    New               N
    Resumable         N
    Already loaded    N
    Too short         N
    Excluded          N
    Probe errors      N

[optional: excluded-repo prompts — one per unique repo]

Rule: Importing {toImport} sessions
  top-level bar: "Importing {done}/{toImport} sessions"
  streamed: "Loading {sid}... {N} lines [new]"
  streamed: "Loading {sid}... {N} lines [resuming from line {R}]"
  streamed: "  ↳ imported subagent {aid} ({N} lines)"
  streamed (errors): "Skipping {sid} [{reason}]"

Rule: Titles & summaries (only if backgroundTasks > 0)
  two bars as today; failures stream

Rule: Done
  grid:
    Loaded              N
    Resumed             N
    Already loaded      N
    Too short           N
    Excluded            N
    Errored             N  (only row rendered red when > 0)
    Titles              {G} generated, {S} skipped, {F} failed   (if background phase ran)
    Summaries           {G} generated, {F} failed                 (if any summaries requested)
```

The `Importing` bar's denominator is `|New| + |Partial|`, the count of sessions that actually need work. On a re-run with nothing to import, Phase 2 is skipped entirely and we jump to `Done`.

### Phase 1 — Classify

Inputs: the transcript discovery step that exists today (enumerate `~/.claude/projects/*/*.jsonl`, dedup by resolved path, apply `--session` / `--cwd` filters).

For each transcript:

1. Extract metadata once (`ExtractSessionMetadata`) — cached on the classification record.
2. Check `TitleGenerator.IsKapacitorSubSession` — kapacitor-spawned sub-sessions are classified as `InternalSubSession`. This category is counted silently and never surfaced in the Plan or Done grids (equivalent to today's invisible skip).
3. Run the `last-line` probe. Probes execute concurrently via `SemaphoreSlim(8)`. Map response:
   - `404` → `New`.
   - `204` → `AlreadyLoaded`.
   - `200` with a `last_line_number` field → `Partial` (carry `ResumeFromLine = lastLine + 1`).
   - `200` without `last_line_number` → `AlreadyLoaded`.
   - Other status / `HttpRequestException` → `ProbeError` (carry a short reason string).
4. Apply the `TooShort` filter only for sessions classified `New` or `Partial`. Use a bounded `CountLinesUpTo(path, minLines)` that early-exits once `minLines` lines are observed; if fewer, reclassify as `TooShort`. Running the probe before the line count means `AlreadyLoaded` / `ProbeError` sessions never trigger a transcript scan, saving substantial I/O on re-runs.
5. If the session's cwd (resolved as `meta.Cwd ?? DecodeCwdFromDirName(encodedCwd)`, matching today) maps to an excluded repo via `RepositoryDetection.DetectRepositoryAsync` → mark `PendingExcluded` with the `{Owner}/{RepoName}` key. This applies only to sessions still classified as `New` or `Partial` after the TooShort check; other statuses are left alone (matching today, where the exclusion prompt only fires on the importable path).

After all probes complete:

6. Resolve `PendingExcluded` sessions:
   - Group by `{Owner}/{RepoName}`.
   - In TTY mode: prompt once per repo — "Repository `foo/bar` is excluded. Include {count} sessions from it? (y/N)". Yes → sessions retain their `New` / `Partial` status. No → sessions become `Excluded`.
   - In non-TTY mode: all `PendingExcluded` sessions become `Excluded` (today's auto-skip behaviour).

Classification output is a list of records:

```csharp
record SessionClassification(
    string SessionId,
    string FilePath,
    string EncodedCwd,
    SessionMetadata Meta,
    ClassificationStatus Status,
    int ResumeFromLine,              // only for Partial
    string? ProbeErrorReason,        // only for ProbeError
    string? PreviousSessionId        // from continuation map
);

enum ClassificationStatus {
    New, Partial, AlreadyLoaded, TooShort, Excluded, ProbeError, InternalSubSession
}
```

The continuation map (`BuildContinuationMap`) is built from the cached metadata — same algorithm as today, just fed from the classification list instead of re-reading transcripts.

### Phase 2 — Import in parallel

Input: the sublist with `Status ∈ { New, Partial }`.

Chains are built from the continuation map: each chain is an ordered list of sessions sharing a slug, earliest first. Sessions without a slug become singleton chains. Chain ordering is stable (slug string, then session id) so re-runs of partial imports resume the same order.

Chains are dispatched to a worker pool via `SemaphoreSlim(4)`. Each worker processes one chain start-to-finish **serially**:

- For each session in the chain:
  - `New` → emit `session-start` hook (with `previous_session_id` populated from the continuation map when present), import transcript + subagents, emit `session-end` hook, enqueue background title/summary tasks.
  - `Partial` → resume from `ResumeFromLine` without hooks (today's behaviour).

Serial-within-chain is required: a continuation's `session-start` hook references its predecessor's session id, which should already exist on the server. Because chain order is by timestamp and chains are processed head-first, a predecessor in the *same* classification is always imported (or was already on the server as `AlreadyLoaded`) before its successor within the chain. Slugs are the only cross-session link — continuations never reference a session in a different chain.

**Edge case (unchanged from today):** if a predecessor classified as `ProbeError` is skipped but its successor is `New`, the successor's `session-start` hook still carries `previous_session_id` pointing at the un-imported predecessor. Today's serial loop has the same behaviour; the server accepts the reference and the chain link is simply dangling. This is not addressed here.

Failures inside a chain (HTTP errors on `session-start`, probes that race, import exceptions) increment `errored` and stream a `Skipping {sid} [{reason}]` line, then the chain-worker moves to the next session in its chain (today's behaviour applied per-chain).

#### Progress reporting

One pinned top-level task: `Importing {done}/{toImport} sessions`, `MaxValue = |New|+|Partial|`.

`IProgress<ImportProgress>` events fire per chain-worker. The consumer:

- `BatchFlushed { AgentId: null }` — no footer mutation (per-session line counts are out for parallel).
- `SubagentStarted` — no footer mutation.
- `SubagentFinished` — stream `  ↳ imported subagent {aid} ({N} lines)`.

Session completion increments the top-level bar (`task.Increment(1)`) and streams `Loading {sid}... {N} lines [new|resuming from line R]`. Counter increments use `Interlocked.Increment` on shared `loaded`/`resumed`/`errored` ints.

Streamed line ordering is "as sessions finish". Lines from different sessions may interleave (one session's subagent completion may print between two other sessions' top-line completions). This is expected for parallel work and not worth serializing through a channel.

### Phase 3 — Titles & summaries (background)

Unchanged behaviour. Wrapped in the same `Rule("[dim]── Titles & summaries ──[/]")` header. The two-progress-bars + streamed failures display from DEV-1573 stays.

Note that background tasks are enqueued during Phase 2 and run concurrently with imports via the existing `SemaphoreSlim(3)`. This is unchanged — they already overlap imports today. Phase 3 is the *wait* for those tasks, not their start.

### Phase 4 — Done (final summary)

Replace today's single line (`Done: N loaded, N resumed, N skipped[, N errored]`) with a Spectre `Grid`:

```csharp
var grid = new Grid().AddColumn().AddColumn();
grid.AddRow("[bold]Loaded[/]",         $"{loaded}");
grid.AddRow("[bold]Resumed[/]",        $"{resumed}");
grid.AddRow("[bold]Already loaded[/]", $"{alreadyLoaded}");
if (tooShort > 0)   grid.AddRow("[bold]Too short[/]",     $"{tooShort}");
if (excluded > 0)   grid.AddRow("[bold]Excluded[/]",      $"{excluded}");
if (probeErrored > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{probeErrored}[/]");
if (errored > 0)    grid.AddRow("[bold]Errored[/]",       $"[red]{errored}[/]");
if (ranBackground) {
    grid.AddRow("[bold]Titles[/]",    $"{titlesGenerated} generated, {titlesSkipped} skipped, {titlesFailed} failed");
    if (summaryTaskCount > 0)
        grid.AddRow("[bold]Summaries[/]", $"{summariesGenerated} generated, {summariesFailed} failed");
}
AnsiConsole.Write(new Rule("[green]Done[/]").LeftJustified());
AnsiConsole.Write(grid);
```

Rows with zero counts in optional categories (`TooShort`, `Excluded`, `ProbeError`, `Errored`) are skipped so the grid stays compact. `Loaded` / `Resumed` / `Already loaded` always render, even when zero, so users can confirm the re-run saw the sessions.

### Non-TTY fallback

Same pipeline, different renderer:

- Phase 1: print `Probing {N} sessions...` (one line), run probes, print the Plan grid as plain text (labels + right-aligned counts).
- Phase 2: skip Spectre `Progress`; stream the same completion lines (`Loading {sid}... N lines [new]`, `  ↳ imported subagent ...`) via `Console.WriteLine`.
- Phase 3: print `Waiting for {N} background tasks...`; failures stream as plain lines.
- Phase 4: print a plain-text Done summary — one `Key: Value` per non-zero row.

Parallelism (8 probes, 4 chain-workers) applies in both modes. CI runs still benefit.

Excluded-repo prompts: non-TTY branch auto-skips (equivalent to today; no interactive prompt fires).

## Code layout

Split `HistoryCommand.HandleHistory` into four focused helpers. The file is already the largest command in the CLI; this is a good time to split it.

- `HistoryCommand.HandleHistory` — orchestration (discovery → classify → import → background → summary). ~60 lines.
- `HistoryCommand.DiscoverTranscriptsAsync` — today's discovery block as-is.
- `HistoryCommand.ClassifyAsync(httpClient, baseUrl, transcripts, minLines, excludedRepos, isTty, display)` — runs probes with `SemaphoreSlim(8)`, returns `List<SessionClassification>` (plus the resolved excluded-repo decisions). ~100 lines.
- `HistoryCommand.ImportChainsAsync(httpClient, baseUrl, classifications, continuationMap, display)` — groups into chains, dispatches to 4-worker pool, streams progress. ~100 lines.
- `HistoryCommand.RunBackgroundPhase(backgroundTasks, counters, display)` — existing block, wrapped in a `Rule`.
- `HistoryCommand.RenderFinalSummary(counters, display)` — grid in TTY, plain lines in non-TTY.

`HistoryDisplay` gains methods for:

- `BeginPhase(string title)` — emits a `Rule` (TTY) or a `===` heading (non-TTY).
- `WritePlanGrid(ClassificationCounts counts)`.
- `WriteDoneGrid(FinalCounters counters)`.

No changes to `SessionImporter.cs`, `ImportProgress.cs`, `TitleGenerator.cs`, `WhatsDoneCommand.cs`.

## Concurrency

- **Probe concurrency: 8.** Idempotent HTTP GETs; 8 is a conservative number that collapses a 1500-session probe from ~75s (50ms each serial) to ~10s.
- **Chain-worker concurrency: 4.** Each worker can post dozens-to-hundreds of batch POSTs for a big session. 4 chains in flight × typical 100-line batches is a reasonable load on the server without requiring server-side tuning.
- **Background title/summary concurrency: 3** (unchanged from today, via the existing `SemaphoreSlim(3)`).

All counters shared across chain-workers use `Interlocked.Increment` or `ConcurrentBag` (titles/summary failures are already bags). No `lock` statements needed.

Values are hardcoded for v1. If real-world use surfaces a need, a `--parallelism` flag (applies to chain-workers) can be added without re-architecting.

## Ordering guarantees

- **Within a chain**: strict serial order by timestamp. A continuation is never imported before its predecessor.
- **Across chains**: no guarantees. A shorter chain may finish before a longer one that started first. Streamed completion lines appear in the order sessions finish, not the order they start.
- **Subagent lines vs parent-session line**: a subagent line (`  ↳ imported subagent ...`) for session X always appears before session X's `Loading {x}... {N} lines [new]` line (today's behaviour is preserved inside a chain-worker). Subagent lines from session X may interleave with unrelated completion lines from sessions Y, Z running on other workers.
- **Output thread-safety**: streamed lines go through Spectre's `AnsiConsole.MarkupLine` (TTY) or `Console.WriteLine` (non-TTY). Both are thread-safe under concurrent writers. No extra serialization layer is required.

## AOT / trim safety

Per `CLAUDE.md`, after the change:

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

must produce no output. No new reflection-heavy Spectre APIs are introduced — only `Grid`, `Rule`, `Progress`, `Status`, `MarkupLine`, all already used by DEV-1573.

## Testing

Existing tests cover file-I/O helpers (`ExtractSessionMetadata`, `BuildContinuationMap`, `SortByContinuationOrder`, `ExtractLastTimestamp`) and pass unchanged.

New tests:

- `ClassificationTests`: given a set of mocked `last-line` responses (via WireMock.Net, consistent with the existing integration suite), the classification produces the expected `SessionClassification` records and counts. Covers all 6 status branches including `PendingExcluded → Excluded` via repo exclusion.
- `ChainSchedulingTests`: given a classification list + continuation map, the chain grouping produces ordered chains and singletons as expected (pure function — no HTTP).

Manual verification:

- `kapacitor history --min-lines 10` on a directory with mixed new / already-loaded / short / errored sessions — confirm plan grid matches actual counts, import bar denominator is |New|+|Partial|, parallel imports show multiple sessions completing out-of-order, final grid includes only non-zero optional rows.
- `kapacitor history --min-lines 10 > out.log` — confirm non-TTY path emits plain lines and no Spectre artifacts; still runs parallel.
- `kapacitor history --min-lines 10 --session {sid}` with a single session — confirm pipeline degenerates gracefully (1-session plan, 1-worker import, full summary).
- `kapacitor history --min-lines 10 --generate-summaries` — confirm background phase still renders and interleaves with the main import.
- Re-run a just-completed import — confirm no `Skipping ... already loaded` spam; all 1.5K sessions tallied in the `Already loaded` row of the plan + done grids.
- An import with an excluded repo in TTY — confirm the per-repo consent prompt fires once, not per session, and sessions are redirected accordingly.

## Out of scope (explicit)

- `--parallelism` / `--probe-concurrency` flags.
- Live per-session line-progress for in-flight sessions (K-row active panel). If we miss it, add later.
- Cancellation on Ctrl-C with graceful partial-summary render. Today's behaviour (abort mid-operation, no summary) is preserved.
- Batching the `last-line` probe server-side into a single bulk call. Out of scope for a CLI-only change.
