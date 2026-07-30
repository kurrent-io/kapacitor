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

**RESOLVED by review — `clear` MUST re-inject; `resume` must not.** My original proposal (let the lease
suppress both, matching Claude) was wrong, and the reason is decisive: `clear` *removes the model context
that held the injection*. Suppressing it means "once per session" stops achieving the stated goal — the
index is simply unavailable for the rest of that session, silently. That is a feature gap dressed up as
idempotence.

The two cases are genuinely different:

| Source | Model context | Correct behaviour |
|---|---|---|
| `resume` | earlier injection still in context (same transcript continues) | **suppress** — re-injecting duplicates |
| `clear` | earlier injection destroyed | **re-inject** — otherwise the feature is gone |

**Lease rule: the lease key gains a context generation.** A session-id-only key cannot express this. Key
the lease on `(sessionId, generation)` where the generation increments on each `clear`, so:

* `startup` → generation 0, injects.
* `resume` → generation unchanged, lease held, suppressed.
* `clear` → generation +1, fresh lease, injects.
* second `clear` → generation +2, injects again.

The generation must be **durable** alongside the lease (a hook process is short-lived and cannot hold it
in memory) and must not be inferable from the transcript, since `clear` does not start a new one. The
simplest durable form is a per-session counter in the lease store incremented on a `clear`-sourced
`SessionStart`.

**Acceptance coverage is required for the sequences, not just the states:** `startup → resume` (exactly
one output), `startup → clear` (a second output), `clear → clear` (a third). A test that only checks
"clear injects" would pass with a broken generation counter that always injects.

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

### 3.1 Contract — now MEASURED from Gemini's own hook runner, not assumed

The review correctly refused to accept this on field-name hit counts. It has since been read out of the
installed `@google/gemini-cli` bundle. Three facts, each load-bearing:

**(1) Blocking requires an explicit decision. A `hookSpecificOutput`-only payload cannot block.**

```js
isBlockingDecision() { return this.decision === "block" || this.decision === "deny" }
```

`decision` is `undefined` in our envelope, so this is `false`. The output object's constructor reads
`continue`, `stopReason`, `suppressOutput`, `systemMessage`, `decision`, `reason`, `hookSpecificOutput`
independently — absence of the decision fields is not a blocking state.

**(2) `additionalContext` is consumed through a string-typed accessor:**

```js
getAdditionalContext() {
  if (this.hookSpecificOutput && "additionalContext" in this.hookSpecificOutput) {
    const context = this.hookSpecificOutput["additionalContext"];
    if (typeof context !== "string") { … }
```

So the fragment must be a JSON **string**, which is what `HookMemoryOutput` already produces. A non-string
would be rejected rather than blocking.

**(3) Malformed or truncated stdout is fail-open, not blocking** — this is the answer to the review's
residual-risk demand in §3.3:

```js
const textToParse = stdout.trim() || stderr.trim();
try   { let parsed = JSON.parse(textToParse); if (typeof parsed === "string") parsed = JSON.parse(parsed); … }
catch { output = this.convertPlainTextToHookOutput(…, exitCode || EXIT_CODE_SUCCESS); }
```

A parse failure degrades to a plain-text hook output. It does **not** synthesise a block. So a partial
write cannot block the session — it can only produce a junk context string.

### 3.1a NEW HAZARD found while verifying: empty stdout falls back to STDERR

Look again at the first line above:

```js
const textToParse = stdout.trim() || stderr.trim();
```

**When stdout is empty, Gemini parses the hook's STDERR as its output.** kcap writes diagnostics to
stderr — `AgentHookPoster` logs there on a failed POST, and the auth-lapse notice is a stderr write.

Consequences, and they invert part of the original design intuition:

* "Emit nothing to stdout" is **not inert** for Gemini whenever stderr is non-empty. This is
  *pre-existing* behaviour — today's dispatcher never writes stdout, so any stderr diagnostic is already
  being parsed as hook output — but it was not previously understood, and it means the fail-open path is
  noisier than assumed.
