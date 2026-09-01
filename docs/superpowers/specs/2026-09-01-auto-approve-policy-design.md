# Auto-approve policy: vendor-agnostic three-outcome approvals

**Issue:** [kcap-cli#738](https://github.com/kurrent-io/kcap-cli/issues/738) (supersedes AI-57)
**Status:** draft for review

## Problem

Every supported harness ships some form of auto-mode, but each is a per-vendor, per-machine
mechanism: incompatible configuration dialects, no knowledge of the org → team → project → repo
hierarchy, no central audit, and — critically — a binary allow/deny classifier. A silent deny sends
the agent into workaround spirals, so the human ends up watching the session *more* closely than in
manual mode. The interesting middle — "a human would want to see this one" — has nowhere to go.

Capacitor can provide what no vendor can: one approval policy with identical semantics across all
harnesses, rules and LLM judge prompts authored by the user and scoped to org, team, project or
repo, a third **ask** outcome, and every decision recorded for post-hoc audit.

## Goals

1. Three-outcome policy per tool call: **allow** / **deny** / **ask**, evaluated with one semantics
   everywhere; per-vendor *enforcement coverage* is advertised explicitly (see Seams) and the UI
   never presents coverage a seam has not verified.
2. Two layers in one policy: deterministic rules (fast, local, offline-capable) and a server-side
   LLM classifier with user-authored prompts (the judge).
3. Scoped governance: org, team and project policies authored in the app and server-stored; repo
   policy as a PR-reviewable file in the repo.
4. Ask escalation that reaches the human where they are: the vendor-native prompt locally, the
   existing PermissionRequest long-poll lane for hosted sessions.
5. Every policy decision recorded with provenance and replayable, from the first shipped phase.

## Non-goals

- Rebuilding the vendors' sandbox / command-safety cores. The **vendor-immutable layer** — a
  vendor's own sandbox, safety refusals, and anything it decides before exposing a decision point
  to us — is never overridden: a policy allow cannot resurrect an action that layer refused,
  because we are only consulted at decision points the vendor exposes. (kcap's own ACP launch
  presets are *not* this layer — see Hosted sessions.)
- Remote answering of a *local* terminal's ask. The plumbing would allow it (the blocking lane
  exists), but the terminal prompt and the app would race for the same answer. Local asks are
  answered in the terminal; the app gets a notification only.
- An agentic reviewer (Codex-Guardian style, with shell access and a 90 s budget). The judge is a
  single-shot classifier.
- Binding a hostile *local user*. Client-side enforcement cannot survive a user who removes or
  bypasses kcap; mandatory governance (below) defends against drift, bootstrap gaps and accidents,
  not against the machine's own operator.

## Canonical actions

Every vendor payload is normalized before any policy sees it. The policy engine is vendor-neutral
Core code; normalizers live per vendor in `Harness/<Vendor>/`, extending the existing hook parsers.

```
CanonicalAction
  kind: shell | file_edit | file_read | network | mcp_tool | other
  vendor, session context (repo root, cwd)
  shell:      command (raw string), analyzed: bool, segments[] { program, argv } when analyzed
  file_*:     paths[]
  network:    host (lowercase, punycode-normalized), optional port, raw url
  mcp_tool:   server, tool
  other:      raw vendor tool name + payload (the escape hatch — unmapped tools stay governable)
  justification: vendor-provided reason for the call, when the harness carries one
```

**Normalization never fails open.** A payload the normalizer cannot map, a mapping that throws, or
an action with an empty component set where one is required (a shell action with zero segments, a
file action with no paths) yields `kind: other` with the raw payload — so `other`-kind rules still
apply and a governance deny still binds. There is no path from a normalizer error to a skipped
evaluation, and no vacuously-covered action (see Merge rule).

**Conservative shell analysis — allowlist grammar.** A command is `analyzed` only when every part
of it is on this list, exhaustively:

- simple commands whose every token is a **literal word** — no `$`-anything (parameter, command,
  arithmetic expansion), no backticks, no glob metacharacters, no here-docs, no process
  substitution, no redirection of any kind, no backgrounding, no `eval`/`exec`, no nested shell;
- joined only by top-level `&&`, `;` or `|`.

