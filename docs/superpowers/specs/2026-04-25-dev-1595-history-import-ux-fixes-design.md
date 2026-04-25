# DEV-1595 — History import UX fixes

## Background

The DEV-1579 history import rewrite (`HistoryCommand.cs`, commit `7936ef8`)
parallelised the import path and replaced the previous live "footer"
display with per-session log lines. Three regressions surfaced in real
runs (Linear DEV-1595, screenshots in the issue):

1. **Probing stuck at 0%.** The "Probing N sessions" progress bar sits at
   zero through the entire `ClassifyAsync` await, then jumps to 100%.
   The bar value is set after the await — there is no per-task increment.

2. **"Already loaded: 0" but resuming many sessions with 0 new lines.**
   A 200 OK from the `/api/sessions/{id}/last-line` probe is always
   classified as `Partial`, even when the local transcript has no lines
   beyond `last_line_number`. The plan grid under-reports
   `AlreadyLoaded`; the import phase then sends zero lines per such
   session, which is wasted work and misleading output.

3. **"Loading" lines scroll instead of staying live.** Each completed
   session prints a `✓ Loading <id>… N lines` line via
   `AnsiConsole.MarkupLine`. With 4 parallel workers and hundreds of
   sessions, the screen scrolls continuously. The previous
   single-line footer pattern (DEV-1573) was lost when the importer
   was parallelised.

This spec addresses all three issues. They are independent fixes in
the same file but warrant a single change because they share test
fixtures and ship together.

## Goals

- Probing bar advances smoothly from 0% to 100% as probes complete.
- Plan grid `AlreadyLoaded` accounts for sessions whose local
  transcript has no lines beyond the server's last-imported line.
- Importing phase shows up to 4 live worker slots that update in place
  instead of scrolling per-session log lines. Subagent activity is
  visible while it is happening.

## Non-goals

- No change to `MaxDegreeOfParallelism` (stays at 4).
- No change to the non-TTY pipe-friendly output (still scrolls).
- No change to the probe HTTP contract or the `last-line` endpoint.
- No change to background title/summary phase rendering.

## Design

### Issue 1 — Probing progress

`ClassifyAsync` gains an optional probe-completion callback:

```csharp
internal static Task<List<SessionClassification>> ClassifyAsync(
    HttpClient httpClient,
    string baseUrl,
    List<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
    int minLines,
    string[]? excludedRepos,
    CancellationToken ct,
    Action? onProbed = null);
```

Each `ClassifyOneAsync` invokes `onProbed?.Invoke()` exactly once after
its work finishes (probe + optional line count + excluded-repo check),
in a `try { … } finally { onProbed?.Invoke(); }` so the bar still
advances when a probe throws.

The TTY wrapper at `HistoryCommand.cs:222-231` passes
`() => bar.Increment(1)`; the non-TTY caller passes `null`. The
post-await `bar.Value = bar.MaxValue` line goes away — by the time the
await returns, every probe has already incremented.

Spectre's `ProgressTask` mutations are safe from arbitrary threads
under the active `Progress` context; we are not introducing any new
synchronisation requirement that the existing footer pattern did not
already meet.

### Issue 2 — Plan classification correctness

`ClassifyOneAsync` currently calls `CountLinesUpTo(filePath, minLines)`
only on `New | Partial` to apply the `TooShort` filter. We extend that
read to also cover the "no new lines beyond last imported" case:

```csharp
if (status is ClassificationStatus.New or ClassificationStatus.Partial) {
    var threshold = Math.Max(
        minLines,
        status == ClassificationStatus.Partial ? resumeFromLine + 1 : 0);
    var observedLines = CountLinesUpTo(filePath, threshold);

    if (minLines > 0 && observedLines < minLines) {
        // existing TooShort branch — return immediately
    }

    if (status == ClassificationStatus.Partial && observedLines <= resumeFromLine) {
        status = ClassificationStatus.AlreadyLoaded;
        resumeFromLine = 0;
    }
}
```

Order matters: the `TooShort` check still wins because a transcript
below `minLines` should be skipped regardless of what the server
thinks. The reclassification to `AlreadyLoaded` happens *before* the
excluded-repo flagging block, so reclassified sessions do not pull
the user into a "include excluded repo?" prompt for work that does
not exist.

`CountLinesUpTo` already early-exits at the threshold, so the read
cost is bounded by `Math.Max(minLines, resumeFromLine + 1)` lines per
session — usually a few KB.

