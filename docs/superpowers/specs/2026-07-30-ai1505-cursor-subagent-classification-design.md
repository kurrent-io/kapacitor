# AI-1505 — Cursor subagent classification and SessionStart memory injection

**Status:** rev 8 — in spec review
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
with home paths and the user email redacted. What this does and does not make
auditable:

| Claim | Auditable from the repo? |
|---|---|
| F1 (runs 3–4), F2, F3 timeline | **Yes** — `probe-run3.log`, `probe-run4.log`, plus the harness (`probe_hook.py`, `hooks.json`, `prober.md`) |
| F6 bundle scan | **Partially** — `bundle-scan.md` carries the exact version string, SHA-256 of each bundle file containing the symbol, the extraction command, and bounded verbatim excerpts. The bundles themselves are a third-party install and are deliberately **not vendored**, so full re-derivation needs that `cursor-agent` version installed. |
| F1 run-1/run-2 server corroboration | **No, not reproducibly** — `server-corroboration.md` records the exact SQL and a transcribed (not verbatim: `role` is an added annotation, all-NULL `repo_hash` omitted) result table, from a point-in-time query against a live tenant rather than a fixture |
| F7 | **Yes** — pure source claim, verifiable in `src/` at this revision |
| D2a machine-state audit | **Commands yes, result no** — `state-audit.md` carries the exact read-only commands (re-runnable anywhere), but the recorded counts are point-in-time on one machine, like the server corroboration |

The harness re-runs in minutes, which is what makes the §6 re-probe procedure cheap.

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

Scanning the CLI's JS bundles (evidence, hashes and extraction command archived at
`docs/probes/2026-07-30-cursor-subagent-hooks/bundle-scan.md`) shows `subagentStart` is
not merely a documented name:

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
   Cursor conversation, and **the kcap dispatcher emits injected context only on
   `sessionStart`** — so within the adapter as built, a suppressed injection has no
   later retry. (Upstream capability is broader: Cursor also accepts
   `additional_context` on `postToolUse`, and per F6 on `subagentStart`. The
   permanence is a property of our implementation, not of Cursor.) A wrongly-suppressed
   session loses its memory index for good, whereas the averted harm is one redundant
   index in a subagent.

Deferred-channel alternatives are considered and rejected rather than ruled out: since
`postToolUse` accepts `additional_context`, injection *could* in principle be deferred
to a post-tool moment when correlation is possible. Rejected because it would inject
mid-turn rather than into initial context (changing what the adapter means), would fire
per-tool-call and so needs its own dedupe, and buys nothing while (1) holds.
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
3. **Stale persistent state has *decided* behaviour** — see D2a. Rev 3 only said each
   state would be "pinned", which would have frozen whatever the code happens to do.
   D2a decides each case first.
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
claim from rev 2 is withdrawn: it rested on one empty machine. D2a decides what happens
if such state does exist.

### D2a — Decided behaviour for stale persistent state

**Three** durable artifacts can outlive a session:

1. the link **marker** (`cursor-subagent-links/<child>`), read by `TryLoadLink`;
2. a spooled **`subagent-start`** entry (`spool/`, in any of the three backlog forms
   `HookSpool` recognises — `<sid>.jsonl`, `<sid>.<pid-seq>.draining`,
   `<sid>.ordered-*`);
3. the **subagent-start ack marker** (`cursor-subagent-start-ack/<child>`,
   `CursorMarkers.SubagentStartAckPath`), which is independently durable and is what
   makes the ack-without-marker state below persist.

All paths are under `${KCAP_CONFIG_DIR:-$HOME/.config/kcap}`. `TryLoadLink` runs on
every event (F4), so these are how the divert stays reachable.

**Precondition — production, not consumption.** Rev 5 said these states "become live
exactly when the arm does". That conflated two different things and is corrected here.

- **Production:** no *new, code-produced* state arises in a fresh installation on the
  tested CLI surface, because the transcript-derived arm is the only thing that attempts
  `SaveLink` or enters the divert, and per F1/F2 it never runs there. (Manual edits and
  externally-created artifacts are outside that statement — the table lists them.)