Everything else — including ordinary redirection (`git status > file` performs a write the argv
does not show) and unexpanded globs (the executed argv would differ from the matched argv) — is
**unanalyzed**: no segments, and normatively **never eligible for allow**. Unanalyzed commands
still match deny and ask rules against their raw string, so unanalyzable syntax cannot evade a
restriction, only forfeit auto-approval.

## Policy documents

One document shape for all five scopes: org, team, project (server-stored, authored in the app,
requiring the corresponding admin/lead role — detailed RBAC lives in the server spec), repo
(`.kcap/approvals.yaml`, versioned and PR-reviewable), and user (`~/.config/kcap/approvals.yaml`).
The rules merge is order-independent; scope order matters only for judge prompt composition
(org → team → project → repo → user).

```yaml
version: 1
rules:
  - match: { kind: shell, command: "git push --force*" }
    outcome: deny
    reason: force-push goes through the PR lane
  - match: { kind: shell, command: ["git status*", "git diff*", "dotnet build*"] }
    outcome: allow
  - match: { kind: mcp_tool, server: "kcap-*" }
    outcome: allow
  - match: { kind: shell, command: "gh pr merge*" }
    outcome: ask
judge:
  mode: unmatched          # off | unmatched
  prompt: |
    Approve routine read-only git and build commands anywhere in the repo.
    Escalate anything touching CI config or release tags to ask.
caps:                      # only meaningful in server-stored scopes
  narrower_widening: on    # off = repo and user scopes cannot widen: no allow rules, no judge
                           # enablement, and their judge prompts are excluded from composition
enforcement: best_effort   # best_effort | required — org scope only; see Failure taxonomy
```

`caps.narrower_widening` defaults to `on`; when any applicable server scope sets `off`, off wins
(tighten-only aggregation). With `off`, repo and user documents contribute **only deny and ask
rules** — their allow rules are ignored, their `judge.mode` cannot enable the judge, and their
prompts are excluded from judge composition — so a hostile branch cannot widen anything, by rule or
by judge steering. When the judge runs, the prompts of **all** scopes not excluded by caps
participate, regardless of which scope enabled it.

Document limits (validated before activation): ≤ 500 rules, ≤ 1 000 tokens of judge prompt, ≤ 32
patterns per matcher. The server refuses to activate an invalid org/team/project document
(validation errors are authoring-time, never session-time).

### Matching semantics (normative)

- Patterns are globs: `*` and `?` only, case-sensitive. An invalid pattern invalidates its document
  (see Failure taxonomy).
- A matcher field that is absent matches anything; a matcher field that is present fails against an
  action where that field is missing.
- `shell`: matching is **token-wise against the segment's argv** (program is token 0), never
  against a joined display string — an argument containing a space can never collide with two
  arguments. A pattern is split into tokens; `*`/`?` match within a token; a trailing bare `*`
  token matches zero or more remaining argv tokens; otherwise token counts must be equal. For
  unanalyzed commands, deny/ask patterns additionally match the raw string; allow patterns never
  match an unanalyzed command.
- `file_*`: paths are resolved absolute against the session cwd and lexically normalized (`.`/`..`
  collapsed; no filesystem access, no symlink resolution — symlink escapes are the vendor sandbox
  layer's concern, not this policy's).
- `network`: patterns match the normalized host; a matcher may pin a port. No redirect reasoning —
  we see the requested URL only.
- **Outcome-sensitive collection**: deny and ask are any-match — one matching segment or path
  suffices. Allow is all-covered — see the merge rule below.

### Merge rule: tighten-only with full coverage

Outcomes are ordered `allow < ask < deny`. All scopes evaluate independently; per action:

1. If any deny rule matches any component (segment or path), the outcome is **deny**.
2. Else if any ask rule matches any component, the outcome is **ask**.
3. Else if the action has a **nonempty** component set and **every executable component** is
   matched by an allow rule — every segment of an analyzed command, every path of a multi-path
   action — the outcome is **allow**. An empty or invalid component set is never allow-eligible
   (it normalizes to `other`).
4. Else the rules layer yields **nothing** (the action proceeds to the judge, or pass-through).

Rule 3 is the coverage requirement: `git status && rm -rf x` with an allow on `git status*` and no
match on the second segment is *unmatched*, not allowed. Partial allow coverage never authorizes.

Consequences: org deny + repo allow → deny; org allow + repo deny → deny; a wider scope cannot
guarantee an allow — central policy is a ceiling, never a floor.

