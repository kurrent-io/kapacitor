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

1. Three-outcome policy per tool call: **allow** / **deny** / **ask**, evaluated identically across
   harnesses.
2. Two layers in one policy: deterministic rules (fast, local, offline-capable) and a server-side
   LLM classifier with user-authored prompts (the judge).
3. Scoped governance: org, team and project policies authored in the app and server-stored; repo
   policy as a PR-reviewable file in the repo.
4. Ask escalation that reaches the human where they are: the vendor-native prompt locally, the
   existing PermissionRequest long-poll lane for hosted sessions.
5. Every non-pass-through decision recorded with provenance, auditable via evals.

## Non-goals

- Rebuilding the vendors' sandbox / command-safety cores. Vendor auto-modes remain the inner layer;
  this policy governs above them.
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
  shell:      command (raw string), segments[] { program, argv } when safely splittable
  file_*:     paths[]
  network:    url or host
  mcp_tool:   server, tool
  other:      raw vendor tool name + payload (the escape hatch — unmapped tools stay governable)
  justification: vendor-provided reason for the call, when the harness carries one
```

**Conservative shell splitting.** Only trivially safe compounds split into segments: top-level
`&&` / `;` / `|` with no command substitution, no `eval`, no nested shell invocation. Anything
murkier stays one opaque segment that patterns must match whole. Unsplittable does not mean
unmatchable — it means only whole-string patterns apply.

## Policy documents

One document shape for all four scopes. Repo scope: `.kcap/approvals.yaml` in the repository,
versioned and PR-reviewable. Org/team/project scopes: server-stored, authored in the app, fetched
at session start and cached on disk (same delivery point as the session-start knowledge fetch).
A user-level local file (`~/.config/kcap/approvals.yaml`) participates in the merge like any other
scope (the merge is order-independent); its judge prompt composes last.

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
```

Matchers are glob patterns over the canonical fields for the rule's `kind`; a matcher may also
qualify by raw vendor tool name (`kind: other`) for tools no normalizer maps yet.

### Merge semantics: tighten-only

Outcomes are ordered by restrictiveness: `allow < ask < deny`. All scopes evaluate independently
against the action (per segment, for split shell commands); the merged outcome is the **most
restrictive matched outcome across all scopes and all segments**. This yields tighten-only
governance with no special cases:

- org deny + repo allow → deny (the wider ceiling holds)
- org allow + repo deny → deny (narrower scopes may tighten)
- no rule matched anywhere → the rules layer yields nothing

A wider scope cannot *guarantee* an allow — a narrower scope may always tighten. That is the chosen
trade-off: central policy is a ceiling, never a floor.

### No match

When no rule matches and the judge is off or yields nothing: **pass-through** — kcap stays silent
and the harness's native behavior (its own auto-mode, allowlists, or prompt) decides. Enabling the
policy never makes a session noisier, and every error path resolves to pass-through (fail-open).

## The judge

A server-side single-shot classifier on kcap-server, consulted only when no deterministic rule
matched and at least one scope enables `judge.mode: unmatched`. Keys and model choice stay
server-side. Because the judge runs only on unmatched actions, it can never override an explicit
rule.

### What the judge receives

The CLI (or daemon) sends only `session_id` + the canonical action. The server assembles the rest —
the transcript never travels on the hook path. Prompt composition, in order:

1. **Base prompt** — versioned, maintained in kcap-server.
2. **Scope prompts appended org → team → project → repo** — user-authored guidance composes, never
   replaces.
3. **Bounded authorization window, ≤ ~2 000 tokens, assembled from the already-recorded session**:
   the current turn's user message, the last few user messages before it, and recent tool-call
   headlines (command/tool + target — no outputs). User messages get the budget; tool evidence is
   titles only. The window is best-effort: recording lags the live session, and the judge must
   produce a verdict from prompts + action alone when the window is empty.
4. **The canonical action**, every string capped (~4 000 tokens), plus the vendor justification
   when present.

The window is framed as **untrusted evidence**: only user-role content can authorize; assistant or
tool text never self-authorizes; elided content is not presumed benign. (Codex's Guardian applies
the same framing; its bounds — 30 k tokens, 40 entries, user-turn anchoring, separate tool-evidence
budget — validate the shape at 15× our size. We stay far smaller because our judge answers in
seconds, not 90.)

### Verdict

```
outcome:            allow | ask | deny | uncertain
rationale:          string
user_authorization: unknown | low | medium | high
```