- **Consumption is independent and is *not* gated.** `TryLoadLink` (`:283`) and
  `DrainAllAsync` (`:345`) run on every event regardless. A marker or spool entry
  written earlier — by another surface, an older build, or a partially-successful
  invocation — is therefore consumed by later `cursor-agent` hooks even though the arm
  never runs on those invocations. F4 already says this; D2a must not contradict it.

So the hazards below are gated by "such state exists", **not** by "the arm runs on the
current surface". They can already be live on a machine with history. The §7 IDE probe
is therefore not the only way to discover them.

**This is an explicit risk acceptance, not an absence of risk.** Deferring the
dual-routing remedy is accepted on the grounds that (a) the state requires an unusual
production path, and (b) a read-only audit of the developer's own machine on 2026-07-30
found zero link markers, zero ack markers, and zero spool backlog in all three forms
HookSpool recognises. The exact commands and their output are archived at
`docs/probes/2026-07-30-cursor-subagent-hooks/state-audit.md` so this ground is
auditable rather than asserted. It is one machine at one moment — weak evidence, and
explicitly not a basis for assuming the population is clean. §7 adds the same read-only
audit as a step so a second data point is collected cheaply; if stale state turns out to
be common, the remedy stops being deferrable.

**Rev 4's write-ordering invariant is WITHDRAWN — it was false on two counts**
(spec review, round 3; both verified in source):

1. `SaveLink` is **best-effort**: it swallows every directory/write failure
   (`CursorLiveSubagentLinker.cs:124–132`). `subagentParentId`/`subagentAgentType` are
   assigned at `CursorHookCommand.cs:291–292` **before** the `SaveLink` call at `:293`,
   so a failed marker write still leaves `isSubagentChild` true and still enters
   `HandleSubagentChildEventAsync`. From there a failed start POST spools a
   `subagent-start` (`:675–686`) and a successful one marks the ack (`:689–692`) —
   **both without a marker**.
2. `MarkSubagentStartAcked` has **two** production callers (`:692` and `:768`), not one.

So spool-without-marker and ack-without-marker are *producible by ordinary failure*,
not only by external deletion. They cannot be asserted unproducible.

| State | Reachable how | Current behaviour | Decision |
|---|---|---|---|
| Marker only, no ack | **Ordinary partial failure** — start spooled after a transient failure, retry hits a permanent 4xx, entry dropped (exactly what `Permanently_dropped_subagent_start_gates_all_child_transcript_delivery_forever`, `CursorWatcherSpawnTests.cs:254–318`, already demonstrates); also a crash after `SaveLink` | Divert active; every non-start hook returns at the `HasSubagentStartAck` gate (`:631–633`) — child's raw events **and** transcript backfill suppressed indefinitely | **Keep fail-closed**, to preserve start-before-content ordering. Accepted cost: that child's live capture is lost until `kcap import --cursor` + the server adoption sweep. |
| Marker + valid spool entry | Normal recovery path | Drain redelivers start-first, marks the ack, converges as designed | **Keep.** Pin the start-before-stop ordering invariant. |
| Spool entry, no marker | `SaveLink` write failure, then a spooled start | On drain (`:345–360`) the callback runs `MaybeSpawnChildWatcherFromPayloadAsync` (`:756–769`), which marks the ack and spawns a `{parent}-{child}` watcher — while the current hook, having missed `TryLoadLink` at `:283`, continues down the **top-level** path (`:382–389` skipped, `:438–464` runs) | **Unsupported corrupt state — NOT benign.** See the dual-routing hazard below. No runtime change in scope; documented as a known risk. |
| Ack, no marker | `SaveLink` write failure, then a **successful** start POST | The *same* invocation marks the ack (`:689–692`), spawns the `{parent}-{child}` watcher (`:697–699`) and backfills the child transcript under the parent (`:703–706`); later invocations miss `TryLoadLink` and run top-level | **Unsupported corrupt state — also dual-routes**, with no spool drain involved. See below. |
| Malformed / truncated marker | Partial write, manual edit | `TryLoadLink` requires ≥2 lines with a non-empty first (`CursorLiveSubagentLinker.cs:113–116`); otherwise null | **Keep fail-open to top-level.** Already the safe direction; pin it. |

