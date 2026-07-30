# AI-1505 — Cursor subagent classification and SessionStart memory injection

**Status:** design approved 2026-07-30
**Supersedes the premise of:** AI-1461 code-review finding F2
**Repo:** `kcap-cli`

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

**This spec does not build that heuristic.** A controlled probe falsified the
premise. What it found instead is a real defect in the same code, which this spec
fixes.

## 2. Method

Four `cursor-agent` runs in a throwaway git workspace at
`/private/tmp/kcap-cursor-subagent-probe-1505`, each prompted to delegate, each
confirmed to have spawned a real subagent — runs 1–3 via the `Task` tool
(`taskToolCall` observed in `--output-format stream-json`), run 4 via a named custom
subagent defined at `.cursor/agents/prober.md` (delegation confirmed by the child
returning the workspace file's marker line).

Also, a static scan of the CLI's JS bundles under
`~/.local/share/cursor-agent/versions/2026.07.23-e383d2b/` for the hook-event registry
and the `subagentStart` implementation (F6).

Hooks were wired at **project level** (`<workspace>/.cursor/hooks.json`), which
Cursor merges with the user-level config at higher precedence — so the probe never
touched the developer's global `~/.cursor/hooks.json`. Run 3 wired all 13 documented
events. The hook logged, to a file, each payload plus a snapshot of every
`agent-transcripts/*/*.jsonl` touched in the last 15 minutes (line count, byte size,
mtime, whether a first user text block exists, whether a `Task`/`Agent` tool_use
exists). Hook stdout carried only the JSON response — never tee-wrapped, since a
stdout-consuming hook that blocks fakes an "upstream ignores our output" result.

Versions: `cursor-agent 2026.07.23-e383d2b`; Cursor IDE 3.10.20 installed but **not**
exercised (see §7).

Artifacts: `probe.log`, `probe_hook.py`, `.cursor/hooks.json` in the probe workspace.

## 3. Findings

### F1 — A Cursor subagent child never fires `sessionStart` or `sessionEnd`

Run 3, all 13 events wired:

| Session | Role | Hooks fired |
|---|---|---|
| `3c41f3ac` | parent | `sessionStart`, `afterAgentThought`, `preToolUse` |
| `c20a0f2e` | child | `afterAgentThought`, `preToolUse`, `beforeReadFile`, `postToolUse`, `afterAgentThought` |

Children fire many hooks. They never fire `sessionStart` or `sessionEnd`.

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

**Consequence:** memory injection is reached only from `sessionStart`
(`CursorHookCommand.RunMemoryOrchestrationAsync`). A subagent child therefore cannot
receive the memory index under any classification. AI-1505's premise does not occur.

### F2 — `sessionStart` never carries a `transcript_path`

Every `sessionStart` payload observed has `"transcript_path": null`. The value is
populated on later hooks (`beforeReadFile`, `postToolUse`, `sessionEnd`). This is
structural, not mode-specific: the directory-state snapshot taken from inside the
`sessionStart` hook shows the session's own transcript directory does not exist yet.

### F3 — Live prompt-hash correlation is unachievable, not merely unimplemented

AI-1461 modelled the failure as an eventual-consistency race that a later moment
would resolve. The probe shows it is the steady state for the whole live window.

At the child's first `transcript_path`-bearing hooks (`beforeReadFile`, then
`postToolUse`), the disk snapshot shows:

- the **child's own transcript file does not exist**, even though the payload's
  `transcript_path` points at it; and
- the **parent's transcript has `taskToolUse=False`** — 1 line, 293 bytes — its
  `Task` tool_use is not flushed.

`CursorSubagentCorrelator` matches the child's first user prompt hash against a
parent's `Task` prompt hash. Both sides are absent. No hook relocation, no
mtime/recency filter, and no candidate-pool change can recover a link, because the
data does not exist at any live moment. Correlation can only succeed once transcripts
are complete — which is when `kcap import --cursor` and the server-side
`CursorSubagentAdoptionSweep` already run.

