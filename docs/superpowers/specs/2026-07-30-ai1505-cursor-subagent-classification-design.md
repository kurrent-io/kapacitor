# AI-1505 — Cursor subagent classification and SessionStart memory injection

**Status:** rev 3 — in spec review
**Supersedes the premise of:** AI-1461 code-review finding F2
**Repo:** `kcap-cli`

**Scope of every empirical claim below:** `cursor-agent` CLI `2026.07.23-e383d2b` on
macOS. The Cursor IDE Agent Window is **not** covered; §7 makes closing that gap an
acceptance gate rather than a caveat.

## 1. What was asked

AI-1461 shipped the Cursor `sessionStart` memory-index adapter with
`ClassificationAuthoritative: true` and an in-code note accepting a residual risk:
that `CursorLiveSubagentLinker.ResolveParent` can return null for a session that
*is* a subagent (because the parent's `Task`/`Agent` tool_use has not flushed to the
parent's transcript yet), so a misclassified child would get the memory index
injected once.

AI-1505 asked for a **recency/mtime-filtered sibling-transcript signal** so an
uncertain classification could skip injection without regressing genuine top-level
sessions, to be shipped "only if the heuristic's own false-positive rate is
acceptably low."

**This spec does not build that heuristic**, on the tested surface. A controlled probe
shows the harm it would prevent does not occur there. What the probe found instead is a
real defect in the same code, which this spec fixes.

## 2. Method

Four `cursor-agent` runs in a throwaway git workspace, each prompted to delegate, each
confirmed to have spawned a real subagent — runs 1–3 via the `Task` tool
(`taskToolCall` observed in `--output-format stream-json`), run 4 via a named custom
subagent defined at `.cursor/agents/prober.md` (delegation confirmed by the child
returning the workspace file's marker line).

Hooks were wired at **project level** (`<workspace>/.cursor/hooks.json`), which Cursor
merges with the user-level config at higher precedence — so the probe never touched the
developer's global `~/.cursor/hooks.json`. Runs 3–4 wired all 13 documented events. The
hook logged, to a file, each payload plus a snapshot of every
`agent-transcripts/*/*.jsonl` touched in the last 15 minutes (line count, byte size,
mtime, whether a first user text block exists, whether a `Task`/`Agent` tool_use
exists). Hook stdout carried only the JSON response — never tee-wrapped, since a
stdout-consuming hook that blocks fakes an "upstream ignores our output" result.

Also a static scan of the CLI's JS bundles under
`~/.local/share/cursor-agent/versions/2026.07.23-e383d2b/` for the hook-event registry
and the `subagentStart` implementation (F6).

**Artifacts are archived in-repo** at `docs/probes/2026-07-30-cursor-subagent-hooks/`
(`probe_hook.py`, `hooks.json`, `prober.md`, `probe-run3.log`, `probe-run4.log`; home
paths and the user email redacted), so every claim below is auditable from the
revision that makes it, and the probe is re-runnable on a version bump.

## 3. Findings

### F1 — A Cursor subagent child never fires `sessionStart` or `sessionEnd`

Run 3 (all 13 events wired) and run 4 (named custom subagent):

| Run | Session | Role | Hooks fired |
|---|---|---|---|
| 3 | `3c41f3ac` | parent | `sessionStart`, `afterAgentThought`, `preToolUse` |
| 3 | `c20a0f2e` | child | `afterAgentThought`, `preToolUse`, `beforeReadFile`, `postToolUse`, `afterAgentThought` |
| 4 | `f7075537` | parent | `sessionStart`, `afterAgentThought`, `preToolUse`, `afterAgentThought`, `sessionEnd` |
| 4 | `db7f7278` | child | `afterAgentThought`, `preToolUse`, `beforeReadFile`, `postToolUse`, `afterAgentThought` |

Children fire many hooks. Across 4 runs and 2 subagent kinds they never fire
`sessionStart` or `sessionEnd`.

Independently corroborated server-side. All four sessions from runs 1–2 exist as
top-level sessions, and `model` — a field carried **only** on the `sessionStart`
payload — is populated for both parents and NULL for both children:

| session | model | event_count |
|---|---|---|
| `17e20135…` (parent) | `claude-4.5-sonnet-thinking` | 9 |
| `30b68e33…` (child) | NULL | 8 |
| `41841599…` (parent) | `claude-4.5-sonnet-thinking` | 9 |
| `2170e5d0…` (child) | NULL | 8 |

This corroboration comes from a *different* hook installation (the developer's global
`kcap hook --cursor`), so it is not an artifact of the probe's own config.

**Consequence, on the tested surface:** memory injection is reached only from
`sessionStart` (`CursorHookCommand.RunMemoryOrchestrationAsync`, whose sole call site
sits behind `if (eventName != "sessionStart") return null`). A subagent child therefore
cannot receive the memory index under any classification, and AI-1505's premise does
not occur there. This is an inductive result from 4 runs, not a guarantee extracted
from Cursor's source; §7 states what would falsify it.

### F2 — `sessionStart` never carries a `transcript_path`

Every `sessionStart` payload observed has `"transcript_path": null`. The value is
populated on later hooks (`beforeReadFile`, `postToolUse`, `sessionEnd`). This is
structural rather than mode-specific: the directory-state snapshot taken from inside
the `sessionStart` hook shows the session's own transcript directory does not exist
yet.

### F3 — Prompt-hash correlation is unavailable throughout the child's entire hook window

AI-1461 modelled the failure as an eventual-consistency race that a later moment would
resolve. Run 4's snapshots show the parent side never arrives while the child is still
firing hooks. Full timeline (parent `f7075537`, child `db7f7278`):

| Hook (session) | child transcript | parent `Task` flushed? |
|---|---|---|
| `sessionStart` (parent) | — | parent file absent |
| `afterAgentThought` (child) | absent | **no** (1 line, 263 B) |
| `preToolUse` (child) | absent | **no** (1 line, 263 B) |
| `beforeReadFile` (child) | 1 line | **no** (1 line, 263 B) |
| `postToolUse` (child) | 1 line | **no** (1 line, 263 B) |
| `afterAgentThought` (child — last child hook) | 2 lines | **no** (1 line, 263 B) |
| `afterAgentThought` (parent, after child finished) | 4 lines | **yes** (2 lines) |
| `sessionEnd` (parent) | 4 lines | yes (4 lines) |

`CursorSubagentCorrelator` needs both the child's first user prompt hash and the
parent's `Task` prompt hash. The child's own side becomes available partway through
(from `beforeReadFile`); **the parent's side does not appear until after the child's
last hook.** So at every moment the child could act on a classification, the link is
uncomputable.

The precise claim, narrowed from rev 2: correlation is not available **early enough to
gate SessionStart injection, nor at any point during the child's own hook window.** It
is not asserted that both inputs are absent at every instant of the parent's lifetime —
the table shows the parent's `Task` does land before parent `sessionEnd`, which is
exactly why the offline paths (`kcap import --cursor`, the server-side
`CursorSubagentAdoptionSweep`) can correlate successfully.

### F4 — The transcript-derived classification arm has no producer; the marker-driven arm is reachable from persistent state

The only site that writes a link marker is guarded by:

```csharp
} else if (eventName == "sessionStart" && !string.IsNullOrEmpty(transcriptPath)) {
```

`CursorHookCommand.cs:286`. By F1 and F2 both conjuncts fail on the tested surface, so
**`SaveLink` has no producer there** and the `ResolveParent`/`DiscoverSiblingTranscripts`
arm never runs.

**This does not make the whole path statically dead**, and rev 2 overstated it. The
outer block runs `TryLoadLink` for *every* event carrying a session id
(`CursorHookCommand.cs:282–285`); a marker that already exists on disk immediately sets
`isSubagentChild` and reaches the divert (`:303`, `:382–389`). Markers persist across
CLI versions and could in principle have been written by an untested surface or an
older build. The same applies to a persisted `subagent-start` spool entry and the drain
arm at `:359`. So the accurate statement is: **no producer in a fresh installation
under the measured CLI contract** — not "unreachable", and not "no user can have a
populated marker directory".

`CursorLiveSubagentIntegrationTests` passes only because its fixture supplies a
non-null `transcript_path` at `sessionStart` (`:236`), a payload shape the harness
never produces. The suite asserts the behaviour of an arm that has no producer. This is
why AI-1461's review reasoned about the *accuracy* of a classification that was in fact
never being computed.

### F5 — A native mechanism exists upstream

Current Cursor documents `subagentStart`/`subagentStop` hooks carrying an explicit
`parent_conversation_id`, `subagent_id`, `subagent_type`, `task` and `tool_call_id` —
exactly the link `CursorSubagentCorrelator`'s doc comment says Cursor does not provide,
and one that needs no transcript at all. Neither fired in any run. F6 and F7 establish
why, and why we cannot use it yet.

### F6 — `subagentStart` is implemented in the CLI but was not dispatched in the tested version

Scanning the CLI's JS bundles shows `subagentStart` is not merely a documented name:

- `index.js` contains the payload builder (`subagent_id`, `subagent_type`, `task`,
  `parent_conversation_id`, `tool_call_id`, `subagent_model`, `is_parallel_worker`,
  `git_branch`) and the `executeHookForStep(_E.subagentStart, …)` call;
- the response validator accepts `permission` ∈ {`allow`, `deny`, `ask`};
- it appears in the hook-name registry and the `matcher` resolver (which keys
  `subagentStart`/`subagentStop` off `subagent_type`; an absent/empty/`*` matcher
  matches all, so the probe's matcher-less config was not the reason).

**The response type accepts `additional_context`** — `SubagentStartRequestResponse({permission, userMessage, additionalContext: …})` — which the published docs omit. Recorded
because it means a per-subagent injection channel exists should we ever want one. We
deliberately do not want one here: the parent has already been injected, and injecting
a subagent again is the redundancy AI-1461 was trying to avoid.

Run 4 tested the leading hypothesis that dispatch requires a *named* subagent type
(runs 1–3 produced `subagentType: {unspecified: {}}`, whereas the one real pre-existing
pair on disk shows `subagent_type: generalPurpose`). A custom subagent was defined and
invoked by name; delegation genuinely occurred. Still no `subagentStart`/`subagentStop`.

The handler switches on an inbound request from the agent service
(`case:"subagentStart"`), so dispatch is driven remotely. Narrowed claim: **it did not
dispatch in the tested version and cannot be enabled through the tested hooks
configuration.** An inbound handler plus four non-events does not establish that no
IDE, version or feature-gate path dispatches it.

### F7 — Even if Cursor dispatched it, `kcap` could not receive it

Surfaced by spec review; verified in source. `kcap` would drop the event twice over:

- `CursorHooksParser.CursorHookEvents` lists exactly 8 events and excludes
  `subagentStart`/`subagentStop`, so `kcap plugin` install/upgrade never registers them
  in `~/.cursor/hooks.json`.
- Even if a user hand-registered them, `CursorHookEventMap.Map` contains the same 8, so
  `TryResolve` returns false and `HandleCoreInner` returns at
  `CursorHookCommand.cs:232` before any observable handling.

This is a **third** independent reason the linker cannot revive on its own, and it is
the one that would bite whoever tries. It also invalidates rev 2's proposed "tripwire"
test — see D3.

## 4. Decisions

### D1 — Do not build the recency-filtered signal (tested surface); do not claim it is unbuildable

Rev 2 claimed the heuristic was *unbuildable*. That was wrong and is withdrawn. The
requested signal is coarser than prompt-hash correlation: sibling directory mtimes
under `agent-transcripts/` are readable at `sessionStart` via `workspace_roots[0]`, even
though `transcript_path` is null. F3 constrains deterministic correlation, not the
existence of coarse signals. The rejection therefore rests on two other grounds:

1. **Nothing to prevent on the tested surface.** By F1 a child never reaches the
   injection path, so a suppression signal has no harm to avert.
2. **Asymmetric, permanent cost of a false positive.** `sessionStart` fires once per
   Cursor conversation, and of the events we handle only `sessionStart` accepts
   `additional_context`. A wrongly-suppressed session loses its memory index for good,
   whereas the averted harm is one redundant index in a subagent.

Deferred-channel alternatives are considered and rejected rather than ruled out:
`postToolUse` also accepts `additional_context`, so injection *could* in principle be
deferred to a post-tool moment when correlation is possible. Rejected because it would
inject mid-turn rather than into initial context (changing what the adapter means),
would fire per-tool-call and so needs its own dedupe, and buys nothing while (1) holds.
Revisit only if the IDE gate in §7 shows children do reach injection.

This decision is **scoped to `cursor-agent 2026.07.23-e383d2b`.** It is not asserted for
the IDE.

### D2 — Keep the inert live-linking path, documented as inert

The path is **retained, not removed.** F6 shows `subagentStart` is already built into
the CLI and carries an authoritative `parent_conversation_id`. If Cursor turns dispatch
on, some of this code is reusable — specifically the marker store, the marker-driven
divert gate, and the child-transcript routing. Deleting all of it would mean rebuilding
those.

Retention is deliberately *not* justified as "exactly what a native implementation
needs" (rev 2 overstated this). The lifecycle builders and
`HandleSubagentChildEventAsync` are the **wrong shape** for a native world: they emit
`subagent-start` from the child's `sessionStart` and `subagent-stop` from its
`sessionEnd`, neither of which a child fires (F1). A native implementation must trigger
from the *parent's* `subagentStart`/`subagentStop`.

The defect being fixed is therefore not the code's existence; it is that the code
*reads as live* and its tests assert it works in production. Retention carries
obligations:

1. **Every element carries a doc comment** stating it has no producer today, why (the
   two unsatisfiable conjuncts of F4), that a native revival needs a *different
   trigger* (D2 above), and that registration + event-mapping must also change (F7).
2. **Inertness is tested, not merely asserted** (D3).
3. **Stale persistent state has defined behaviour.** Because `TryLoadLink` runs on
   every event (F4), a pre-existing marker still activates the divert. Audit and pin
   what happens for a stale marker and a stale `subagent-start` spool entry rather than
   assuming neither exists.
4. **The reachability condition is single-sourced** — comments point at this spec
   rather than each restating the analysis.

Elements retained and to be annotated:

| Element | Status today |
|---|---|
| `CursorHookCommand.cs:283–301` classification block | Transcript-derived arm has no producer (F4); `TryLoadLink` gate still runs every event |
| `HandleSubagentChildEventAsync` + `isSubagentChild` divert (`:382–389`, `:585+`) | Reachable only from a pre-existing marker; wrong trigger shape for a native revival |
| `CursorLiveSubagentLinker` members | `SaveLink` has no producer; `TryLoadLink` is live |
| `subagent-start` spool-drain arm (`:359`), `MaybeSpawnChildWatcherFromPayloadAsync` (`:756`) | No producer in a fresh install; reachable from a persisted spool entry |
| `CursorMarkers` subagent-ack helpers (`CursorMarkers.cs:146–160`) | Referenced only by the divert |

`CursorSubagentCorrelator` is retained outright — `CursorImportSource.ClassifyAsync`
calls it (`CursorImportSource.cs:291–294`), and per F3 the offline path is the only
place correlation can succeed.

No marker-cleanup migration is proposed, but the "no user has a populated directory"
claim from rev 2 is withdrawn: it rested on one empty machine. D3 covers stale state by
pinning behaviour instead.

### D3 — Rewrite the wrong-architecture tests; drop the vacuous tripwire

- **`CursorLiveSubagentLinkerTests` stays as-is.** It exercises pure functions against
  inputs it constructs itself. Those assertions are honest.
- **The `CursorLiveSubagentIntegrationTests` scenarios are removed or rewritten, not
  relabelled.** Rev 2 proposed a header comment; spec review correctly rejected that.
  The suite asserts child-`sessionStart` → `subagent-start` and child-`sessionEnd` →
  `subagent-stop` (`:19–33`, `:69–85`), which D2 identifies as precisely the wrong
  trigger. A comment cannot convert those assertions into coverage for a native parent
  event; leaving them preserves an obsolete contract and recreates the false
  confidence. Retain only the narrowly useful marker-driven mid-lifecycle, backfill and
  ordering tests, whose value is independent of how the marker was produced.
- **The rev 2 "tripwire" test is dropped.** F7 shows it could not work: a unit test
  feeding a `subagentStart` payload proves nothing about real installations, because
  neither `CursorHooksParser.CursorHookEvents` nor `CursorHookEventMap` contains the
  event, so a real dispatch would be dropped at `CursorHookCommand.cs:232`. Detection is
  handled by the re-probe procedure in §6 instead. Registering and handling the native
  events is explicitly **out of scope** here — it is runtime behaviour we cannot
  exercise end-to-end on any available surface, and shipping unverifiable behaviour is
  the exact failure mode this spec exists to correct.

Add tests pinning the **measured** contract:

1. A realistic `sessionStart` payload carries a null `transcript_path`, and the
   transcript-derived classification arm does not run for it.
2. Memory injection is reached only on `sessionStart`, so
   `ClassificationAuthoritative: true` holds by construction.
3. Stale-state behaviour (D2.3): a pre-existing marker, and a pre-existing
   `subagent-start` spool entry, each have pinned, intentional behaviour.

Each must fail when the behaviour it guards is removed; a pin that passes against a
mutant proves nothing.

### D4 — Correct the in-code note

The note at `CursorHookCommand.cs:538–561` asserts a residual risk that does not occur
on the tested surface and attributes the decision to "no cheap signal exists." Replace
with the measured reason, **explicitly scoped to the tested CLI version**: a Cursor
subagent child never fires `sessionStart`, so this method is unreachable for a child
and `ClassificationAuthoritative: true` is correct by construction. Cite this spec
rather than restating the analysis.

Note: `scripts/check-linear-ids.sh` rejects `AI-<digits>` tokens in `src/**/*.cs` and
`test/**/*.cs`. Reference this spec by path, not by issue id, in code comments.

### D5 — The IDE surface is an acceptance gate, not a caveat

See §7. Until it is run, F1/F2/D1/D4 are stated as scoped to `cursor-agent`, and no
claim is made that AI-1505's premise is falsified globally.

## 5. Testing strategy

- **Unit/contract:** the three pins in D3, alongside the retained Cursor hook
  dispatcher suites.
- **Mutation check:** each new pin must fail when the behaviour it guards is removed.
- **Regression:** the full `Capacitor.Cli.Tests.Unit` suite plus the integration suite.
  Note the known ~42 pre-existing macOS failures (MCP-registration / config-file /
  uninstall tests) — compare against a baseline rather than expecting green.
- **No new live-cert gate.** The change alters no runtime behaviour — it is
  documentation, test correction, and stale-state pinning over a path with no producer.

## 6. The `subagentStart` question — resolved in scope, not deferred

Originally scoped out as a follow-up issue; folded in here. F6 and F7 answer it:
`subagentStart` **did not dispatch in the tested version**, and even if it did, `kcap`
would drop it because the event is in neither `CursorHooksParser.CursorHookEvents` nor
`CursorHookEventMap`. So there is nothing to wire today, and wiring it blind would ship
behaviour no available surface can exercise.

The deliverable is to be ready and to make re-checking cheap:

- D2 keeps the reusable parts as a landing site, with comments recording that a native
  revival needs a different trigger **and** registration/mapping changes (F7).
- F6 records the payload contract (`parent_conversation_id`, `subagent_id`,
  `subagent_type`, `tool_call_id`) and the undocumented `additional_context`
  capability, so a future implementation need not re-derive it from a minified bundle.
- **Re-probe procedure:** the archived harness in
  `docs/probes/2026-07-30-cursor-subagent-hooks/` re-runs in minutes. Run it when
  bumping the supported `cursor-agent` floor, or when Cursor release notes mention
  subagent hooks; a non-empty `subagentStart` line in the log is the signal to
  implement.

## 7. Acceptance gate: the Cursor IDE surface

All findings are `cursor-agent` CLI `2026.07.23-e383d2b`. The IDE Agent Window was not
exercised; driving it is not scriptable from the working environment. The IDE is also
where `subagentStart` most plausibly does fire — the one real pre-existing parent/child
pair on disk carries a named `subagent_type: generalPurpose`, which the CLI never
produced.

This is a **correctness gap, not a documentation gap.** If an IDE subagent child fires
`sessionStart`, then — because `transcript_path` is null there (F2) — the classification
block is skipped, `isSubagentChild` is false, and the child **does** receive the memory
index. That is exactly the harm AI-1505 was filed about, and D1's first ground would not
hold on that surface.

**Gate:** before this spec's conclusions are treated as settled, run the archived probe
against the IDE:

1. Copy `docs/probes/2026-07-30-cursor-subagent-hooks/{probe_hook.py,hooks.json,prober.md}`
   into a throwaway repo (fix the absolute paths in `hooks.json` and the `LOG` constant).
2. Open it in Cursor and, in the Agent Window, ask the agent to delegate to a subagent.
3. Inspect `probe.log` for (a) a `sessionStart` whose `session_id` differs from the
   parent's, and (b) any `subagentStart` line.

Outcomes:

- **No child `sessionStart`, no `subagentStart`:** F1/D1 generalise; drop the scoping
  qualifiers.
- **Child fires `sessionStart`:** D1 must be reopened for the IDE — the requested
  heuristic (or a `conversation_id`/`generation_id` relationship, or
  `is_background_agent`, none of which the CLI runs let us evaluate) becomes relevant,
  and the injected-memory harm is real.
- **`subagentStart` fires:** F7's registration/mapping gap becomes the blocking work,
  and D2's landing site gets used.

Independently of the outcome, F3 constrains only deterministic prompt-hash correlation;
it does not by itself decide the IDE case.
