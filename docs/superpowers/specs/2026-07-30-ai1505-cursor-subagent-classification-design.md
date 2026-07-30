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

Three `cursor-agent` runs in a throwaway git workspace at
`/private/tmp/kcap-cursor-subagent-probe-1505`, each prompted to delegate via the
`Task` tool, each confirmed to have spawned a real subagent (`taskToolCall` observed
in `--output-format stream-json`).

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
`cursor-agent 2026.07.23-e383d2b` despite a real subagent running in all three runs.

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

### D2 — Remove the inert live-linking path

Behaviour-preserving: the code provably never executes, so runtime behaviour is
unchanged. Subagent nesting continues to come from `kcap import --cursor` and the
server-side `CursorSubagentAdoptionSweep`, which operate on complete transcripts —
the only conditions under which correlation can work.

Removal surface, with transitive-dead proof:

| Element | Why dead |
|---|---|
| `CursorHookCommand.cs:283–301` classification block | Guard unsatisfiable (F4) |
| `HandleSubagentChildEventAsync` + its `isSubagentChild` divert (`:382–389`, `:585+`) | Only reachable via a marker that is never written |
| `CursorLiveSubagentLinker` (`TryLoadLink`, `SaveLink`, `ResolveParent`, `DiscoverSiblingTranscripts`, `BuildSubagentStartPayload`, `BuildSubagentStopPayload`) | Sole consumer is the block above |
| `subagent-start` spool-drain arm (`:359`) and `MaybeSpawnChildWatcherFromPayloadAsync` (`:756`) | The sole producer of a Cursor `subagent-start` spool entry is `:602`, inside the dead divert |
| `CursorMarkers` subagent-ack helpers (`CursorMarkers.cs:146–160`) | Only referenced by the divert |

`CursorSubagentCorrelator` is **retained** — the import path genuinely uses it.

No data migration is needed: since no marker is ever written, no user has a populated
`~/.config/kcap/cursor-subagent-links/` to clean up.

### D3 — Replace the fiction-feeding tests with contract tests

Delete or rewrite the suites that assert the dead branch
(`CursorLiveSubagentIntegrationTests`, `CursorLiveSubagentLinkerTests`, the
`SaveLink`-based fixtures in `CursorWatcherSpawnTests`, `CursorHookCommandTests`,
`CursorImportSourceTests` — the last uses `SaveLink` purely as setup and does not read
markers in production code).

Add tests pinning the **measured** payload contract, so the dead branch cannot be
rebuilt on the old assumption:

1. A `sessionStart` payload carries a null `transcript_path`; the hook must not
   attempt transcript-derived work from it.
2. Memory injection is reached only on `sessionStart`, and `ClassificationAuthoritative`
   is `true` by construction.

These convert the probe's evidence into a regression guard.

### D4 — Correct the in-code note

The ~20-line note at `CursorHookCommand.cs:538–561` asserts a residual risk that
cannot occur and attributes the decision to "no cheap signal exists." Replace with the
measured reason: a Cursor subagent child never fires `sessionStart`, so this method is
unreachable for a child and `ClassificationAuthoritative: true` is correct by
construction. Cite this spec and the probe recipe rather than restating the analysis.

Note: `scripts/check-linear-ids.sh` rejects `AI-<digits>` tokens in `src/**/*.cs` and
`test/**/*.cs`. Reference this spec by path, not by issue id, in code comments.

## 5. Testing strategy

- **Unit/contract:** the two pins in D3, plus whatever remains of the Cursor hook
  dispatcher suites after the dead-branch tests are removed.
- **Mutation check:** each new pin must fail when the behaviour it guards is removed.
  A test that still passes with the guard deleted proves nothing.
- **Regression:** the full `Capacitor.Cli.Tests.Unit` suite plus the integration
  suite. Note the known ~42 pre-existing macOS failures (MCP-registration /
  config-file / uninstall tests) — compare against a baseline rather than expecting
  green.
- **No new live-cert gate.** This change removes behaviour; there is nothing new for a
  harness to surface.

## 6. Follow-up (separate issue, to be scheduled, not parked)

Adopt Cursor's native `subagentStart` for live subagent nesting (F5). It carries an
explicit `parent_conversation_id` and needs no transcript, so it is the only mechanism
that could ever do live nesting correctly. It did not fire in the CLI, so the first
step is determining where it *is* available (IDE vs CLI, version floor). This is a new
capability rather than the defect fixed here, which is why it is scoped out — but it
should be filed and scheduled, not left implicit.

## 7. Assumptions and residual risk

**All findings are `cursor-agent` CLI 2026.07.23-e383d2b.** The Cursor IDE Agent
Window was not exercised; driving it is not scriptable from the working environment.
The IDE is also the surface where `subagentStart` most plausibly does fire.

If the IDE fires `sessionStart` for subagent children, F1 would not hold there and D2
would need reconsidering — though D1 would still stand, since F3 (no correlatable data
at any live moment) is a property of transcript flush timing, not of hook wiring.

Re-test recipe: restore `.cursor/hooks.json` and `probe_hook.py` from the probe
workspace into an IDE-opened throwaway repo, run a prompt that forces a `Task`
delegation from the Agent Window, and inspect `probe.log` for a `sessionStart` whose
`session_id` differs from the parent's. This is cheap; it is left explicit rather than
silently assumed.