### F4 — The live subagent linker is dead code, and its tests encode a fiction

The only site that writes a link marker is guarded by:

```csharp
} else if (eventName == "sessionStart" && !string.IsNullOrEmpty(transcriptPath)) {
```

`CursorHookCommand.cs:286`. By F1 and F2 both conjuncts fail: children never fire
`sessionStart`, and `sessionStart` never carries a `transcript_path`. No marker is
ever written, so `TryLoadLink` always misses and the `HandleSubagentChildEventAsync`
divert is unreachable.

Weakly corroborated: `~/.config/kcap/cursor-subagent-links/` is empty on a machine
with Cursor history. This is only weak support — the linker landed 2026-07-17 and the
only pre-probe `Task` tool_use on that machine dates from 2026-07-02, so an empty
marker dir is also consistent with simply not having run a Cursor subagent since. The
load-bearing evidence is the guard analysis above, not the empty directory.

The divert could not work even if reached: it emits `subagent-start` from the child's
`sessionStart` and `subagent-stop` from its `sessionEnd` — the two events a child
never fires.

`CursorLiveSubagentIntegrationTests` passes only because its fixture supplies a
non-null `transcript_path` at `sessionStart` (`CursorLiveSubagentIntegrationTests.cs:236`),
a payload shape the harness never produces. The suite asserts the behaviour of a
branch that cannot execute in production. This is why AI-1461's review reasoned about
the *accuracy* of a classification that was in fact never being computed.

### F5 — A native mechanism exists upstream but did not fire

Current Cursor documents `subagentStart`/`subagentStop` hooks carrying an explicit
`parent_conversation_id`, `subagent_id`, `subagent_type`, `task` and `tool_call_id` —
exactly the link `CursorSubagentCorrelator`'s doc comment says Cursor does not
provide, and one that needs no transcript at all. Neither fired in
`cursor-agent 2026.07.23-e383d2b` despite a real subagent running in every run. F6
establishes why.

### F6 — `subagentStart` is implemented in the CLI but not dispatched by this version

