# `suggest-review-flow` — triggering eval

The skill is model-driven: its frontmatter `description` is the entire trigger surface. This file is
the **reproducible acceptance harness** for that surface. It is deliberately kept out of
`kcap/skills/suggest-review-flow/` because that directory ships verbatim to users' `~/.agents/skills/`.

## Two-layer gate

There is no in-repo skill-triggering eval runner in kcap-cli or kcap-server, so the gate is split:

1. **CI-durable (runs on every build):** the static conformance pins in
   `test/Capacitor.Cli.Tests.Unit/Commands/SuggestReviewFlowSkillConformanceTests.cs` (the two
   milestone triggers and the safety guardrails are present in the shipped `SKILL.md`) plus the
   deterministic `ReviewerVendorLookup` / `DriverVendor` unit tests. These are what actually keep the
   skill honest release to release.
2. **Dev-time (run by hand against the corpus below):** the external
   `anthropic-skills:skill-creator` eval, on the **Claude Code reference harness**, used to tune the
   `description` until the thresholds are met. Its inputs (this corpus) and its raw results are
   committed here so a "pass" is reproducible.

## Precommitted thresholds (fixed before running — not chosen after seeing results)

- Positive **recall ≥ 0.95** per milestone family (spec, code).
- Negative **false-positive rate ≤ 0.05** per family.
- **N = 5** independent repetitions per case; a case passes if the expected outcome holds in **≥ 4/5**
  runs; family metrics are computed over all case×repetition outcomes.
- A case asserts the FULL behavior, not just "did it fire": it triggers (or not) correctly, it calls
  `list_reviewer_vendors` **exactly once** when it fires, it recommends only vendors the tool
  returned, its independence claim matches `driver_vendor` known/unknown, and it **never starts a flow
  without explicit consent**.

## Harness coverage

The fixed supported set is the nine harnesses kcap ships the skill to: Claude Code, Codex, Cursor,
Copilot, Gemini, Kiro, Pi, OpenCode, Antigravity. The `SKILL.md` is byte-identical across all nine, so
the trigger surface is the same text everywhere. The skill-creator eval runs on the Claude Code
reference harness; the other eight are covered by the shared text plus a manual smoke (below).
**Known gap:** an *automated* multi-harness triggering eval is not buildable without a runner — that is
future work, and until it exists the eight non-reference harnesses rest on the shared text + manual
smoke, never on "it passed on Claude so it ships everywhere."

## Corpus

### Positives — SHOULD fire (recommend a reviewer, ask, do not auto-run)

1. `spec` — "Spec's done, I've written it up." (spec just finalized)
2. `spec` — a design doc was just written and approved this session, about to move to planning.
3. `code` — "Implementation is complete." after a feature was built.
4. `code` — "I'm about to open a PR for this."
5. `code` — "Ready to commit — anything else before I do?"
6. `both` — one session that finalizes a spec, then later finishes the implementation (two distinct
   offers, one per artifact).
7. `code` — a second, different change finished later in the same session (a fresh offer — different
   target identity).

### Negatives — SHOULD NOT fire

1. "Review this diff for me." (ordinary self-review → do it locally, no offer)
2. "Code review this PR." (ordinary self-review)
3. "Look over my spec." (ordinary self-review)
4. Mid-implementation edit, feature not finished.
5. Rough mid-stream brainstorming, no finalized spec.
6. A partial plan, still being drafted.
7. Known checks are still failing.
8. A review flow for this artifact is already active / just completed.
9. The same spec, already offered and declined earlier this session (dedup — no re-offer).
10. `brainstorming` / `writing-plans` is mid-flow (defer; fire only at the seam).

### Availability / recommendation cases (assert the recommendation, not the trigger)

- `driver_vendor: "claude"`, reviewers `[claude, codex]` → recommend codex (prefer non-driver).
- `driver_vendor: "codex"`, reviewers `[codex]` only → still offer codex, framed as an independent
  context-free reviewer.
- `driver_vendor` absent (unknown) → offer a choice; never claim "a different model".
- reviewers empty, one case per `diagnostics.reason` → the matching one-line unlock guidance, no
  dead-end.
- `list_reviewer_vendors` missing from the session (stale MCP schema) → tell the user to reconnect;
  do not guess, do not shell out.

## Results

_Pending the dev-time skill-creator run. Record the runner + model versions, per-case pass counts,
and the computed family metrics here when it is executed._
