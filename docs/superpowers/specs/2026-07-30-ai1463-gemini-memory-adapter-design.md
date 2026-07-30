# AI-1463 — SessionStart memory index: Gemini CLI adapter

**Status:** specced 2026-07-30 against `gemini 0.53.0` and `origin/main` (`694d32b`).
**Repository:** kurrent-io/kcap-cli
**Parent:** AI-1456 (SessionStart memory-index injection across all coding harnesses)
**Prior art (all merged):** Claude (AI-1460, the baseline) · Cursor (AI-1461) · Codex (AI-1459) ·
Copilot (AI-1462) · Kiro (AI-1464) · live certs (AI-1591)

## What this is

Wire the sixth harness into the existing SessionStart memory-index foundation. The goal is unchanged
from the five merged adapters: at session start, fetch `GET /api/memories/index` in parallel with the
lifecycle hook POST, inject a grouped org/team/user `## Team memory` index into the model's context,
budget-bounded and fail-open, once per session.

**Unusually little of this is new work** — see §1. The bulk of the risk is in one place, §3.

## 1. What already exists (verified in code, not assumed)

Gemini is **already represented in the foundation**, from the original Claude-baseline work:

```csharp
// SessionStartMemoryContracts.cs
enum SessionStartHarness { …, Gemini, … }

// SessionStartMemoryOutputAdapters.cs
SessionStartHarness.Gemini => new GeminiMemoryEnvelope(
    fragment is null ? null : new HookMemoryOutput("SessionStart", fragment)),

// SessionStartMemoryJsonContext.cs  (AOT-safe, source-generated)
[JsonSerializable(typeof(GeminiMemoryEnvelope))]
internal sealed record GeminiMemoryEnvelope(
    [property: JsonPropertyName("hookSpecificOutput")] HookMemoryOutput? HookSpecificOutput = null);

// SessionStartMemoryIdentity.cs
SessionStartHarness.Gemini => "gemini",
```

So the envelope, its AOT serialization context, and the identity mapping are done. **What is missing is
only the call site:** `GeminiHookCommand` never invokes the orchestrator. Grepping the commands
directory, only `ClaudeHookCommand`, `CursorHookCommand`, `CodexHookCommand`, `CopilotHookCommand` and
`KiroHookCommand` do.

### 1.1 The envelope shape is CORRECT — verified against Gemini's own code

This mattered enough to check rather than trust, because the envelope was written speculatively before
any adapter consumed it. A static scan of the installed `@google/gemini-cli` package:

| Field we emit | Hits in Gemini's own JS |
|---|---|
| `hookSpecificOutput` | 150 |
| `additionalContext` | 96 |
| `hookEventName` | 15 |

Gemini genuinely parses the Claude-shaped envelope. **No new wire format is being invented.**

### 1.2 Gemini's hook model (from `GeminiHookCommand` + the CLI)

* Uniform PascalCase `hook_event_name`: `SessionStart` / `SessionEnd` / `Notification`, self-routed by
  one `kcap hook --gemini` registration per event.
* `session_id` is a dashed UUID; the dispatcher stores the dashless form (shared convention).
* `source` ∈ `startup` (default) / `resume` / `clear`. **Gemini re-fires `SessionStart` with
  `source: "resume"` on the SAME session id**, appending to the same transcript.
* **stdout is a JSON decision channel.** Today's dispatcher deliberately writes **nothing**, and its
  doc comment says so: *"Gemini treats hook stdout as a JSON decision channel; this dispatcher emits
  nothing (no stdout) so every action is allowed."*

## 2. Design

Mirror the merged adapters. In `HandleSessionStart`:

1. Start the memory fetch **in parallel** with the existing POST, not before it — the POST is the
   latency-critical path.
2. Bound it with `HookBudget.Remaining(processStart, "session-start")`.
3. Write the rendered envelope to stdout.
4. Preserve every existing side effect and exit code (POST, watcher spawn, disabled/excluded fast paths).

### 2.1 Thread `hookProcessStart` — currently absent for Gemini

`Program.cs` passes `hookProcessStart` to Claude, Codex, Copilot and Kiro, but Gemini's dispatch is
`GeminiHookCommand.Handle(baseUrl!, Console.In)` — no timestamp. Without it there is no budget origin
and `HookBudget.Remaining` cannot be computed.