`AlreadyLoaded` carries no `ResumeFromLine` semantics, so we clear it
back to `0` for tidiness; nothing reads it for that status, but the
record's invariant ("ResumeFromLine only meaningful when Partial")
stays clean.

### Issue 3 — Loading display

The `Importing` phase replaces its single bar + scrolling per-session
log lines with **one progress bar plus four live "slot" rows**, all
rendered inside a single `AnsiConsole.Progress(...).StartAsync(...)`.

**Rendering**

```
─ Importing 401 sessions ──────────────────────────────
Importing  ████████████░░░░░░░░░  60%
  Slot 1   Loading e71eb384… (resuming from line 34)
  Slot 2   Loading 3fe3d96e… (new)
            ↳ subagent aedf7cc3… (35 lines)
  Slot 3   Loading c280f03c… (resuming from line 16)
  Slot 4   idle
```

Implementation: alongside the main `bar`, the wrapper adds four
description-only `ProgressTask`s. Each slot task is created with
`IsIndeterminate = true` while the worker is processing a session
(stripe animation under `ProgressBarColumn`) and toggled to
`IsIndeterminate = false` with `Description = "idle"` when the
worker has nothing to do. The same `Progress` context renders the
main `Importing` bar and the four slot rows together, so the whole
phase is one cohesive live region.

**Worker dispatch**

`Parallel.ForEachAsync` does not expose a worker index. To put each
session on a stable slot row, we replace the current parallel loop
with a hand-rolled four-worker fan-out:

```csharp
var queue = Channel.CreateUnbounded<List<SessionClassification>>(
    new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
foreach (var chain in chains) await queue.Writer.WriteAsync(chain, ct);
queue.Writer.Complete();

var workers = Enumerable.Range(0, 4).Select(slot => Task.Run(async () => {
    while (await queue.Reader.WaitToReadAsync(ct)) {
        while (queue.Reader.TryRead(out var chain)) {
            foreach (var session in chain) {
                events.OnSessionStarted(slot, session);
                // existing per-session import logic …
                events.OnSessionEnded(slot, session, outcome);
            }
        }
    }
})).ToArray();

await Task.WhenAll(workers);
```

The `MaxDegreeOfParallelism = 4` guarantee is preserved (four
workers, no more, no fewer for the duration of the phase). Cancellation
flows through `ct` as before.

**Events**

`ChainWorkerEvents` shape:

```csharp
internal sealed record ChainWorkerEvents {
    // Slot-aware lifecycle (new):
    public required Action<int, SessionClassification> OnSessionStarted { get; init; }
    public required Action<int, string, string> OnSubagentStarted { get; init; }      // slot, sessionId, agentId
    public required Action<int, string, string, int> OnSubagentFinished { get; init; }// slot, sessionId, agentId, lines
    public required Action<int, SessionClassification, SessionImportOutcome, int> OnSessionEnded { get; init; }
    // Errors (replaces old OnSessionErrored streaming line):
    public required Action<int, string, string> OnSessionErrored { get; init; }       // slot, sessionId, reason

    // Background phase (renamed from the previous `OnSessionEnded` — that name
    // now belongs to the slot-aware lifecycle event above):
    public required Action<(string SessionId, string FilePath, string? PreviousSessionId)> OnTitleTaskReady { get; init; }
    public required Action<(string SessionId, bool GenerateWhatsDone)> OnBackgroundWorkReady { get; init; }
}
```

The rename of the background-trigger callback (`OnSessionEnded` →
`OnBackgroundWorkReady`) disambiguates the two roles the old field
had: signalling slot completion vs. enqueueing background title /
summary work. `HandleHistory` is the only caller; one rename.

The TTY wrapper:

- `OnSessionStarted(slot, c)` → `slots[slot].Description = "Loading <short> (<verb>)"`
  where verb is `new` or `resuming from line N`.
- `OnSubagentStarted(slot, agentId, sid)` → swap description to `"  ↳ subagent <short> …"`
  on the same slot row (it is the same worker doing the work — no
  extra row is needed).
- `OnSubagentFinished` → revert description back to the parent
  session's `Loading …` line.
- `OnSessionEnded` → increment `bar.Increment(1)`. Description stays
  on the last completed session until `OnSessionStarted` swaps it,
  giving the user a brief glance at what just finished.
- `OnSessionErrored(slot, sid, reason)` → write a single
  `AnsiConsole.MarkupLine($"[red]✗[/] Skipping {sid} [{reason}]")` *outside*
  the slot rows so errors persist in scrollback, then revert the slot
  description as if `OnSessionEnded`. Errors are rare and worth
  keeping; we accept a minor scroll for them.