* It makes writing the envelope on the failed-POST path **protective rather than merely harmless**: a
  populated stdout wins the `||`, so it *prevents* our stderr diagnostic from being interpreted as the
  hook's decision channel.
* Since a parse failure only yields plain text (fact 3), the pre-existing behaviour is not a live bug.
  It is recorded here because it is the kind of thing that becomes a bug the moment someone adds a
  `decision`-shaped word to a stderr message.

**This does not need fixing in this issue**, but it must not be silently discovered again — noted in §7.

### 3.2 Fail-open — corrected: "zero bytes on any failure" was too strong

The review was right that the original claim could not hold. Rendering to a string first covers
*serialization* failure, but `writer.Write(payload)` can throw **after a partial write**, and the shown
`try` did not cover the write at all. Corrected contract:

1. **Render completely, then perform exactly one write.** No incremental/streaming construction, so
   there is one failure point rather than many.
2. **A write exception must not change the command's pre-existing exit code.** Catch it, swallow it
   (non-fatal), and return whatever the POST path would have returned.
3. **A partial write is a bounded, non-blocking risk, not an unbounded one** — established by §3.1
   fact (3): truncated JSON fails `JSON.parse` and degrades to plain text. The worst case is a junk
   context string, never a block. This is the residual risk the review asked to have verified or
   documented; it is now verified.
4. **Do not claim atomicity we do not have.** A single `Write` of a small payload to a pipe is not
   guaranteed atomic, and application code cannot retract bytes already written. The mitigation is (3),
   not a stronger write.

```csharp
static void WriteSessionStartOutput(TextWriter writer, string? fragment) {
    string payload;
    try { payload = SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Gemini, fragment); }
    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return; }

    // Separate try: a write that throws mid-payload must not alter the exit code. Truncated JSON is
    // fail-open on Gemini's side (§3.1 fact 3), so there is nothing to repair here.
    try { writer.Write(payload); }
    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
}
```

`Render` returns `""` for a null fragment, so opt-out / budget-exhausted / lease-held all produce zero
bytes naturally.

**Tests required by this section:** a writer that throws *before* writing anything, and a writer that
throws *after* a partial write — both asserting the exit code is unchanged.

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

**RESOLVED by measurement — option (a), no commit gate.** The review was right to call this blocking: my
recommendation rested on a behaviour the spec never established. It has now been read from Gemini's hook
runner:

```js
child.on("close", (exitCode) => {
    const textToParse = stdout.trim() || stderr.trim();     // ← no exit-code gate
    try { let parsed = JSON.parse(textToParse); … } catch { … }
    return { success: exitCode === EXIT_CODE_SUCCESS, stdout, exitCode: exitCode || EXIT_CODE_SUCCESS, … };
});
```

**Gemini parses hook stdout unconditionally.** The exit code is recorded as `success` but does not gate
parsing. So stdout on a non-zero exit is *not* discarded — which removes the entire premise for a commit
gate, since the lease is not burned for an undelivered injection.

Two supporting reasons this is right rather than merely permitted:

* The review's own objection to (b) is decisive against it: *"the spec must not rely on a later `resume`:
  a startup hook failure may prevent the session from reaching a resumable state."* Option (b) depended
  on that retry existing; option (a) depends on nothing.
* Per §3.1a, writing stdout on the failed-POST path is actively **protective** — a populated stdout wins
  the `stdout || stderr` selection and stops our stderr diagnostic being parsed as the hook's output.