### No match

When no rule decides and the judge is off or yields nothing: **pass-through** — kcap stays silent
and the harness's native behavior (its own auto-mode, allowlists, or prompt) decides. Enabling the
policy never makes a session noisier.

## The policy snapshot

All evaluation — local rules and the server judge — runs against one immutable **policy snapshot**
per session, so a decision's provenance is exact:

- Built at session start (at launch, for hosted sessions): fetch org/team/project documents from
  the server, read the repo and user files, validate, merge. The snapshot id is a content hash over
  every document plus the engine's canonicalization version.
- The client **uploads the content-addressed snapshot for every session that emits policy
  decisions** — judge enabled or not — so evals can reconstruct the full competing rule set, not
  just the matched rule. Identical snapshots are stored once; offline sessions upload the snapshot
  when the event queue drains. The judge composes prompts from exactly this snapshot, never from
  newer server state.
- The session runs on its snapshot until it ends. Mid-session edits — to the repo file, the user
  file, or the server documents — are inert until the next session. This is also the trust
  boundary for the repo file: what becomes live is what was on disk when the session the user
  started began, the same boundary as `.envrc` or committed hooks. A hostile branch can therefore
  put a widening rule or prompt in front of a user who starts a session on it; orgs preclude that
  with `caps.narrower_widening: off`, which removes both widening channels.
- Offline or fetch failure at session start: last-known-good cached server documents are used and
  the snapshot is marked degraded (see Failure taxonomy).

## The judge

A server-side single-shot classifier on kcap-server, consulted only when the rules layer yielded
nothing and a scope permitted to do so (see caps) enables `judge.mode: unmatched`. Keys and model
choice stay server-side.

**The judge is advisory, not a governance ceiling.** Hard limits are deterministic rules — that is
what makes them auditable, replayable, and immune to prompt injection. Scope prompts steer the
judge's judgment; they do not enforce. Two consequences the design accepts and states: a narrower
scope's allow rule (where permitted by caps) prevents the judge from running, and prompt
composition cannot mechanically guarantee that org guidance beats repo guidance — the base prompt
instructs that wider-scope guidance wins on conflict, best-effort. An org that needs a guarantee
writes a rule, or removes narrower widening entirely with caps.

### What the judge receives

The CLI (or daemon) sends `session_id`, the canonical action, the snapshot id, and a **turn
anchor** — the id of the newest user message the local watcher has recorded. The server assembles:

1. **Base prompt** — versioned, maintained in kcap-server.
2. **Scope prompts** from the uploaded snapshot (excluding caps-excluded scopes),
   org → team → project → repo → user, each in a labeled, structurally delimited block.
3. **Bounded authorization window, ≤ 2 000 tokens**, from the recorded session: the anchor user
   message, the last few user messages before it, and recent tool-call headlines (command/tool +
   target, no outputs). The window is built **only if** the anchor is the newest user message in
   the recorded transcript **and** the recording is fresh (latest recorded event within a freshness
   bound of the request); otherwise the judge runs windowless — a stale anchor is not evidence of
   authorization. Role labels come from kcap's own authenticated recording, never from the request.
4. **The canonical action** and vendor justification, each field in its own delimited block.

**Budgets, deterministically allocated in this priority order**: base prompt and the complete
canonical action first (if the action alone cannot fit its 4 000-token per-string / bounded-arrays
budget, it is truncated with a machine-visible flag); then scope prompts, dropping narrowest-first
(user, then repo, …) so wider guidance survives; then the window, dropping oldest-first. Total
prompt ≤ 16 000 tokens; rationale ≤ 1 000 tokens.

**Truncation is mechanically unsafe-capped, not prompt-disciplined**: whenever any action field
carries the truncation flag, the server **clamps the verdict to at most ask** — a truncated action
can never be judge-allowed, by code, regardless of what the model returns.

Every data block — window content, action strings, justification, and the scope prompts themselves
— is framed as **untrusted evidence**: only user-role content from the recording can authorize;
assistant or tool text never self-authorizes; quoted material inside a user message does not
inherit user authority; elided content is not presumed benign. (Codex's Guardian applies the same
framing at 15× our window size; we stay smaller because our judge answers in seconds, not 90.)

### Verdict

```
outcome:            allow | ask | deny | uncertain
rationale:          string (≤ 1 000 tokens)
user_authorization: unknown | low | medium | high
```

