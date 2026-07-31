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

Gemini emits only `startup` / `resume` / `clear`.

`CallbackMayRepeat: false`. Gemini's `SessionStart` is a session-level event, not a per-turn callback —
unlike Kiro's `agentSpawn`, which fires per prompt and needed `RepeatedTurnCallback`.

**The review was right that suppressing `clear` is a silent feature loss — and following that finding to
its conclusion moved it OUT of this issue. Split to AI-1617.**

The finding stands: `clear` destroys the model context that held the injection, so a session-id-keyed
lease means the index is silently unavailable for the rest of that session. That is a real bug.

But it is **not a Gemini bug**, and the fix does not belong in adapter #6. Verified:

* `ClaudeHookCommand` maps `resume`/`reopen`/`fork`/`compact` and lets everything else — **including
  `clear`** — fall to `_ => SessionLifecycleReason.New`.
* There is **no `Clear` reason in `SessionStartMemoryContracts` at all.**

So Claude has the identical silent loss today, and the mechanism required to fix it (a durable atomic
context generation in the *shared* lease store, a stable delivery id to dedupe duplicate `clear`
callbacks, and a persisted-key migration so five already-merged adapters don't start re-injecting on
upgrade) is a **foundation feature affecting every harness**. Building it here would invent a foundation
mechanism under delivery pressure, put five shipped adapters at migration risk, and fix one harness while
five keep the bug.

**Decision for AI-1463: `clear` behaves exactly as it does for the other five adapters — the lease
suppresses it.** This is a *documented known gap*, not a claim that it is correct. AI-1617 owns fixing it
everywhere at once.

| Gemini `source` | `SessionLifecycleReason` | Injects? |
|---|---|---|
| `startup` | `New` | yes |
| `resume` | `Resume` | no — lease held |
| `clear` | `New` (no `Clear` reason exists) | no — lease held · **known gap, AI-1617** |

Mapping `clear` to `New` matches Claude's existing behaviour exactly, so this adapter introduces no new
divergence. The acceptance requirement that survives here is narrow: `startup → resume` emits **exactly
once**. The `clear` sequences move to AI-1617.

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

### 3.2-pre The fail-open path must EMIT, not stay silent (round-2 finding 3, in scope)

**This reverses the "zero bytes" decision below, and the review is right to put it in scope rather than
defer it.** §3.1a established that empty stdout makes Gemini parse **stderr** instead. The adapter's
stated guarantee is that the no-fragment / opt-out / budget / failure paths are *inert*. Measurement
proves they are not: those are exactly the paths where `AgentHookPoster` and the auth-lapse notice write
to stderr, so "emit nothing" hands kcap diagnostics — potentially including server URLs and auth state —
to Gemini as model-visible hook output.

The worst case is the one the review named, and it is genuinely bad: **with memory injection opted out, a
failed lifecycle POST can still inject kcap text into the model's context.** A user who disabled the
feature gets kcap prose in their session anyway.

**Decision: on Gemini `SessionStart`, always write a valid JSON hook result.** When there is no memory
fragment for any reason, emit an explicit allow-with-no-context object rather than zero bytes, so stdout
wins the `stdout || stderr` selection and the diagnostics are never parsed as hook output.

* This is a **deliberate divergence** from the other five adapters, which emit nothing on the empty path.
  It is justified by a Gemini-specific runner behaviour, and the reason must be stated at the call site so
  nobody "harmonises" it back later.
* Scope: `SessionStart` only. `SessionEnd` / `Notification` keep today's behaviour and are split to
  **AI-1618** — they are not paths this issue redesigns.
* **Residual, documented:** if the stdout write itself fails wholly or partially (§3.2), stderr can still
  be exposed. Application code cannot prevent that; it is bounded by §3.1 fact (3).

#### The exact wire contract (round-3 finding 3)

The review is right that `{}`, `{"continue":true}`, a null `hookSpecificOutput` and an event-only envelope
are **not** semantically interchangeable on a decision channel, and that `Render` returning `""` means
this needs a new serialization path. Specified:

```jsonc
// no-fragment / opt-out / budget-exhausted / lease-held / provider-failure
{"continue":true}
```