Scanning the CLI's JS bundles (`~/.local/share/cursor-agent/versions/2026.07.23-e383d2b/`)
shows `subagentStart` is not merely a documented name — it is fully built:

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
pair on disk shows `subagent_type: generalPurpose`). A custom subagent was defined at
`.cursor/agents/prober.md` and invoked by name; delegation genuinely occurred (the
child returned the file's marker line, and the parent reported it). Hooks fired:

| Session | Role | Hooks fired |
|---|---|---|
| `f7075537` | parent | `sessionStart`, `afterAgentThought`, `preToolUse`, `afterAgentThought`, `sessionEnd` |
| `db7f7278` | child | `afterAgentThought`, `preToolUse`, `beforeReadFile`, `postToolUse`, `afterAgentThought` |

Still no `subagentStart`/`subagentStop`. Across 4 runs and 2 subagent kinds, zero.

The hook is driven by an inbound request from the agent service (the handler switches
on `x.request.value` with `case:"subagentStart"`), so dispatch is gated server-side or
behind a newer protocol version — not by anything a client config can enable.
**`subagentStart` therefore cannot be wired today.**

Run 4 also re-confirms F1 on a second subagent kind: the child fired five hooks and
neither `sessionStart` nor `sessionEnd`, while the parent fired both.

## 4. Decisions

### D1 — Do not build the recency-filtered signal

Rejected on evidence, not cost. By F1 the harm it would prevent cannot occur; by F3
the signal it would compute does not exist. Independently, the trade is bad in
principle: `sessionStart` fires once per Cursor conversation and only `sessionStart`
and `postToolUse` accept `additional_context` (`beforeSubmitPrompt` returns
`{continue, user_message?}` only), so a suppressed injection is permanent. Any
heuristic with a nonzero false-positive rate would cost a genuine top-level session
its memory index forever to prevent a bounded, benign redundancy that does not
happen.

### D2 — Keep the inert live-linking path, documented as inert

The path is **retained, not removed.** F6 shows `subagentStart` is already built into
the CLI and carries an authoritative `parent_conversation_id`; only its dispatch is
missing. If Cursor turns dispatch on, this code is the natural landing site — the
marker store, the divert, and the payload builders are exactly what a
`subagentStart`-driven linker needs. Deleting it would mean rebuilding it.

The defect being fixed is therefore **not** the code's existence; it is that the code
*reads as live* and its tests assert it works in production. Retention is only safe if
inertness is explicit and enforced, so this decision carries obligations:

1. **Every element carries a doc comment stating it is currently unreachable**, why
   (the two unsatisfiable conjuncts of F4), and the precise condition that would make
   it reachable (a `subagentStart` dispatch writing the marker). A reader must not be
   able to mistake it for a working feature — that mistake is what produced AI-1505.
2. **Inertness is tested, not merely asserted** (D3). An undocumented, untested inert
   path decays back into the same trap.
3. **The reachability condition is single-sourced** — the doc comments point at this
   spec rather than each restating the analysis, so a future change updates one place.

Elements retained and to be annotated, with the transitive-dead proof that justifies
calling them inert:

| Element | Why currently unreachable |
|---|---|
| `CursorHookCommand.cs:283–301` classification block | Guard unsatisfiable (F4) |
| `HandleSubagentChildEventAsync` + its `isSubagentChild` divert (`:382–389`, `:585+`) | Only reachable via a marker that is never written |
| `CursorLiveSubagentLinker` (`TryLoadLink`, `SaveLink`, `ResolveParent`, `DiscoverSiblingTranscripts`, `BuildSubagentStartPayload`, `BuildSubagentStopPayload`) | Sole consumer is the block above |
| `subagent-start` spool-drain arm (`:359`) and `MaybeSpawnChildWatcherFromPayloadAsync` (`:756`) | The sole producer of a Cursor `subagent-start` spool entry is `:602`, inside the divert |
| `CursorMarkers` subagent-ack helpers (`CursorMarkers.cs:146–160`) | Only referenced by the divert |

A second reason to retain rather than delete: the divert's *shape* is wrong for a
`subagentStart` world (it emits `subagent-start` from the child's `sessionStart` and
`subagent-stop` from its `sessionEnd`, neither of which a child fires — F1). The doc
comments must say so, so that whoever revives it knows the trigger has to move to the
parent's `subagentStart`/`subagentStop`, not just be switched on.

`CursorSubagentCorrelator` is likewise retained — the import path genuinely uses it,
and it is the only place correlation can work at all (F3).

No data migration is needed: since no marker is ever written, no user has a populated
`~/.config/kcap/cursor-subagent-links/` to clean up.

### D3 — Relabel the forward-looking tests; add contract tests that pin reality

Since D2 retains the code, its tests are retained too — but they must stop claiming to
describe production.

- **`CursorLiveSubagentLinkerTests` stays as-is.** It exercises pure functions
  (`ResolveParent`, `DiscoverSiblingTranscripts`, marker round-trip) against inputs it
  constructs itself. Those assertions are honest: they describe the functions, not the
  live wiring.
- **`CursorLiveSubagentIntegrationTests` is relabelled forward-looking.** Its fixture
  supplies a non-null `transcript_path` at `sessionStart` (`:236`), which the harness
  never produces (F2). The suite keeps its coverage value for a future
  `subagentStart`-driven revival, but must carry a header comment stating that its
  payload shape is **synthetic and does not occur in production today**, pointing at
  this spec. Same for the `SaveLink`-based fixtures in `CursorWatcherSpawnTests`,
  `CursorHookCommandTests` and `CursorImportSourceTests` (the last uses `SaveLink`
  purely as setup; no production code reads markers on the import path).

Add tests pinning the **measured** contract, so the inert path cannot be quietly
assumed live again:

1. A realistic `sessionStart` payload carries a null `transcript_path`, and the
   classification block does not run for it.
2. Memory injection is reached only on `sessionStart`, so
   `ClassificationAuthoritative: true` holds by construction.
3. A detection test for F6: if a `subagentStart` payload ever arrives, it must not be
   silently ignored. This is the cheap tripwire that tells us dispatch turned on
   without anyone re-running the probe.

Each of these must fail when the behaviour it guards is removed; a pin that passes
against a mutant proves nothing.

### D4 — Correct the in-code note

The ~20-line note at `CursorHookCommand.cs:538–561` asserts a residual risk that
cannot occur and attributes the decision to "no cheap signal exists." Replace with the
measured reason: a Cursor subagent child never fires `sessionStart`, so this method is
unreachable for a child and `ClassificationAuthoritative: true` is correct by
construction. Cite this spec and the probe recipe rather than restating the analysis.

Note: `scripts/check-linear-ids.sh` rejects `AI-<digits>` tokens in `src/**/*.cs` and
`test/**/*.cs`. Reference this spec by path, not by issue id, in code comments.

## 5. Testing strategy

- **Unit/contract:** the three pins in D3, alongside the existing Cursor hook
  dispatcher suites (retained and relabelled, not removed).
- **Mutation check:** each new pin must fail when the behaviour it guards is removed.
  A test that still passes with the guard deleted proves nothing.
- **Regression:** the full `Capacitor.Cli.Tests.Unit` suite plus the integration
  suite. Note the known ~42 pre-existing macOS failures (MCP-registration /
  config-file / uninstall tests) — compare against a baseline rather than expecting
  green.
- **No new live-cert gate.** This change alters no runtime behaviour at all — it is
  documentation plus tests over a path that never executes — so there is nothing new
  for a harness to surface.

## 6. The `subagentStart` question — resolved in scope, not deferred

This was originally scoped out as a follow-up issue. It is folded in here instead, and
F6 answers it: **`subagentStart` cannot be wired today.** It is fully implemented in
the CLI bundle, but dispatch is driven by an inbound request from the agent service, so
it is gated server-side or behind a newer protocol — nothing a client config can turn
on. Four runs across two subagent kinds produced zero `subagentStart` events.

So the deliverable is not "wire it" but "be ready and notice when it lands":

- D2 keeps the linker as its landing site, with doc comments recording that the trigger
  must move to the parent's `subagentStart`/`subagentStop` (the child fires neither
  `sessionStart` nor `sessionEnd`).