**Residual gap, stated honestly:** this establishes stdout is *parsed* on a non-zero exit. Whether the
consuming layer *additionally* gates on `success` before applying `getAdditionalContext()` was not traced
to a definitive call site. It does not change the decision — (b)'s rationale is void either way — but the
live cert must include a non-zero-exit case (§5) so the end-to-end answer is measured, not inferred.

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
   reusing `MemoryIndexLiveCertHarness`. `[NotInParallel]`.

   * **Positive:** a nonce saved as a memory is reproduced by a real `gemini` turn — *and the turn
     completes successfully*. Nonce reproduction alone is insufficient: the review noted the harness could
     mask a failed invocation, so the cert must assert turn completion (exit status + no
     blocking/stop decision), which is what actually verifies §3.1.
   * **Negative control:** with `disable_memory_index` set, the nonce does **not** appear.
   * **Non-zero-exit case** (new, from §3.3's residual gap): drive a `SessionStart` whose POST fails so
     the hook exits non-zero while emitting recognisable `additionalContext`, then assert the session
     continues **and** record whether the model received the context. This is the only way to settle
     whether the consuming layer gates on `success`.

   **Cleanup and isolation are explicit requirements, not `[NotInParallel]` side effects.** The review is
   right that `[NotInParallel]` only prevents concurrency — it restores nothing. The existing
   `MemoryIndexLiveCertHarness` already provides `ReadDisableMemoryIndexAsync` /
   `SetDisableMemoryIndexAsync` / `RestoreDisableMemoryIndexAsync`, and the Kiro cert already nests the
   restore inside a `finally` *inside* the archive `finally` (so a throwing restore cannot skip the
   memory archive). Reuse that shape exactly, and additionally:

   * snapshot **before** anything is created, and restore in `finally` — **including restoring the unset
     state**, not just `false`;
   * assert the **positive** control runs with opt-out *disabled*, so a leaked `true` from an earlier
     failed run cannot make the positive test pass vacuously;
   * archive the nonce memory unconditionally — a leaked nonce corrupts every later cert's index. (The
     Kiro cert work leaked 13 memories exactly this way; `archive_memory` takes `id`, not `memory_id`.)

### 5.2 Supported Gemini version boundary

The review is right that this is unspecified and that changing zero-stdout → decision-payload affects any
installed version running the hook.

* **Verified against `gemini 0.53.0`** — every §3.1 fact is read from that bundle. Facts about
  `isBlockingDecision`, the unconditional parse, and the `stdout || stderr` fallback are version-specific
  observations, not guarantees.
* **Minimum supported: 0.53.0.** Below it, the hook-output contract is unverified.
* **The live cert must record and assert the exact binary version** (`gemini --version`), mirroring
  `RecordCertEnvironmentAsync` in the existing harness — a cert that passes against an unknown build tells
  us nothing, which is precisely how the AI-1592 memory-cert failures were misdiagnosed (a stale installed
  binary, not a code defect).
* **On an older/unknown version: still emit.** Suppressing would silently disable the feature, and the
  measured fail-open behaviour (§3.1 fact 3 — malformed or unrecognised stdout degrades to plain text
  rather than blocking) means a wrong guess is not dangerous. Do **not** add a version gate that declines
  installation; that trades a benign degradation for a silent feature loss.

**Every guard assertion must be mutation-proven.** The Kiro work shipped a vacuous guard test that
passed with the guard removed; the standard here is that deleting the guard fails exactly the intended
test.

### 5.1 The cert is load-bearing for §3

The live cert is the only thing that verifies §3.1 — that a `hookSpecificOutput`-only payload does not
disturb Gemini's decision channel. A green unit suite proves the bytes we emit, not that Gemini accepts
them. If the cert cannot be run, this issue should not be called done.

## 6. Follow-ups this work surfaces but does not fix

* **`stdout.trim() || stderr.trim()` (§3.1a).** Gemini parses a hook's **stderr** as its output whenever
  stdout is empty. kcap writes diagnostics to stderr, so this already happens today on every failed-POST
  session across the Gemini hook. It is currently benign — a parse failure degrades to plain text — but it
  is one carelessly-worded stderr message away from being a real bug, and it means kcap's stderr is
  semantically part of Gemini's hook contract. Worth its own issue rather than a comment.

## 7. Out of scope

* Gemini hosted agents (AI-899) and the Gemini reviewer (AI-1413) — separate issues, separate epics.
* `SessionEnd` / `Notification` behaviour — untouched.
* Subagent memory injection: Gemini fires no subagent-start hook (`GeminiSubagentTeardown` exists
  precisely because the parent owns teardown), so there is no per-subagent injection point to wire.