Chosen deliberately over the alternatives:

* **`{"continue":true}`** — an explicit allow. `continue` is read directly by the output object's
  constructor, and carrying **no** `hookSpecificOutput` key at all means `getAdditionalContext()` short-
  circuits on its `"additionalContext" in this.hookSpecificOutput` guard and contributes nothing. No
  `systemMessage`, so nothing is surfaced to the user either.
* **`{}`** — rejected: parses, but asserts nothing. It relies on every default staying benign, which is
  exactly the assumption §3.1 had to go and verify. An explicit allow does not.
* **`{"hookSpecificOutput":{"hookEventName":"SessionStart"}}`** — rejected: an `additionalContext`-less
  `hookSpecificOutput` is a shape whose handling we would have to verify separately, for no benefit.

Requires a new AOT-serializable record (e.g. `GeminiAllowEnvelope([JsonPropertyName("continue")] bool
Continue = true)`) registered in `SessionStartMemoryJsonContext`. **Verify on 0.53.0** that it continues
the turn, contributes no context and no system message, and shadows stderr.

#### Path scope — the invariant covers EVERY recognised SessionStart

The review asked whether the disabled-session and excluded-path fast paths (which `return 0` before
`HandleSessionStart`) are exceptions. **They must not be.** An exception-riddled invariant is not an
invariant, and those paths are not stderr-free in practice — `Program.cs` runs a central spool drain
before dispatch, and any of it can write stderr.

**Invariant: a recognised Gemini `SessionStart` writes exactly one JSON object to stdout on every
returning path** — the memory envelope when there is a fragment, `{"continue":true}` otherwise. That
includes the disabled-session and excluded-path early returns.

Deliberately excluded, because these are not recognised `SessionStart` events at all and Gemini has no
hook result to attribute: unparseable stdin, missing/blank `hook_event_name`, a non-GUID `session_id`, and
any non-`SessionStart` event. Those keep today's silent `return 0`.

**Acceptance:** failed POST × {null fragment, opt-out, provider failure}, plus disabled-session and
excluded-path — each asserting exactly one JSON object on stdout and that kcap diagnostics are **not**
consumed as hook context.

### 3.2 Write mechanics — "zero bytes on any failure" was too strong

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

**CORRECTED after round 2 — the decision is CONDITIONAL, and my asymmetry argument was backwards.**

The review is right that parsing is not consuming. If a downstream `success` gate exists, that is
*semantically identical to discarding stdout* for this feature: the model receives no index while option
(a) has permanently completed the lease. So measurement (4) alone does not settle it.

It also correctly refuted my "resume may never occur" argument, which I had inverted:

* if no resume occurs, **releasing is harmless** — nothing is lost;
* if the session does resume, **releasing is what permits recovery**;
* and conversely, if context *is* consumed on the failed invocation, releasing risks a **duplicate** later.

So the risk is asymmetric in favour of (b), not (a).

**The decision is therefore load-bearing on a test result, not on this document:**

| Real-Gemini non-zero-exit test outcome | Design |
|---|---|
| turn continues **and** the nonce is consumed | option **(a)** — no gate; the injection was delivered |
| turn continues but the nonce is **not** consumed | option **(b)** — gate/release, so a later `resume` recovers |
| turn does not continue | neither: escalate — a failed POST aborting the user's session is its own defect |

**This issue does not ship because the test was added; it ships on what the test returns.** Implementation
should provide the release path behind the gate seam so switching to (b) is a wiring change, not a
redesign.

Supporting (not decisive) evidence for the "consumed" branch: the `getAdditionalContext()` call sites in
the bundle read it after only `shouldStopExecution()` / `isBlockingDecision()` checks, with no visible
`success` gate — but those are the BeforeAgent / AfterTool paths, and the `SessionStart`-specific consumer
was not definitively located. Treat as a prior, not proof.

## 4. Files

**Scope reverted after the AI-1617 split.** Round 2 correctly pointed out that my original two-file claim
was self-contradictory *once the generation rule was in scope*. With that rule moved to AI-1617, the small
scope is honest again — and no lease-key change means **no migration risk to the five merged adapters**,
which is the main reason the split is worth it.