- D3.3 adds the tripwire so a newly-dispatched `subagentStart` is not silently dropped.
- F6 records the payload contract (`parent_conversation_id`, `subagent_id`,
  `subagent_type`, `tool_call_id`) and the `additional_context` capability, so a future
  implementation does not have to re-derive it from a JS bundle.

Nothing here is left for a future issue to discover.

## 7. Assumptions and residual risk

**All findings are `cursor-agent` CLI 2026.07.23-e383d2b.** The Cursor IDE Agent
Window was not exercised; driving it is not scriptable from the working environment.
The IDE is also the surface where `subagentStart` most plausibly does fire — the one
real pre-existing parent/child pair on disk carries a named `subagent_type:
generalPurpose`, which the CLI never produced.

Choosing retention over removal (D2) substantially de-risks this gap. Had the plan
been deletion, an IDE that fires `sessionStart` for children would have meant deleting
a path that was live on another surface. Retention makes the IDE question a
documentation-accuracy question rather than a correctness one: the worst case is that
the "currently unreachable" comments are too absolute for the IDE and need qualifying.

D1 stands regardless of surface. F3 — no correlatable data at any live moment — is a
property of transcript flush timing, not of hook wiring, so the recency heuristic is
unbuildable on the IDE too.

Re-test recipe: restore `.cursor/hooks.json` and `probe_hook.py` from the probe
workspace into an IDE-opened throwaway repo, run a prompt that forces a `Task`
delegation from the Agent Window, and inspect `probe.log` for a `sessionStart` whose
`session_id` differs from the parent's. This is cheap; it is left explicit rather than
silently assumed.