This is the **same omission that had to be fixed for Codex, Copilot and Kiro**, so it is a known trap
rather than a discovery: add the parameter and thread it.

**Do not subtract `Safety` at the call site.** `HookBudget.Remaining` already subtracts it
(`Ceiling - elapsed - Safety`, clamped ≥ 0). Double-subtracting was a real defect in the Copilot work,
fixed at four sites.

### 2.2 Lifecycle mapping — reuse Claude's, do not invent one

`ClaudeHookCommand` maps `source` → `SessionLifecycleReason` (`resume`→`Resume`, `reopen`→`Reopen`,
`fork`→`Fork`, `compact`→`Compact`), and `SessionStartMemoryLifecyclePolicy` suppresses injection when
`Reason == Compact` or `!IsTopLevel`.

Gemini emits only `startup` / `resume` / `clear`, so:

| Gemini `source` | `SessionLifecycleReason` | Inject? |
|---|---|---|
| `startup` | `Startup` | yes |
| `resume` | `Resume` | yes — but the lease dedupes (§2.3) |
| `clear` | `Clear` if it exists, else `Startup` | yes |

`CallbackMayRepeat: false`. Gemini's `SessionStart` is a session-level event, not a per-turn callback —
unlike Kiro's `agentSpawn`, which fires per prompt and needed `RepeatedTurnCallback`.

**Open for review:** whether `clear` should inject. It is a context reset within one session id, so the
model has lost the earlier injection and arguably *should* be re-injected — but the lease is keyed on
session id and will suppress it. Flagging rather than silently picking: the safe default is to let the
lease suppress, matching Claude's behaviour, and revisit only if a user reports a missing index after
`/clear`.

### 2.3 Re-injection on `resume` is suppressed by the lease, deliberately

Because `resume` re-fires on the same session id, `SessionStartMemoryLeaseStore` will already hold a
completed lease and return no fragment. That is the desired outcome: one injection per session.

Note the contrast with Kiro, and why it is not a contradiction. Kiro's `agentSpawn` fires per *prompt*
within one session, so the lease is what makes it once-per-session. Gemini's `SessionStart` fires per
*session-entry*, so the lease is what makes a resume idempotent. Same mechanism, different event
semantics.

### 2.4 Opt-out — the Codex/Copilot profile trap does NOT apply here

Both the Codex and Copilot adapters shipped a bug where the opt-out was ignored under `KCAP_URL`,
because `ProfileResolver.Resolve()` returns `Profile: null` when `--server-url`/`KCAP_URL` wins, and
only `GetActiveProfileAsync()` falls back to disk.

`GeminiHookCommand` **already** resolves `activeProfile` via `await AppConfig.GetActiveProfileAsync()`
and threads it into `HandleSessionStart`. So `activeProfile?.DisableMemoryIndex is true` is correct here
with no extra work. Stated explicitly so a reviewer does not have to re-derive it, and so nobody
"helpfully" swaps in the resolver later.

## 3. The one genuinely new risk: stdout is a decision channel

This is the part that differs from every previous adapter and deserves the review attention.

The five merged adapters write into channels that are either inert (Kiro: raw stdout, no envelope) or
already-decision-shaped and exercised (Claude/Cursor/Codex/Copilot). For Gemini we are changing a hook
that **currently emits nothing at all** into one that emits a JSON object — on a channel Gemini uses to
decide whether to *continue*.

Gemini's parsed decision fields (same static scan): `continue`, `decision`, `stopReason`,
`systemMessage`, `suppressOutput`.

### 3.1 Contract

Emitting `{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"…"}}` — with **no**
`continue`, `decision` or `stopReason` — must mean "allow, and here is extra context". That is Claude's
semantics for the identical envelope, and Gemini parses the identical field names.

**This must be verified live, not assumed.** A regression here does not degrade gracefully: it could
block or abort the user's Gemini session at startup. It is the reason §5's live cert is mandatory rather
than nice-to-have.

### 3.2 Fail-open, byte-identical to today

On **any** failure — null fragment, opt-out, budget exhausted, lease held, serialization error — write
**nothing**. Not `{}`, not an empty object: zero bytes, exactly today's behaviour.

`SessionStartMemoryOutputAdapters.Render` already returns `""` for a null fragment, so this falls out of
the existing contract, and the Kiro adapter's `WriteAgentSpawnOutput` already models the
catch-and-emit-nothing shape:

```csharp
try   { payload = SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Gemini, fragment); }
catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return; }
writer.Write(payload);
```

### 3.3 Ordering: write stdout BEFORE any non-zero return

`HandleSessionStart` currently ends:

```csharp
if (!AgentHookPoster.ShouldSpawnAfter(outcome)) return outcome == HookPostOutcome.Failed ? 1 : 0;
await EnsureWatcher(…);
return 0;
```

**This exact shape caused a real defect in the Codex adapter**, where an early `return 1` on a failed
POST skipped the stdout handshake entirely — fixed later with a mutation-tested integration regression.
Do not reintroduce it: the memory fragment must be written on every path that returns, including the
failed-POST path.

Rationale: the memory index is independent of lifecycle capture. A server that rejects the
`session-start` POST has not invalidated an index we already fetched, and the user should still get it.

**Open for review — and I want a second opinion.** Does Gemini read a hook's stdout when the hook exits
**non-zero**? If it discards it, writing before `return 1` is harmless but pointless, and the lease would
be burned for an injection the model never saw — which is what the Copilot adapter's `commitGate` was
built to prevent. Two candidate answers:

* **(a) Keep exit codes unchanged**, write stdout first, accept that a failed-POST session may burn its
  lease. Simplest, and a failed POST is already an error path the user sees on stderr.
* **(b) Add a commit gate** like Copilot's: only complete the lease when the output was plausibly
  delivered (i.e. we are returning 0), otherwise release it for retry on the next `resume`.

Recommendation: **(b)**, because Gemini's `resume` gives a natural, frequent retry opportunity that
Copilot did not have, so releasing the lease has a real chance of succeeding later. But it costs a
`commitGate` wire-up, so (a) is defensible if the reviewer disagrees.

## 4. Files

* `src/Capacitor.Cli/Commands/GeminiHookCommand.cs` — orchestrator call, stdout write, budget.
* `src/Capacitor.Cli/Program.cs` — thread `hookProcessStart` into the `--gemini` dispatch.
* No foundation changes expected (§1). If one proves necessary, that is a signal to re-check whether the
  envelope really was complete.

## 5. Test plan

Layers, per the project's per-vendor convention:

1. **Foundation** — `Gemini_*` cases in `SessionStartMemoryFoundationTests`: envelope rendering
   (fragment → exact JSON), null fragment → empty string, lease dedupe across two `SessionStart`
   invocations on one session id (the `resume` case).
2. **Hook command** — `GeminiSessionStartMemoryTests`: opt-out honoured (including under `KCAP_URL`, the
   §2.4 trap); budget exhaustion writes nothing; a throwing provider writes nothing and still returns the
   pre-existing exit code; stdout is written on the failed-POST path (§3.3).
3. **Integration** — WireMock 400 on `session-start/gemini` asserting the exit code AND parseable stdout,
   mirroring `CodexSessionStartHandshakeOnPostFailureTests`.
4. **Live cert** — `GeminiMemoryIndexLiveCertTests`, gated on `KCAP_GEMINI_MEMORY_LIVE=1` + `KCAP_URL`,
   reusing `MemoryIndexLiveCertHarness`. Positive: a nonce saved as a memory is reproduced by a real
   `gemini` turn. Negative control: with `disable_memory_index` set, the nonce does **not** appear.
   `[NotInParallel]` — the negative control mutates process-global config.

**Every guard assertion must be mutation-proven.** The Kiro work shipped a vacuous guard test that
passed with the guard removed; the standard here is that deleting the guard fails exactly the intended
test.

### 5.1 The cert is load-bearing for §3

The live cert is the only thing that verifies §3.1 — that a `hookSpecificOutput`-only payload does not
disturb Gemini's decision channel. A green unit suite proves the bytes we emit, not that Gemini accepts
them. If the cert cannot be run, this issue should not be called done.

## 6. Out of scope

* Gemini hosted agents (AI-899) and the Gemini reviewer (AI-1413) — separate issues, separate epics.
* `SessionEnd` / `Notification` behaviour — untouched.
* Subagent memory injection: Gemini fires no subagent-start hook (`GeminiSubagentTeardown` exists
  precisely because the parent owns teardown), so there is no per-subagent injection point to wire.