**Dual-routing hazard — BOTH `SaveLink`-failure states, not just the spooled one.**
Rev 4 called this benign; rev 5 corrected that but scoped it to spool-without-marker
only. Rev 6 corrects the scope: the root cause is that `SaveLink` failing silently does
not stop the start's side effects, so *whichever* way the start completes, the child can
end up routed twice.

| Path | How the child gets routed under the parent | How it also gets routed top-level |
|---|---|---|
| Start POST **succeeds** (ack, no marker) | Same invocation marks the ack (`:689–692`), spawns the `{parent}-{child}` watcher (`:697–699`) and backfills under the parent (`:703–706`) | Later invocations miss `TryLoadLink` (`:283`) and take the normal top-level path |
| Start POST **fails → spooled** (spool, no marker) | A later drain (`:345–360`) calls `MaybeSpawnChildWatcherFromPayloadAsync` (`:756–769`), which marks the ack and spawns the same watcher | The same invocation, having missed `TryLoadLink`, continues top-level (`:438–464`) |

Either way the same child transcript is ingested both agent-scoped under the parent and
as its own top-level session. That is duplication, not a graceful fallback.

Candidate remedies, all **runtime changes this spec keeps out of scope**:

1. **Prevent the side effects when the marker did not persist** — have `SaveLink` report
   success and fail open to top-level *before* posting the start. This is the only
   remedy that covers the successful-live-start case, since there is no spooled payload
   to repair from after the process exits. It is therefore the most likely correct fix.
2. Restore/validate the marker from the spooled payload before spawning — **spooled case
   only**; cannot repair the ack case.
3. Suppress ack+spawn whenever the marker is absent.
4. Bound and accept the duplication.

Recorded as a known corrupt-state risk under the explicit risk acceptance above — not as
an impossibility. If the §7 IDE probe shows the arm running on that surface, **or** the
audit finds stale marker/spool/ack state in the wild, this becomes real work and should
be fixed before anything depends on live linking.

**On "diagnosable".** The in-code comment at `:625–631` calls the marker-only loss "an
accepted, diagnosable loss", but the handler simply returns at `:631–633` with no log,
metric, or marker surfaced. Rev 4 repeated that word uncritically. Corrected: this spec
accepts **silent** live-capture loss until an offline import, and does not claim a
diagnostic exists. Adding one is a reasonable follow-up but is not proposed here.

The remaining asymmetry is deliberate: a *malformed* marker fails **open** (top-level),
whereas a *well-formed* marker without an ack fails **closed** (suppressed, recoverable
offline). Both are defensible only because the offline path exists — the same reason D1
and F3 lean on it.

### D3 — Rewrite the wrong-architecture tests; drop the vacuous tripwire

- **`CursorLiveSubagentLinkerTests` stays as-is.** It exercises pure functions against
  inputs it constructs itself. Those assertions are honest.
- **The `CursorLiveSubagentIntegrationTests` scenarios are removed or rewritten, not
  relabelled.** Rev 2 proposed a header comment; spec review correctly rejected that.
  The suite asserts child-`sessionStart` → `subagent-start` and child-`sessionEnd` →
  `subagent-stop` (`:19–33`, `:69–85`), which D2 identifies as precisely the wrong
  trigger. A comment cannot convert those assertions into coverage for a native parent
  event; leaving them preserves an obsolete contract and recreates the false
  confidence.

  Rev 3's "retain the mid-lifecycle/backfill/ordering tests" was too broad — spec
  review showed it still admits the obsolete contract. The precise cut:

  | Test shape | Disposition |
  |---|---|
  | Child `sessionStart` → `subagent-start`, child `sessionEnd` → `subagent-stop` (`:19–33`, `:69–85`) | **Remove.** Asserts the wrong trigger. |
  | Stop-ordering behaviour, e.g. `linked_child_subagent_stop_is_not_delivered_ahead_of_a_spooled_subagent_start` (`:107–136`) | **Remove/defer.** Its invariant is real but is expressed through child `sessionEnd`; how `subagentStop` is keyed and spooled is undefined until a native design exists. |
  | Watcher order/ack tests that *generate* the start via child `sessionStart` (`CursorWatcherSpawnTests.cs:102–153`, `190–239`, `254–318`) | **Rewrite** to seed the required persistent state (marker/ack/spool) directly, per D2a, instead of driving it through a child lifecycle hook. |
  | Marker lookup, ack gating, child transcript backfill and watcher self-heal, where setup seeds valid state directly | **Retain.** Value is independent of how the marker was produced. |

  Rule of thumb for the rewrite: a retained test may *depend on* marker/ack state, but
  must not *assert that a child lifecycle hook produces it*.
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
2. **The orchestrator call-site guard**: memory injection is invoked only from the
   `sessionStart` branch. Named for what it actually proves — an *internal* invariant.
   It does **not** prove `ClassificationAuthoritative: true` is warranted; that also
   requires the external fact that a child never receives `sessionStart` (F1), which is
   an empirical vendor contract no unit test can establish.
