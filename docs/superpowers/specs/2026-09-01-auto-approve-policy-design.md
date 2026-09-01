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
5. Every policy decision recorded with provenance, auditable via evals, from the first shipped
   phase.

## Non-goals

- Rebuilding the vendors' sandbox / command-safety cores. Vendor auto-modes remain the inner layer;
  this policy governs above them, and a policy allow never resurrects an action the vendor's own
  layer already refused (we are only consulted at decision points the vendor exposes).
- Remote answering of a *local* terminal's ask. The plumbing would allow it (the blocking lane
  exists), but the terminal prompt and the app would race for the same answer. Local asks are
  answered in the terminal; the app gets a notification only.
- An agentic reviewer (Codex-Guardian style, with shell access and a 90 s budget). The judge is a
  single-shot classifier.

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

**Normalization never fails open.** A payload the normalizer cannot map, or a mapping that throws,
yields `kind: other` with the raw tool name and payload — so `other`-kind rules still apply and a
governance deny on `other` still binds. There is no path from a normalizer error to a skipped
evaluation.

**Conservative shell analysis.** A command is `analyzed` only when it is a single simple command or
a compound of simple commands joined by top-level `&&` / `;` / `|`, with no command substitution,
no `eval`, no redirection into an interpreter, and no nested shell invocation. Analyzed commands
carry tokenized segments. Everything else is unanalyzed: it has no segments, and — normatively —
**an unanalyzed command is never eligible for allow** (see Matching). It can still match deny and
ask rules against its raw string, so hiding behind unanalyzable syntax cannot evade a restriction,
only forfeit auto-approval.

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
  narrower_allow: on       # off = repo and user scopes may contribute only ask/deny rules