`uncertain`, timeout, or any transport error → pass-through. Budget: 2 s on local hook paths
(configurable), 10 s on hosted-lane paths where nothing is waiting on a hook timeout. Verdicts are
cached per (canonical action, merged policy version) server-side and per session in the client.

The fail-open direction is deliberate and differs from Codex (whose Guardian fails closed to deny):
our judge sits *above* the harness's own safety layer, so pass-through lands on the vendor's native
behavior, not on "run it".

## Seams

One engine, per-vendor translation. Each adapter declares its seam capability beside its
registration — the `LocalControlCapabilities` pattern: nothing advertised without a live handler —
and the app renders what each vendor actually supports.

### Local interactive sessions

| Vendor | Seam | Outcomes |
| --- | --- | --- |
| Claude | PermissionRequest hook (installed today, decision-capable) | allow / deny at raised prompts |
| Claude | PreToolUse hook (new plugin entry) | allow / deny / **ask**, fires on every call |
| Codex | approval hook | allow / deny at raised prompts; sandboxed auto-runs never reach us |
| Gemini | hook decision | allow / deny |
| others | per the seam spike (#738) | to be verified |

Two Claude seams, one engine, distinct jobs: PermissionRequest auto-answers prompts that would
otherwise interrupt (kills the babysitting); PreToolUse makes wider-scope ceilings binding on calls
the local harness would silently allow — those never raise a prompt, so no PermissionRequest fires.

**Degradation rule:** a seam without native ask degrades **ask → pass-through, never deny**. Deny
is what causes workaround spirals; pass-through defers to native behavior, which at a raised prompt
is the prompt itself. Degraded outcomes are recorded as degraded.

**Hosted-agent guard:** `KCAP_RENDERED_AGENT=1` disables the local hook approval path entirely
(pass-through). Hosted sessions are governed on the daemon path below; the human-in-the-loop lane
cannot be short-circuited from a local hook.

### Hosted sessions

The decision transports all exist; the engine is inserted as a decision source in front of the
human lane at each existing choke point:

- **Hosted Claude** — the PermissionRequest long-poll: evaluate before parking the request; allow
  and deny answer through the same mechanism a human's click uses (deny maps to the agent's reject
  option); ask parks the request exactly as today.
- **Hosted Codex** — in-process app-server approvals: same insertion at the approval-request
  handler.
- **ACP vendors** — `AcpInteractionBridge` is already the single choke point; the engine evaluates
  before the launch presets (`explore`/`edit`), so an org deny beats a preset allow. Presets stay
  untouched in v1; folding them into session-scoped rules is deferred.

Extras on these paths are small and none are transport: payload normalization, resolving a hosted
session's scopes at launch (the daemon knows the workspace), and provenance on recorded decisions.

## Recording and audit

Every non-pass-through decision emits a session event: the outcome, the matched rule + scope (or
judge verdict + rationale + user-authorization estimate), the degradation flag if any, and the
**merged-policy version hash** — so audits know exactly which rules were live. The rules layer is a
deterministic function of (policy hash, action), so recorded decisions replay in evals.
Pass-throughs are counted, not individually recorded.

## Testing

- **Engine**: pure table-driven unit tests in Core.Tests.Unit — matching, tighten-only merge,
  segment aggregation, and every shell-splitter "murky → unsplit" rule pinned.
- **Normalizers**: fixture tests from captured real hook payloads per vendor.
- **Seam adapters**: the existing hook-command pattern — stdin payload in, vendor decision JSON out,
  including the hosted-agent guard and the ask-degradation table.
- **Judge client**: WireMock contract tests, including timeout → pass-through and the
  budget split (2 s hook / 10 s lane).
- **Integration**: one `KcapProcess` spawn covering the Claude PermissionRequest decision path
  end-to-end.

## Phasing

1. **Engine + canonical actions + Claude seams + repo/user rule files.** Local auto-mode ships,
   works offline. Includes the hosted-path insertions for Claude and ACP — cheap once the engine
   exists.
2. **Judge**: kcap-server classifier endpoint, prompt composition, decision recording surface
   (server work tracked in Linear); CLI/daemon calls with fail-open budgets.
3. **Scoped governance**: server-stored org/team/project policies, app authoring UI, session-start
   fetch + cache.
4. **Remaining vendors** per the seam capability matrix, and the hosted Codex insertion.

## Deferred

- Per-scope caps on judge outcomes (e.g. "org allows the judge to at most ask").
- Remote answering of local asks (dual-surface racing needs a design of its own).
- Folding ACP launch presets into session-scoped rules.
- A configurable no-match default per policy (`unmatched: ask` for sensitive repos) — the schema
  admits it later; v1 is pass-through only.