* `src/Capacitor.Cli/Commands/GeminiHookCommand.cs` — orchestrator call, stdout write, budget, `source` →
  lifecycle mapping, and the §3.2-pre always-emit invariant across every recognised `SessionStart` path.
* `src/Capacitor.Cli/Program.cs` — thread `hookProcessStart` into the `--gemini` dispatch.
* `src/Capacitor.Cli/SessionStartMemory/SessionStartMemoryJsonContext.cs` — register the new
  `{"continue":true}` allow record (§3.2-pre). This is an *additive* AOT registration, not a change to any
  existing type, so it cannot alter another harness's output.
* **No lease-store or orchestrator change.** If one becomes necessary, stop: it means AI-1617 scope has
  leaked back in.

## 5. Test plan

Layers, per the project's per-vendor convention:

1. **Foundation** — `Gemini_*` cases in `SessionStartMemoryFoundationTests`: envelope rendering
   (fragment → exact JSON), null fragment → empty string, lease dedupe across two `SessionStart`
   invocations on one session id (the `resume` case). `clear` sequences move to AI-1617.
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
* **CORRECTED (round-2 finding 4): the "benign degradation" rationale over-extrapolated.** The plain-text
  fail-open path, the independent field parsing and the non-blocking defaults were measured **only in
  0.53.0**. An older or future runner need not behave that way, so I cannot claim a wrong guess is safe
  on a version I have not measured.
  * **Below 0.53.0: unsupported.** Stated plainly, rather than dressed up as benign degradation. We still
    emit (a version gate would trade a *known* feature loss for an *unknown* risk), but the behaviour is
    explicitly out of contract and a report from such a version is not a regression against this spec.
  * **Newer/unknown versions: accepted, documented compatibility risk.** The 0.53.0 cert cannot establish
    their semantics. The mitigation is that the cert asserts the version it ran against, so a future
    failure is diagnosable rather than mysterious — not a claim that newer versions are safe.

**Every guard assertion must be mutation-proven.** The Kiro work shipped a vacuous guard test that
passed with the guard removed; the standard here is that deleting the guard fails exactly the intended
test.

### 5.1 The cert is load-bearing for §3

The live cert is the only thing that verifies §3.1 — that a `hookSpecificOutput`-only payload does not
disturb Gemini's decision channel. A green unit suite proves the bytes we emit, not that Gemini accepts
them. If the cert cannot be run, this issue should not be called done.

## 6. Split out of this issue — both filed, both Todo

* **AI-1617 — context-generation lease key, all harnesses.** The `clear` re-injection gap (§2.2). Split
  because it is foundation-wide (Claude has the identical loss and there is no `Clear` reason in the
  contracts), and because the required persisted-key change carries a **re-injection migration risk to all
  five merged adapters** that must not ride along with an adapter PR.
* **AI-1618 — Gemini stderr shadowing for `SessionEnd` / `Notification`** (§3.1a). The `SessionStart` half
  is fixed here (§3.2-pre) because it is on the paths this issue redesigns and carries the opt-out leak;
  the remaining events are theirs.

## 6a. Definition of done (round-3 finding 4)

The conditional §3.3 design is only acceptable if the PR *finalises* it. Required before merge:

1. **Run the live non-zero-exit case and write the result — with the observed `gemini --version` — back
   into this spec.**
2. **Delete the branch that was not selected.** The PR must not merge with both (a) and (b) still
   normative.
3. **Add the branch-specific lease assertion**, not a generic one:
   * if **(a)**: the failed-POST invocation *completes* the lease, and a subsequent `resume` emits nothing.
   * if **(b)**: it *releases* the lease, and a subsequent `resume` retries.
4. **If the turn aborts, stop and escalate** — that is a defect in its own right, not a probe result to
   design around, and it must not be recorded as a passing cert.

## 7. Out of scope

* Gemini hosted agents (AI-899) and the Gemini reviewer (AI-1413) — separate issues, separate epics.
* `SessionEnd` / `Notification` behaviour — untouched.
* Subagent memory injection: Gemini fires no subagent-start hook (`GeminiSubagentTeardown` exists
  precisely because the parent owns teardown), so there is no per-subagent injection point to wire.