3. Stale-state behaviour per D2a: marker-only fails closed; malformed marker fails open
   to top-level; marker+spool converges start-first. **Plus the two failure-aware states
   rev 4 wrongly declared unproducible** — `SaveLink` write failure followed by a
   *successful* start POST (ack without marker), and `SaveLink` write failure followed
   by a *spooled* start (spool without marker).

These are **contract pins**, except as noted immediately below.

**Both `SaveLink`-failure tests are characterization tests, not contracts.** Per D2a the
duplicate routing occurs on *both* paths — ack-without-marker and spool-without-marker —
and both are labelled *unsupported*. Their tests therefore record a known bug so the risk
is encoded rather than merely described; they do **not** assert desired behaviour. Both
must be named and commented as such (e.g. `…_currently_dual_routes_known_risk`), and both
are explicitly **excluded** from the mutation rule below: remedy 1 in D2a (fail open to
top-level when the marker did not persist) is *expected* to make both assertions fail, at
which point they must be rewritten or deleted rather than "fixed". What they legitimately
pin is the `SaveLink`-write-failure setup and the fact that both routes occur today.

Every other pin must fail when the behaviour it guards is removed; a pin that passes
against a mutant proves nothing.

### D4 — Correct the in-code note

The note at `CursorHookCommand.cs:538–561` asserts a residual risk that does not occur
on the tested surface and attributes the decision to "no cheap signal exists." Replace
with the measured reason, **explicitly scoped to the tested CLI version**: on
`cursor-agent 2026.07.23-e383d2b` a subagent child never fires `sessionStart`, so this
method is not reached for a child and `ClassificationAuthoritative: true` is **valid
under that measured event contract**.

The phrase "correct by construction" is deliberately **not** used. The source
constructs only "the orchestrator is invoked only from `sessionStart`"; the part that
makes the flag *warranted* is an external, empirically-observed vendor behaviour that a
Cursor update could change. The comment must make that dependency visible rather than
read as a proof, so a future reader knows what to re-check. Cite this spec rather than
restating the analysis.

Note: `scripts/check-linear-ids.sh` rejects `AI-<digits>` tokens in `src/**/*.cs` and
`test/**/*.cs`. Reference this spec by path, not by issue id, in code comments.

### D4a — Correct the two inline comments this spec proves false

D2.1 requires doc comments generally and D4 targets the memory-classification note, but
two *inline* comments make claims that D2a now disproves. Shipping the spec while
leaving them in place would leave the source directly contradicting the design — the
precise failure this issue exists to correct. Both are explicit acceptance criteria:

1. **`CursorLiveSubagentLinker.cs:129–131`** currently says losing the marker "just
   means later hooks for this child fall back to being treated as top-level … healed by
   a later `kcap import --cursor`". That is only true when the write fails *before* any
   start side effect. Because the fields are assigned before `SaveLink`
   (`CursorHookCommand.cs:291–293`), the divert still runs, so a marker loss *followed
   by* a start POST or spool leaves ack/watcher state with no marker and can dual-route.
   The replacement must distinguish those two cases and name the dual-routing outcome.
2. **`CursorHookCommand.cs:625–630`** calls the marker-only/no-ack outcome "an accepted,
   diagnosable loss". D2a establishes it is **silent** — the handler returns at
   `:631–633` with no log, metric or surfaced marker. The replacement must say
   fail-closed **and silent**, with offline import as the recovery path.

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