Output is validated strictly against this schema; anything malformed is `uncertain`. `uncertain`,
timeout, or transport error → pass-through. Budget: 2 s on local hook paths (configurable), 10 s on
hosted-lane paths where nothing waits on a hook timeout.

**Caching.** A verdict is reusable only for its exact decision function and evidence. Cache key:
`(session, snapshot id, classifier config — base-prompt version + model revision + output-schema
version, canonical action digest, window digest)`. The window digest freezes the evidence: if the
window grows mid-turn, the digest changes and the verdict is recomputed. Windowless verdicts are
not cached. Concurrent requests for the same key coalesce (in-flight deduplication); entries die
with the turn.

The fail-open direction is deliberate and differs from Codex (whose Guardian fails closed to deny):
our judge sits *above* the harness's own safety layer, so pass-through lands on the vendor's native
behavior, not on "run it".

## Failure taxonomy

"Fail-open" applies to **classifier availability**, not to **policy integrity**. The classes:

| Failure | Behavior |
| --- | --- |
| Judge timeout, transport error, `uncertain`, malformed verdict | Pass-through; decision event recorded with the failure class |
| Server documents unreachable at session start | Last-known-good cached documents; snapshot marked degraded; recorded + surfaced to the user |
| No cached server documents at all | See governance modes below |
| Malformed repo or user file | Document ignored; snapshot marked degraded; recorded + surfaced (its denies are lost, so the loss is loud, never silent) |
| Malformed server document | Cannot occur at session time — the server refuses activation at authoring time |
| Unsupported snapshot schema version | Treated as unreachable documents (last-known-good, degraded) |
| Normalizer failure or empty component set | `kind: other` canonical action — evaluation always happens |
| Cache corruption (snapshot cache) | Refetch; else the unreachable-documents row |

**Governance modes.** The org document declares `enforcement: best_effort` (default) or
`required`:

- **best_effort**: with no server documents and no cache (first run, cleared cache), the session
  proceeds on local scopes only, degraded, loudly. The spec states plainly: org ceilings are
  guaranteed only from the first successful org fetch onward.
- **required**: the client persists a required-governance marker the first time it loads such an
  org document. From then on, any session that cannot obtain a fresh or last-known-good org
  snapshot runs in **tighten-only mode**: deny/ask rules from available scopes still apply, but the
  engine emits no allows and the judge is disabled — kcap grants nothing it cannot verify it is
  allowed to grant. A malformed local document under `required` behaves the same as under
  best_effort (loud degrade), since local scopes can only be missing tightening the org can already
  preclude via caps. The marker defends against bootstrap gaps and accidents, not a hostile local
  operator (see Non-goals).

A degraded snapshot never silently drops a deny that *was* loaded, and every degradation is a
recorded, user-visible event.

## Seams

One engine, per-vendor translation. Each adapter declares its seam capability beside its
registration — the `LocalControlCapabilities` pattern: nothing advertised without a live handler —
and the app renders exactly the verified coverage. Seam classes:

- **Pre-decision seam** — consulted before the vendor decides anything (Claude PreToolUse). All
  three outcomes; silence = the vendor's native flow.
- **At-prompt seam** — consulted only when the vendor already decided to prompt (Claude
  PermissionRequest, Codex approval hook, Gemini hook decision, ACP `request_permission`).
  Allow/deny answer the prompt; ask and pass-through both leave the prompt standing (at a prompt,
  pass-through *is* ask).

### Outcome × native disposition

"Native" here is the **vendor-immutable layer** only (sandbox, safety refusals, auto-allow logic).
kcap's own ACP presets are not native — see Hosted sessions.