```

Document limits (validated before activation): ≤ 500 rules, ≤ 1 000 tokens of judge prompt, ≤ 32
patterns per matcher. The server refuses to activate an invalid org/team/project document
(validation errors are authoring-time, never session-time).

### Matching semantics (normative)

- Patterns are globs: `*` and `?` only, case-sensitive, matched against the canonical field for the
  rule's `kind`. An invalid pattern invalidates its document (see Failure taxonomy).
- A matcher field that is absent matches anything; a matcher field that is present fails against an
  action where that field is missing.
- `shell`: patterns match a segment's normalized rendering (program + argv joined by single spaces,
  shell quoting removed) for analyzed commands. For unanalyzed commands, deny/ask patterns
  additionally match the raw string; allow patterns never match an unanalyzed command.
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
3. Else if **every executable component** is matched by an allow rule — every segment of an
   analyzed command, every path of a multi-path action — the outcome is **allow**.
4. Else the rules layer yields **nothing** (the action proceeds to the judge, or pass-through).

Rule 3 is the coverage requirement: `git status && rm -rf x` with an allow on `git status*` and no
match on the second segment is *unmatched*, not allowed. Partial allow coverage never authorizes.

Consequences: org deny + repo allow → deny; org allow + repo deny → deny; a wider scope cannot
guarantee an allow — central policy is a ceiling, never a floor. The `caps.narrower_allow: off`
switch lets a wider scope additionally forbid narrower scopes (repo and user files — the two an
agent or a hostile branch can write) from contributing allow rules at all.

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
- The client **uploads the snapshot** (or server-doc references by version plus the local documents'
  content) when the judge is enabled, so the server composes judge prompts from exactly the
  snapshot the local rules used — never from newer server state.
- The session runs on its snapshot until it ends. Mid-session edits — to the repo file, the user
  file, or the server documents — are inert until the next session. This is also the trust
  boundary for the repo file: what becomes live is what was on disk when the session the user
  started began, the same boundary as `.envrc` or committed hooks. A hostile branch can therefore
  put an allow rule in front of a user who starts a session on it; orgs that need to preclude that
  set `caps.narrower_allow: off`.
- Offline or fetch failure at session start: last-known-good cached server documents are used and
  the snapshot is marked degraded (see Failure taxonomy).

## The judge

A server-side single-shot classifier on kcap-server, consulted only when the rules layer yielded
nothing and at least one scope in the snapshot enables `judge.mode: unmatched`. Keys and model
choice stay server-side.

**The judge is advisory, not a governance ceiling.** Hard limits are deterministic rules — that is
what makes them auditable, replayable, and immune to prompt injection. Scope prompts steer the
judge's judgment; they do not enforce. Two consequences the design accepts and states: a narrower
scope's allow rule (where permitted by `caps`) prevents the judge from running, and prompt
composition cannot mechanically guarantee that org guidance beats repo guidance — the base prompt
instructs that wider-scope guidance wins on conflict, best-effort. An org that needs a guarantee
writes a rule.

### What the judge receives

The CLI (or daemon) sends `session_id`, the canonical action, the snapshot id, and a **turn
anchor** — the id of the newest user message the local watcher has recorded. The server assembles:

1. **Base prompt** — versioned, maintained in kcap-server.
2. **Scope prompts** from the uploaded snapshot, org → team → project → repo → user, each in a
   labeled, structurally delimited block.
3. **Bounded authorization window, ≤ 2 000 tokens**, from the recorded session: the anchor user
   message, the last few user messages before it, and recent tool-call headlines (command/tool +
   target, no outputs). The window is built **only if the recorded transcript contains the turn
   anchor**; otherwise the judge runs windowless. Role labels come from kcap's own authenticated
   recording, never from the request.
4. **The canonical action** and vendor justification, each field in its own delimited block, every
   string capped at 4 000 tokens.

Total prompt is capped at 16 000 tokens; array cardinalities (paths, segments, window entries) are
bounded; the rationale in the verdict is capped at 1 000 tokens. Every data block — window content,
action strings, justification, and the scope prompts themselves — is framed as **untrusted
evidence**: only user-role content from the recording can authorize; assistant or tool text never
self-authorizes; quoted material inside a user message does not inherit user authority; elided
content is not presumed benign. (Codex's Guardian applies the same framing at 15× our window size;
we stay smaller because our judge answers in seconds, not 90.)

### Verdict

```
outcome:            allow | ask | deny | uncertain
rationale:          string (≤ 1 000 tokens)
user_authorization: unknown | low | medium | high
```

Output is validated strictly against this schema; anything malformed is `uncertain`. `uncertain`,
timeout, or transport error → pass-through. Budget: 2 s on local hook paths (configurable), 10 s on
hosted-lane paths where nothing waits on a hook timeout.

**Caching.** A verdict depends on its authorization context, so the cache key is
`(session, turn anchor, snapshot id, canonical action digest)` — never action + policy alone, which
would replay an allow into a turn that never authorized it. Windowless verdicts are not cached.
Concurrent judge requests for the same key coalesce (in-flight deduplication); entries die with the
turn.

The fail-open direction is deliberate and differs from Codex (whose Guardian fails closed to deny):
our judge sits *above* the harness's own safety layer, so pass-through lands on the vendor's native
behavior, not on "run it".

## Failure taxonomy

"Fail-open" applies to **classifier availability**, not to **policy integrity**. The classes:

| Failure | Behavior |
| --- | --- |
| Judge timeout, transport error, `uncertain`, malformed verdict | Pass-through; decision event recorded with the failure class |
| Server documents unreachable at session start | Last-known-good cached documents; snapshot marked degraded; recorded + surfaced to the user |
| No cached server documents at all | Session proceeds with local scopes only; snapshot marked degraded; recorded + surfaced |
| Malformed repo or user file | Document ignored; snapshot marked degraded; recorded + surfaced (its denies are lost, so the loss is loud, never silent) |
| Malformed server document | Cannot occur at session time — the server refuses activation at authoring time |
| Unsupported snapshot schema version | Treated as unreachable documents (last-known-good, degraded) |
| Normalizer failure | `kind: other` canonical action — evaluation always happens |
| Cache corruption (snapshot cache) | Refetch; else the unreachable-documents row |

A degraded snapshot never silently drops an org deny that *was* loaded; degradation only ever means
a scope's document is absent, and every degradation is a recorded, user-visible event.

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

| Policy outcome | Native would auto-allow | Native would prompt | Native already denied |
| --- | --- | --- | --- |
| allow | runs (pre-decision seam skips the vendor's permission check; vendor's inner safety still applies) | prompt answered allow | never consulted — no resurrection |
| ask | pre-decision seam forces the prompt; at-prompt seam: prompt stands | prompt stands | never consulted |
| deny | denied with reason | prompt answered deny (the agent's reject option) | never consulted |
| pass-through | runs (native behavior) | prompt stands | never consulted |

**Degradation:** a seam that cannot express an outcome degrades it toward pass-through, never
toward deny. Concretely: ask on a seam with no force-prompt ability (every at-prompt seam when the
vendor auto-allowed — the call never reaches us) is simply unenforceable there, and the capability
matrix says so. The audit event records both the **requested** outcome and the **effective**
behavior, so a degraded ask never reads as if execution paused.

### Local interactive sessions

| Vendor | Seam | Class | Verified |
| --- | --- | --- | --- |
| Claude | PermissionRequest hook (installed today, decision-capable) | at-prompt | yes — the rendered path uses it |
| Claude | PreToolUse hook (new plugin entry) | pre-decision | protocol-documented; certify in phase 1 |
| Codex | approval hook | at-prompt (sandboxed auto-runs never reach us) | seam spike |
| Gemini | hook decision | at-prompt | seam spike |
| others | — | — | seam spike (#738) |

**One decision per call.** A local Claude call can traverse PreToolUse and then PermissionRequest.
The engine decides at the earliest seam and journals the decision per call (correlated by session +
tool-call identity, with a hash of the tool input as fallback); later seams for the same call read
the journal instead of re-evaluating. An ask issued at PreToolUse is **sticky**: the subsequent
PermissionRequest for that call records and forwards but never auto-answers — the whole point of
the ask was that a human decides. The judge is consulted at most once per call, at the earliest
seam; the journal also deduplicates audit events.

**Rendered-session guard, revised.** For `KCAP_RENDERED_AGENT=1` sessions the local seams may only
**tighten**: PreToolUse may emit deny or ask (governance ceilings bind on every call, including
calls the harness would silently allow — the ask simply forces the prompt, whose PermissionRequest
hook then parks it on the human lane as today), but local seams never emit allow — auto-approval
for hosted sessions happens only on the daemon path below, in front of the lane. The
human-in-the-loop lane can be tightened from a local hook, never short-circuited.

### Hosted sessions

The decision transports all exist; the engine is inserted as a decision source in front of the
human lane at each existing choke point:

- **Hosted Claude** — the PermissionRequest long-poll: evaluate before parking the request; allow
  and deny answer through the same mechanism a human's click uses (deny maps to the agent's reject
  option); ask parks the request exactly as today. Ceiling enforcement on silently-allowed calls
  comes from the PreToolUse tighten-only guard above, so no call class escapes both choke points.
- **Hosted Codex** — in-process app-server approvals: same insertion at the approval-request
  handler.
- **ACP vendors** — `AcpInteractionBridge` is already the single choke point; the engine evaluates
  before the launch presets (`explore`/`edit`): a policy deny or ask is terminal (ask parks to the
  lane), a policy allow answers, and only a policy no-decision falls through to the preset, so an
  org deny beats a preset allow and a preset can never widen a policy outcome. Presets stay
  otherwise untouched in v1; folding them into session-scoped rules is deferred.

Extras on these paths are small and none are transport: payload normalization, building the
session's snapshot at launch (the daemon knows the workspace), and provenance on recorded
decisions.

## Recording and audit

Every engine decision — allow, deny, ask, and every judge consultation including uncertain and
timeout fall-throughs — emits a session event carrying: the canonical action, the snapshot id, the
engine + canonicalization version, the seam, the requested and the effective outcome, the matched
rule + scope (or the judge verdict, rationale and user-authorization estimate), the degradation
flag, the failure class when one applies, and a correlation id from the per-call journal. The rules
layer is a deterministic function of (snapshot, engine version, action), so recorded decisions
replay in evals. Pure pass-throughs (no rule, no judge) are counted, not individually recorded;
judge fall-throughs are individually recorded because they are the events evals must find.

## Testing

- **Engine**: pure table-driven unit tests in Core.Tests.Unit — matching semantics, the coverage
  rule (partial allow coverage never authorizes), tighten-only merge, caps, and every shell-analysis
  "murky → unanalyzed" rule pinned, including that unanalyzed commands never match allow.
- **Snapshot**: build/degrade/last-known-good table, including the loud-loss rule for malformed
  files.
- **Normalizers**: fixture tests from captured real hook payloads per vendor, including the
  guaranteed `other` fallback.
- **Seam adapters**: the existing hook-command pattern — stdin payload in, vendor decision JSON
  out — covering the outcome × native table, the sticky-ask journal, and the rendered tighten-only
  guard.
- **Judge client**: WireMock contract tests — timeout → pass-through, budget split, cache key
  (turn-scoped, windowless-uncached), in-flight dedup.
- **Integration**: one `KcapProcess` spawn covering the Claude PermissionRequest decision path
  end-to-end.

## Phasing

Each phase has an exit gate; a vendor's three-outcome coverage is never advertised before its seam
is verified against the outcome × native table.

1. **Engine + canonical actions + snapshot (local scopes) + Claude seams + provenance events.**
   Local Claude auto-mode ships, offline-capable, fully audited. Includes the hosted-Claude and ACP
   insertions — cheap once the engine exists. Gate: PreToolUse seam certified; coverage rule and
   journal behavior verified end-to-end.
2. **Judge**: kcap-server classifier endpoint, snapshot upload, prompt composition, turn-anchored
   window and cache (server work tracked in Linear); CLI/daemon calls with fail-open budgets.
   Gate: windowless and degraded modes verified; judge events visible to evals.
3. **Scoped governance**: server-stored org/team/project policies, authoring UI + roles,
   session-start fetch with last-known-good cache, `caps`. Gate: degraded-snapshot UX verified.
4. **Remaining vendors** per seam spikes, and the hosted Codex insertion. Gate per vendor: its
   truth-table row verified before the capability matrix advertises it.

## Acceptance criteria

1. Partial allow coverage never authorizes: an action with any unmatched executable component is at
   best unmatched.
2. An unanalyzed shell command never matches an allow rule.
3. A wider scope's deny cannot be overridden by any narrower scope, the judge, or a preset.
4. `caps.narrower_allow: off` removes repo/user allow contribution entirely.
5. A judge verdict is never reused outside the (session, turn, snapshot) it was produced in.
6. Every degradation is a recorded, user-visible event; no policy-integrity failure silently
   removes a loaded deny.
7. A rendered session's local seams can tighten but never allow.
8. Requested and effective outcomes are both recorded whenever they differ.

## Deferred

- Per-scope caps on judge outcomes beyond `narrower_allow` (e.g. "the judge may at most ask").
- Remote answering of local asks (dual-surface racing needs a design of its own).
- Folding ACP launch presets into session-scoped rules.
- A configurable no-match default per policy (`unmatched: ask` for sensitive repos) — the schema
  admits it later; v1 is pass-through only.
- Mid-session policy refresh (sessions run on their start snapshot).