- When a worker drains the channel, its slot description becomes
  `"idle"` and `IsIndeterminate = false` so the stripe animation
  stops.

The non-TTY wrapper keeps the existing `Console.WriteLine`
implementation: `OnSessionStarted` is a no-op, `OnSessionEnded`
prints the same `Loading <id>… N lines [new|resuming from line X]`
line, `OnSubagentFinished` prints the existing `↳ imported subagent`
line, `OnSessionErrored` prints `Skipping <id> […]`. Pipe-friendly
output is preserved.

**Concurrency notes**

- Slot description writes happen from worker threads inside an active
  `Progress` context. `ProgressTask` mutations under `Progress` are
  thread-safe — same guarantee the existing footer used.
- `bar.Increment(1)` is called once per `OnSessionEnded`; no double
  counting because the wrapper only runs in the TTY branch.

## Testing

### Unit-level

- `ClassifyAsync` with a probe callback: assert callback invoked
  exactly `transcripts.Count` times across mixed `New / Partial /
  AlreadyLoaded / TooShort / ProbeError / InternalSubSession` cases.
  Use the existing `WireMock.Net` setup.
- `ClassifyOneAsync` reclassification: server returns 200 with
  `last_line_number = 27` against a local transcript with exactly 28
  lines → status is `AlreadyLoaded`. Same probe against a 29-line
  transcript → `Partial` with `ResumeFromLine = 28`.
- `ClassifyOneAsync` excluded-repo interaction: a server-200/28-line
  fixture in an excluded repo classifies as `AlreadyLoaded` and does
  *not* set `ExcludedRepoKey`.

### Integration-level

- A real `Progress` context with a `TestConsole` (Spectre's testing
  helper): drive `ImportChainsAsync` with a synthetic chain set and
  capture the rendered output. Assertions:
  - Four slot rows exist while imports are in flight.
  - No `✓ Loading …` lines appear in the captured output (only
    `Importing` bar plus slot rows plus error lines).
  - Per-error rendering: a forced `OnSessionErrored` produces an
    `✗ Skipping …` line in scrollback.
- Non-TTY path: redirect stdout, verify the legacy
  `Loading <id>… N lines […]` and `↳ imported subagent …` lines are
  still emitted line by line.

### Manual smoke

- Run `kapacitor history` on the developer's own
  `~/.claude/projects` (the screenshot's 2261 sessions / 401 imports
  is a representative sample) and visually confirm:
  - Probing bar advances smoothly.
  - Plan grid no longer reports many "Resumable" entries that import
    zero lines (the "Already loaded" count should jump to roughly the
    previous "Resumable, 0 lines" tally).
  - Slot rows update in place; no per-session scroll except for
    errors.
- Pipe to a file (`kapacitor history > out.txt`) and verify legacy
  output still works.

## AOT considerations

`System.Threading.Channels` is AOT-safe and already in use elsewhere
in the project. Spectre.Console's `Progress` API is AOT-safe under
the existing trim flags (no reflection-based formatting). No new
analyzer warnings are expected, but `dotnet publish -c Release` must
be re-run to confirm no IL3050 / IL2026 surfaces — per
`CLAUDE.md`, AOT warnings only show on publish.

## Rollout

Single PR titled `DEV-1595 CLI: history import UX fixes`. Three
commits, one per issue, in the order:

1. Probe progress callback (Issue 1).
2. Reclassify Partial-with-no-new-lines as AlreadyLoaded (Issue 2).
3. Worker-slot live display (Issue 3).

Each commit ships its tests. The third commit touches the most code
and merges last so the first two can be cherry-picked if needed.

## Risks

- **Worker-slot rendering correctness across terminals.** Spectre's
  `Progress` with multiple `IsIndeterminate` tasks renders well in
  iTerm2 and Apple Terminal. Windows Terminal / Conhost have known
  oddities with stripe animations. Acceptable: stripe animations
  fall back to a static block; legibility is preserved.
- **Channel-based dispatch vs. `Parallel.ForEachAsync` at scale.**
  The existing parallel loop uses internal pacing optimisations.
  Four hand-rolled workers reading from an unbounded channel will
  start all four immediately and idle as the queue drains; this is
  what we want for live display, and the per-chain overhead is
  negligible relative to HTTP I/O.
- **`OnSessionEnded` description latency.** A worker that finishes a
  session and then waits for the channel will leave its slot
  description on the just-finished session for a tick. That is a
  feature, not a bug — it gives the user time to see the last
  completed work.