| Policy outcome | Native would auto-allow | Native would prompt | Native refuses (before or after us) |
| --- | --- | --- | --- |
| allow | runs (pre-decision seam skips the vendor's permission check; vendor's inner safety still applies) | prompt answered allow | refused — never consulted, or our allow cannot resurrect it |
| ask | pre-decision seam forces the prompt; at-prompt seam: prompt stands | prompt stands | refused |
| deny | denied with reason | prompt answered deny (the agent's reject option) | refused |
| pass-through | runs (native behavior) | prompt stands | refused |

**Degradation:** a seam that cannot express an outcome degrades it toward pass-through, never
toward deny. Concretely: ask on a seam with no force-prompt ability (every at-prompt seam when the
vendor auto-allowed — the call never reaches us) is simply unenforceable there, and the capability
matrix says so. The audit event records both the **requested** outcome and the **effective**
behavior, so a degraded ask never reads as if execution actually paused.

### Local interactive sessions

| Vendor | Seam | Class | Verified |
| --- | --- | --- | --- |
| Claude | PermissionRequest hook (installed today, decision-capable) | at-prompt | yes — the rendered path uses it |
| Claude | PreToolUse hook (new plugin entry) | pre-decision | protocol-documented; certify in phase 1 |
| Codex | approval hook | at-prompt (sandboxed auto-runs never reach us) | seam spike |
| Gemini | hook decision | at-prompt | seam spike |
| others | — | — | seam spike (#738) |

### The per-call decision journal (normative)

A local Claude call can traverse PreToolUse and then PermissionRequest; the journal makes the pair
behave as one decision:

- **Call identity**: the vendor's call id when the payload carries one; otherwise
  `(session, turn, input hash, per-turn sequence number)` — two identical calls in one turn get
  distinct sequence numbers, so they never share an entry or collapse into one audit event.
- **Only emitted terminal decisions are journaled.** Entries are consume-once — the first later
  seam that reads an entry consumes it — and expire with the turn.
- **Interactive sessions**: the engine decides at the earliest seam; the entry (allow, deny, or
  ask) is terminal. A journaled **ask is sticky**: the forced prompt's PermissionRequest records
  and forwards but never auto-answers — the point of the ask was that a human decides. The judge
  runs at most once per call, at the earliest seam.
- **Rendered sessions** (`KCAP_RENDERED_AGENT=1`): local seams may only **tighten**. PreToolUse
  deny → terminal entry. PreToolUse ask → sticky entry; the prompt's PermissionRequest forwards to
  the human lane as today. PreToolUse allow or no-decision → **no journal entry, nothing emitted**
  — the call proceeds to the vendor's own flow, and if a prompt is raised, the **daemon path**
  (below) evaluates it: policy allow/deny answer the lane, ask/no-decision park for the human.
  A rendered local seam never emits allow, and an unemitted allow is never journaled, so it can
  neither pre-empt the daemon evaluation nor replay at another seam.

### Hosted sessions

The decision transports all exist; the engine is inserted as a decision source in front of the
human lane at each existing choke point:

- **Hosted Claude** — the PermissionRequest long-poll: evaluate before parking the request; allow
  and deny answer through the same mechanism a human's click uses (deny maps to the agent's reject
  option); ask parks the request exactly as today. Ceiling enforcement on silently-allowed calls
  comes from the rendered tighten-only journal rules above, so no call class escapes both choke
  points.
- **Hosted Codex** — in-process app-server approvals: same insertion at the approval-request
  handler.
- **ACP vendors** — `AcpInteractionBridge` is already the single choke point. The launch presets
  (`explore`/`edit`) are **kcap's own configurable layer, not the vendor-immutable layer**, so
  ordering among kcap layers is a stated design decision: policy deny/ask are terminal (ask parks
  to the lane); a policy **allow deliberately takes precedence over the preset's would-prompt** —
  the merged scoped policy is the more expressive instrument, and the launch owner's own org/user
  scopes shape it; only a policy no-decision falls through to the preset, then to the lane. A
  preset can never widen a policy outcome. The vendor's immutable refusals happen before
  `request_permission` is ever raised and are untouched. Presets stay otherwise unchanged in v1;
  folding them into session-scoped rules is deferred.

Extras on these paths are small and none are transport: payload normalization, building the
session's snapshot at launch (the daemon knows the workspace), and provenance on recorded
decisions.

## Recording and audit

Every engine decision — allow, deny, ask, and every judge consultation including uncertain and
timeout fall-throughs — emits a session event carrying: the canonical action, the snapshot id, the
engine + canonicalization version, the seam, the requested and the effective outcome, the matched
rule + scope (or the judge verdict, rationale, user-authorization estimate, classifier config
versions, and window digest), the degradation flag, the failure class when one applies, and the
journal call identity as correlation id. Because the full snapshot content is persisted for every
decision-emitting session, the rules layer is replayable as a deterministic function of (snapshot,
engine version, action); judge decisions are reconstructable from their recorded config and window
digest. Pure pass-throughs (no rule, no judge) are counted, not individually recorded; judge
fall-throughs are individually recorded because they are the events evals must find.

## Testing

- **Engine**: pure table-driven unit tests in Core.Tests.Unit — token-wise matching, the coverage
  rule (partial allow coverage never authorizes; empty component sets never allow-eligible),
  tighten-only merge, caps aggregation (any off wins), and the allowlist shell grammar pinned
  construct-by-construct, including that redirection, expansion and globs are unanalyzed and that
  unanalyzed commands never match allow.
- **Snapshot**: build/degrade/last-known-good table, both governance modes including the
  required-marker tighten-only session, and the loud-loss rule for malformed files.
- **Normalizers**: fixture tests from captured real hook payloads per vendor, including the
  guaranteed `other` fallback and empty-component routing.
- **Seam adapters**: the existing hook-command pattern — stdin payload in, vendor decision JSON
  out — covering the outcome × native table, the journal state machine (interactive and rendered,
  consume-once, duplicate-call sequencing), and that rendered seams never emit allow.
- **Judge client**: WireMock contract tests — timeout → pass-through, budget split, the full cache
  key (window digest recomputation mid-turn, windowless-uncached, config-version separation),
  in-flight dedup, and the truncation clamp (truncated action can never yield allow).
- **Integration**: one `KcapProcess` spawn covering the Claude PermissionRequest decision path
  end-to-end.

## Phasing

Each phase has an exit gate; a vendor's three-outcome coverage is never advertised before its seam
is verified against the outcome × native table.

1. **Engine + canonical actions + snapshot (local scopes, persisted) + Claude seams + provenance
   events.** Local Claude auto-mode ships, offline-capable, fully audited. Includes the
   hosted-Claude and ACP insertions — cheap once the engine exists. Gate: PreToolUse seam
   certified; coverage rule, shell grammar and journal state machine verified end-to-end.
2. **Judge**: kcap-server classifier endpoint, snapshot upload, prompt composition, turn-anchored
   window, cache and truncation clamp (server work tracked in Linear); CLI/daemon calls with
   fail-open budgets. Gate: windowless, stale-anchor and degraded modes verified; judge events
   visible to evals.
3. **Scoped governance**: server-stored org/team/project policies, authoring UI + roles,
   session-start fetch with last-known-good cache, caps, `enforcement: required` + marker.
   Gate: degraded-snapshot UX and tighten-only-mode session verified.
4. **Remaining vendors** per seam spikes, and the hosted Codex insertion. Gate per vendor: its
   truth-table row verified before the capability matrix advertises it.

## Acceptance criteria

1. Partial allow coverage never authorizes: an action with any unmatched executable component is at
   best unmatched; an empty component set is never allow-eligible.
2. An unanalyzed shell command never matches an allow rule; the analyzed grammar is an exhaustive
   allowlist (literal-token simple commands joined by top-level `&&`/`;`/`|`), so redirection,
   expansion, globs, here-docs, process substitution and backgrounding are all unanalyzed.
3. A wider scope's deny cannot be overridden by any narrower scope, the judge, or a preset.
4. `caps.narrower_widening: off` removes every widening channel of repo/user scopes: allow rules,
   judge enablement, and prompt participation. Any applicable `off` wins.
5. A judge verdict is never reused outside its (session, snapshot, classifier config, action,
   window digest); a truncated action is never judge-allowed.
6. Every degradation is a recorded, user-visible event; no policy-integrity failure silently
   removes a loaded deny; under `enforcement: required`, a session without a valid org snapshot
   emits no allows and runs no judge.
7. A rendered session's local seams can tighten but never allow, and an unemitted rendered
   allow/no-decision leaves no journal entry.
8. Requested and effective outcomes are both recorded whenever they differ.
9. Journal entries are consume-once with per-turn sequence identity: duplicate identical calls
   neither share a sticky decision nor collapse into one audit event.

## Deferred

- Per-scope caps on judge outcomes beyond `narrower_widening` (e.g. "the judge may at most ask").
- Remote answering of local asks (dual-surface racing needs a design of its own).
- Folding ACP launch presets into session-scoped rules.
- A configurable no-match default per policy (`unmatched: ask` for sensitive repos) — the schema
  admits it later; v1 is pass-through only.
- Mid-session policy refresh (sessions run on their start snapshot).