This is a potential **correctness gap, not a documentation gap** — but the conditional
must be stated carefully, because rev 3 leaked a CLI-only fact into an IDE prediction.
F2 (`transcript_path` null at `sessionStart`) is measured on the CLI **only**. So:

- **If** an IDE child fires `sessionStart` **and** that payload's `transcript_path` is
  null, the classification block is skipped, `isSubagentChild` is false, and the child
  **does** receive the memory index — exactly the harm AI-1505 was filed about, and
  D1's first ground fails on that surface.
- **If** an IDE child fires `sessionStart` **with** a populated `transcript_path`, the
  transcript-derived arm can actually run; it may still fail to correlate (F3's flush
  timing is likely to apply), but the failure mode differs and must be measured, not
  predicted.

The IDE sample must therefore **record `transcript_path`**, not assume it.

**Merge-blocking status:** this gate is **not** pre-merge Definition of Done, and the
change may ship without it. Justification: the shipped change alters no runtime
behaviour, and every claim it makes is scoped to the tested CLI version, so an IDE
result cannot invalidate what is shipped — it can only *widen* (or refuse to widen) the
scope. The gate is a condition on generalising the conclusions, and on closing AI-1505
as "premise falsified" rather than "premise falsified for `cursor-agent`". It needs a
named owner with IDE access; it is not scriptable from CI.

**Procedure:**

1. Recreate the layout in a throwaway git repo at `<WS>` — the destinations matter,
   since Cursor resolves project hooks and named agents by exact path:

   | Archived file | Destination |
   |---|---|
   | `hooks.json` | `<WS>/.cursor/hooks.json` |
   | `prober.md` | `<WS>/.cursor/agents/prober.md` |
   | `probe_hook.py` | `<WS>/probe_hook.py` (any stable absolute path) |
   | — | log written to `<WS>/probe.log` |

   Then edit the absolute paths: every `command` in `hooks.json` must point at the real
   `probe_hook.py` location, and the `LOG` constant inside `probe_hook.py` must point at
   the intended `probe.log`. Add a `NOTE.md` with a distinctive marker line for the
   subagent to read back.
2. Open it in Cursor and, in the Agent Window, delegate to a subagent — once with an
   unnamed `Task` delegation and once with the named `prober` subagent, mirroring the
   CLI matrix.
3. **Before running it**, take the read-only state audit — run the commands in
   `docs/probes/2026-07-30-cursor-subagent-hooks/state-audit.md` verbatim so the counts
   are comparable with the first data point. They cover `cursor-subagent-links/`,
   `spool/` (total and those containing `subagent-start`), and the ack directory
   `cursor-subagent-start-ack/`, all under `${KCAP_CONFIG_DIR:-$HOME/.config/kcap}`, and
   all **recursive** (`find -type f`). This is the second data point for D2a's risk
   acceptance — if stale state is common in the wild, the dual-routing remedy stops being
   deferrable. (Developer machine, 2026-07-30: all four counts zero.)
4. Record, explicitly: the exact Cursor version and composer mode; whether a child
   `sessionStart` fired; whether a child `sessionEnd` fired (F1 covers both, and rev 3's
   gate checked only the former); the child payload's `transcript_path`; whether any
   `subagentStart`/`subagentStop` fired; and the values of `is_background_agent`,
   `conversation_id`, `generation_id` and `session_id` on both parent and child — the
   candidate signals the CLI runs never let us evaluate.

Outcomes:

- **No child `sessionStart` or `sessionEnd`, no native events:** F1/D1 extend to the
  tested IDE version and mode. **Scoping qualifiers are narrowed, not dropped** — one
  IDE run establishes a version/mode/subagent-kind data point, never a
  version-independent Cursor contract. Rev 3 wrongly said the qualifiers could be
  dropped.
- **Child fires `sessionStart`:** reopen D1 for the IDE. The requested heuristic, or a
  `conversation_id`/`generation_id` relationship, or `is_background_agent`, becomes
  relevant, and the injected-memory harm is real. Branch on the recorded
  `transcript_path` per the conditional above.
- **Native `subagentStart`/`subagentStop` fires:** F7's registration/mapping gap becomes
  the blocking work, and D2's landing site gets used.

Independently of the outcome, F3 constrains only deterministic prompt-hash correlation;
it does not by itself decide the IDE case.
